using System.IO.Abstractions;
using PellyCoder.Core.CharTests;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>The pending-marker check right after the workspace lock (cs/sql runs).</summary>
public sealed class PreflightPhaseTests : IDisposable
{
    private const string CalcSource =
        "namespace Demo;\n\npublic class Calc\n{\n    public int Divide(int a, int b)\n    {\n        return a / b;\n    }\n}\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-preflight-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly ScriptedProcessRunner _processes = new();

    public PreflightPhaseTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Demo"));
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), "");
        File.WriteAllText(CalcPath, CalcSource);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string CalcPath => Path.Combine(_root, "Demo", "Calc.cs");

    private Task<int> RunCs() =>
        FsmOrchestratorFactory.Create(new FileSystem(), _backend, _ui, new PellyOptions(), TimeProvider.System, _processes, null)
            .RunAsync(new RunRequest(CalcPath, null, "Divide", null, "Throw when b is zero.", true, false, null), TestContext.Current.CancellationToken)
            .WaitAsync(TimeSpan.FromSeconds(60), CancellationToken.None);

    [Fact]
    public async Task Preflight_PendingMarker_EnvError()
    {
        var fs = new FileSystem();
        var marker = new CharTestPending(fs).Write(new Workspace(_root, Path.Combine(_root, ".pelly")), "old-run",
            [new PendingFile(Path.Combine(_root, "Demo", "CalcTests.cs"), "test", false, null, "AB", true)]);

        var exit = await RunCs();

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.Equal(0, _backend.StatusCalls);
        Assert.Contains(_ui.Warnings, m => m.Contains("이전 `pelly test` 실행이 끝나지 않았습니다") && m.Contains(marker));
        Assert.Equal(CalcSource, File.ReadAllText(CalcPath));
        // The lock was released: a second run reports the marker again rather than "another run in progress".
        Assert.Equal(4, await RunCs());
        Assert.DoesNotContain(_ui.Warnings, m => m.Contains("진행 중"));
    }

    [Fact]
    public async Task Preflight_NoMarker_Unchanged()
    {
        _backend.Enqueue("<<<<<<< SEARCH\n        return a / b;\n=======\n        if (b == 0) throw new ArgumentException(\"b\");\n        return a / b;\n>>>>>>> REPLACE\n");

        await RunCs();

        // Preflight passed exactly as before: the server was asked, and no pending-marker message appeared.
        Assert.Equal(1, _backend.StatusCalls);
        Assert.NotEmpty(_backend.Requests);
        Assert.DoesNotContain(_ui.Warnings, m => m.Contains("끝나지 않았습니다"));
    }
}
