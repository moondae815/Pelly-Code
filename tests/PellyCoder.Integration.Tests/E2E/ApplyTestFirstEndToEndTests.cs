using System.IO.Abstractions;
using System.Text.Json;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Processes;
using PellyCoder.Core.Specs;
using PellyCoder.Integration.Tests.Corpus;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>
/// <c>pelly apply</c> with an acceptance-criterion test end to end without a model: a canned reply, then the real dotnet
/// restore, build and test runs over a copy of the Modern corpus. The RED test and the spec exist in the copy only.
/// Needs the .NET SDK, so it also runs in CI's unit jobs on macOS and Windows.
/// </summary>
public sealed class ApplyTestFirstEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private const string NewTestName = "OrderServiceTests.CancelAsync_WhenShipped_Throws";
    private const string OldTestName = "OrderServiceTests.CancelAsync_WhenShipped_CurrentlyCancels";
    private const string OldTestAttribute = "        [Fact]\n        public async Task CancelAsync_WhenShipped_CurrentlyCancels()";

    private const string RedTest =
        "        [Fact]\n"
        + "        public async Task CancelAsync_WhenShipped_Throws()\n"
        + "        {\n"
        + "            var repo = new FakeOrderRepository(NewOrder(OrderStatus.Shipped));\n"
        + "            var service = new OrderService(repo, NullLogger<OrderService>.Instance);\n"
        + "\n"
        + "            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(1, \"단순 변심\"));\n"
        + "        }\n"
        + "\n";

    // Response text after the prefilled "<<<<<<< SEARCH\n" marker; the corpus .cs files are LF on every OS (.gitattributes).
    private const string DeliveredGuard =
        "            // 배송 완료된 주문은 반품 절차를 따라야 한다.\n"
        + "            if (order.Status == OrderStatus.Delivered)\n";

    private const string ShippedThrows =
        DeliveredGuard + "=======\n"
        + "            if (order.Status == OrderStatus.Shipped)\n"
        + "            {\n"
        + "                throw new InvalidOperationException(\"배송 중인 주문은 취소할 수 없습니다.\");\n"
        + "            }\n\n"
        + DeliveredGuard + ">>>>>>> REPLACE\n";

    /// <summary>Changes the target but never throws, so the AC test stays red.</summary>
    private const string ShippedOnlyLogs =
        DeliveredGuard + "=======\n"
        + "            if (order.Status == OrderStatus.Shipped)\n"
        + "            {\n"
        + "                _logger.LogWarning(\"배송 중인 주문입니다. OrderId={OrderId}\", orderId);\n"
        + "            }\n\n"
        + DeliveredGuard + ">>>>>>> REPLACE\n";

    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var dir in _temps)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* temp folder, best effort: build servers may still hold files */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task Apply_AcTest_RedThenGreen()
    {
        var (root, file, spec) = await PrepareAsync();

        var (exit, ui, apply) = await ApplyAsync(root, spec, ShippedThrows, maxRetries: null);

        Assert.True(exit == ExitCodes.Success, string.Join("\n", ui.Lines));
        var ac = apply.GetProperty("criteria")[0];
        Assert.Equal("AC1", ac.GetProperty("acId").GetString());
        Assert.Equal("Red", ac.GetProperty("red").GetString());
        Assert.Equal("Met", ac.GetProperty("outcome").GetString());
        Assert.Equal(1, ac.GetProperty("breaksExcluded").GetInt32());
        Assert.True(apply.GetProperty("archived").GetBoolean());
        Assert.False(File.Exists(spec));
        Assert.Contains("InvalidOperationException", File.ReadAllText(file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Apply_AcTest_WrongEdit_NotMet()
    {
        var (root, file, spec) = await PrepareAsync();
        var before = File.ReadAllBytes(file);

        var (exit, ui, apply) = await ApplyAsync(root, spec, ShippedOnlyLogs, maxRetries: 0);

        Assert.True(exit == ExitCodes.VerifyFailed, string.Join("\n", ui.Lines));
        Assert.Equal("NotMet", apply.GetProperty("criteria")[0].GetProperty("outcome").GetString());
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    // ---- plumbing ----

    /// <summary>A restored copy of Modern under a link-free root, with the RED test added and the spec written.</summary>
    private async Task<(string Root, string File, string Spec)> PrepareAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "pelly-atf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var root = CsEndToEndTests.RealPath(temp);
        _temps.Add(root);
        CsEndToEndTests.CopyDirectory(CorpusPaths.Get("Modern"), root);

        var testFile = Path.Combine(root, "Shop.Api.Tests", "OrderServiceTests.cs");
        var text = File.ReadAllText(testFile).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(OldTestAttribute, text, StringComparison.Ordinal);
        File.WriteAllText(testFile, text.Replace(OldTestAttribute, RedTest + OldTestAttribute, StringComparison.Ordinal));

        var spec = Path.Combine(root, "changes", "ship.md");
        Directory.CreateDirectory(Path.GetDirectoryName(spec)!);
        File.WriteAllText(spec,
            "---\nid: ship\nstatus: draft\n---\n# 배송 중 취소 차단\n\n## 수용 기준\n"
            + $"- AC1: 배송 중인 주문은 취소할 수 없다\n  test: {NewTestName}\n  breaks: {OldTestName}\n\n"
            + "## 작업\n"
            + "- [ ] T1 cs Shop.Api/Services/OrderService.cs#OrderService.CancelAsync 배송 중인 주문을 취소하면 InvalidOperationException을 던지도록 수정해줘 (AC1)\n");

        await CsEndToEndTests.RestoreAsync(Path.Combine(root, "Modern.sln")); // pelly builds with --no-restore
        return (root, Path.Combine(root, "Shop.Api", "Services", "OrderService.cs"), spec);
    }

    private async Task<(int Exit, RecordingUserInteraction Ui, JsonElement Apply)> ApplyAsync(
        string root, string spec, string reply, int? maxRetries)
    {
        var fs = new FileSystem();
        var options = Assert.IsType<PellyOptions>(
            PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), new Dictionary<string, string?>()).Options);
        var backend = new CannedLlmBackend([reply]);
        var ui = new RecordingUserInteraction();
        var processes = new SystemProcessRunner(TimeProvider.System);
        var orchestrator = FsmOrchestratorFactory.Create(fs, backend, ui, options, TimeProvider.System, processes);
        var runner = SpecRunnerFactory.Create(
            fs, options, ui, TimeProvider.System, processes, orchestrator.RunDetailedAsync, ImpactFinders.Default(fs));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var exit = await runner.RunAsync(spec, new ApplyOptions(false, false, false, true, false, maxRetries, false), cts.Token);

        foreach (var line in ui.Lines) output.WriteLine(line);
        var runDir = Assert.Single(Directory.GetDirectories(Path.Combine(root, ".pelly", "runs"), "apply-*"));
        var text = File.ReadAllText(Path.Combine(runDir, "apply.json"));
        output.WriteLine(text);
        using var doc = JsonDocument.Parse(text);
        return (exit, ui, doc.RootElement.Clone());
    }
}
