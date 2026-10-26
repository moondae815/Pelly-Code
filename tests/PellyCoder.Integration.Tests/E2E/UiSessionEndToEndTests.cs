using System.IO.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Processes;
using PellyCoder.Integration.Tests.Corpus;
using PellyCoder.Ui.Session;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>
/// The GUI session over the real FSM without a model or a window: a canned reply, the real dotnet restore and build over a
/// copy of the Modern corpus, and <see cref="GuiUserInteraction"/> answering the approval question. Proves the FSM's
/// write and rollback paths are wired through <see cref="UiSession"/> (approve, reject, close while asked).
/// Needs the .NET SDK, like <see cref="TestFirstEndToEndTests"/>.
/// </summary>
public sealed class UiSessionEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromMinutes(10);

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

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    [Fact]
    public async Task Approve_WritesPatchedFile()
    {
        var file = await PrepareAsync();
        var before = File.ReadAllText(file);
        var (session, ui) = NewSession(file);

        var run = await StartAndWaitForApprovalAsync(session, ui);
        ui.Answer(1);
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Dump(ui);

        Assert.Equal(ExitCodes.Success, session.ExitCode);
        var after = File.ReadAllText(file);
        Assert.NotEqual(before, after);
        Assert.Contains("OrderStatus.Shipped)\n            {\n                throw new InvalidOperationException", CsEndToEndTests.Lf(after), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reject_RestoresOriginal()
    {
        var file = await PrepareAsync();
        var before = File.ReadAllBytes(file);
        var (session, ui) = NewSession(file);

        var run = await StartAndWaitForApprovalAsync(session, ui);
        Assert.NotEqual(before, File.ReadAllBytes(file)); // the verified patch is on disk while asking
        ui.Answer(0);
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Dump(ui);

        Assert.Equal(ExitCodes.UserCancelled, session.ExitCode);
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task CloseWhileAwaitingApproval_RestoresOriginal()
    {
        var file = await PrepareAsync();
        var before = File.ReadAllBytes(file);
        var (session, ui) = NewSession(file);
        var closeReady = 0;
        session.CloseReady += () => Interlocked.Increment(ref closeReady);

        var run = await StartAndWaitForApprovalAsync(session, ui);
        Assert.NotEqual(before, File.ReadAllBytes(file)); // the verified patch is on disk while asking
        Assert.False(session.RequestClose());
        await run.WaitAsync(Limit, TestContext.Current.CancellationToken);
        Dump(ui);

        Assert.Equal(1, closeReady);
        Assert.Equal(ExitCodes.UserCancelled, session.ExitCode);
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    // ---- plumbing ----

    /// <summary>A session whose run function is the real FSM with the canned reply, targeting CancelAsync without --yes.</summary>
    private static (UiSession Session, GuiUserInteraction Ui) NewSession(string file)
    {
        var fs = new FileSystem();
        var options = Assert.IsType<PellyOptions>(
            PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), new Dictionary<string, string?>()).Options);
        var backend = new CannedLlmBackend([ShippedThrows]);
        RunFunc run = (request, ui, ct) => FsmOrchestratorFactory
            .Create(fs, backend, ui, options, TimeProvider.System, new SystemProcessRunner(TimeProvider.System))
            .RunAsync(new RunRequest(file, null, "CancelAsync", null, request, Yes: false, DryRun: false, MaxRetries: 0), ct);
        var gui = new GuiUserInteraction(new InlineDispatcher());
        var session = new UiSession(run, gui, [], TestContext.Current.CancellationToken)
        {
            Request = "배송 중인 주문을 취소하면 InvalidOperationException을 던지도록 수정해줘",
        };
        return (session, gui);
    }

    private static async Task<Task> StartAndWaitForApprovalAsync(UiSession session, GuiUserInteraction ui)
    {
        var run = session.RunAsync();
        var deadline = DateTime.UtcNow + Limit;
        while (!(ui.Question is { Kind: QuestionKind.Approve } && session.State == SessionState.AwaitingAnswer))
        {
            if (run.IsCompleted)
                Assert.Fail("run ended before the approval question: exit " + session.ExitCode + "\n"
                            + string.Join("\n", ui.Lines.Select(l => l.Text)));
            if (DateTime.UtcNow > deadline) throw new TimeoutException("approval question not reached");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        Assert.NotNull(ui.DiffLines);
        return run;
    }

    /// <summary>A restored copy of Modern under a link-free root (pelly builds with --no-restore).</summary>
    private async Task<string> PrepareAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "pelly-ui-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var root = CsEndToEndTests.RealPath(temp);
        _temps.Add(root);
        CsEndToEndTests.CopyDirectory(CorpusPaths.Get("Modern"), root);
        await CsEndToEndTests.RestoreAsync(Path.Combine(root, "Modern.sln"));
        return Path.Combine(root, "Shop.Api", "Services", "OrderService.cs");
    }

    private void Dump(GuiUserInteraction ui)
    {
        foreach (var line in ui.Lines) output.WriteLine($"{line.Kind} {line.Phase} {line.Attempt}: {line.Text}");
    }
}
