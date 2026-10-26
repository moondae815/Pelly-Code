using System.IO.Abstractions;
using System.Text.Json;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Processes;
using PellyCoder.Integration.Tests.Corpus;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>
/// <c>--verify-test</c> end to end without a model: a canned reply, then the real dotnet restore, build and test runs over a
/// copy of the Modern corpus (xunit v3 on Microsoft.Testing.Platform). The new RED test is written into the copy only.
/// Needs the .NET SDK, so it also runs in CI's unit jobs on macOS and Windows.
/// </summary>
public sealed class TestFirstEndToEndTests(ITestOutputHelper output) : IDisposable
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

    /// <summary>Changes the target but never throws, so the RED test stays red.</summary>
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
    public async Task ShippedCancel_RedThenGreen()
    {
        var (root, file) = await PrepareAsync();
        var before = File.ReadAllText(file);

        var (exit, ui, run) = await RunAsync(root, file, ShippedThrows, maxRetries: null);

        Assert.True(exit == ExitCodes.Success, string.Join("\n", ui.Lines));
        Assert.NotEqual(before, File.ReadAllText(file));
        var testFirst = run.GetProperty("testFirst");
        Assert.Equal("Red", testFirst.GetProperty("red").GetString());
        Assert.Equal("Passed", testFirst.GetProperty("green").GetString());
        Assert.Equal(1, testFirst.GetProperty("breaksExcluded").GetInt32());
        Assert.Equal(JsonValueKind.Null, run.GetProperty("failureKind").ValueKind);
    }

    [Fact]
    public async Task WrongEdit_GreenFails_Exit1()
    {
        var (root, file) = await PrepareAsync();
        var before = File.ReadAllBytes(file);

        var (exit, ui, run) = await RunAsync(root, file, ShippedOnlyLogs, maxRetries: 0);

        Assert.True(exit == ExitCodes.VerifyFailed, string.Join("\n", ui.Lines));
        Assert.Equal("TestGreenFailed", run.GetProperty("failureKind").GetString());
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    // ---- plumbing ----

    /// <summary>A restored copy of Modern under a link-free root, with the RED test added to the copy's test file.</summary>
    private async Task<(string Root, string File)> PrepareAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "pelly-tf-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var root = CsEndToEndTests.RealPath(temp);
        _temps.Add(root);
        CsEndToEndTests.CopyDirectory(CorpusPaths.Get("Modern"), root);

        var testFile = Path.Combine(root, "Shop.Api.Tests", "OrderServiceTests.cs");
        var text = File.ReadAllText(testFile).Replace("\r\n", "\n", StringComparison.Ordinal);
        Assert.Contains(OldTestAttribute, text, StringComparison.Ordinal);
        File.WriteAllText(testFile, text.Replace(OldTestAttribute, RedTest + OldTestAttribute, StringComparison.Ordinal));

        await CsEndToEndTests.RestoreAsync(Path.Combine(root, "Modern.sln")); // pelly builds with --no-restore
        return (root, Path.Combine(root, "Shop.Api", "Services", "OrderService.cs"));
    }

    private async Task<(int Exit, RecordingUserInteraction Ui, JsonElement Summary)> RunAsync(
        string root, string file, string reply, int? maxRetries)
    {
        var fs = new FileSystem();
        var options = Assert.IsType<PellyOptions>(
            PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), new Dictionary<string, string?>()).Options);
        var backend = new CannedLlmBackend([reply]);
        var ui = new RecordingUserInteraction();
        var orchestrator = FsmOrchestratorFactory.Create(fs, backend, ui, options, TimeProvider.System, new SystemProcessRunner(TimeProvider.System));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));
        var exit = await orchestrator.RunAsync(
            new RunRequest(file, null, "CancelAsync", null, "배송 중인 주문을 취소하면 InvalidOperationException을 던지도록 수정해줘",
                Yes: true, DryRun: false, MaxRetries: maxRetries, VerifyTests: [NewTestName], ExpectBreaks: [OldTestName]),
            cts.Token);

        foreach (var line in ui.Lines) output.WriteLine(line);
        var runDir = Assert.Single(Directory.GetDirectories(Path.Combine(root, ".pelly", "runs")));
        var summaryText = File.ReadAllText(Path.Combine(runDir, "summary.json"));
        output.WriteLine(summaryText);
        using var doc = JsonDocument.Parse(summaryText);
        return (exit, ui, doc.RootElement.Clone());
    }
}
