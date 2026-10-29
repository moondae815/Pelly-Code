using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.CharTests;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Processes;
using PellyCoder.Integration.Tests.Corpus;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>
/// <c>pelly test</c> with the real model against a restored copy of the Modern corpus. The model may or may not get its tests
/// through every verdict (exit 0 or 1); what must hold either way is that the production file is untouched, no pending marker is
/// left, and kept tests were all killed by the mutation. Needs Ollama (<c>PELLY_TEST_OLLAMA</c>, <c>PELLY_TEST_MODEL</c>).
/// </summary>
public sealed class CharTestEndToEndTests(ITestOutputHelper output) : IDisposable
{
    private readonly List<string> _temps = [];

    public void Dispose()
    {
        foreach (var dir in _temps)
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (IOException) { /* temp folder, best effort */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    [OllamaFact]
    public async Task Modern_RealModel_CalculateRefund()
    {
        var root = await PrepareModernAsync();
        var target = Path.Combine(root, "Shop.Api", "Services", "OrderService.cs");
        var testFile = Path.Combine(root, "Shop.Api.Tests", "OrderServiceTests.cs");
        var before = File.ReadAllBytes(target);
        var testsBefore = File.ReadAllBytes(testFile);

        var (exit, ui) = await RunAsync(target, "CalculateRefundAsync");
        var run = DumpRun(root, ui);

        Assert.True(exit is ExitCodes.Success or ExitCodes.VerifyFailed, $"exit {exit}");
        Assert.Equal(before, File.ReadAllBytes(target));
        Assert.False(File.Exists(Path.Combine(run, CharTestPending.FileName)), "pending marker left in " + run);
        var charTest = JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "summary.json"))).RootElement.GetProperty("charTest");
        if (exit == ExitCodes.Success)
        {
            Assert.True(charTest.GetProperty("kept").GetInt32() > 0);
            Assert.Equal(charTest.GetProperty("kept").GetInt32(), charTest.GetProperty("killedByMutation").GetInt32());
        }
        else
        {
            Assert.Equal(testsBefore, File.ReadAllBytes(testFile));
        }
    }

    // ---- run plumbing (the same steps as M3aEndToEndTests) ----

    private async Task<string> PrepareModernAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "pelly-chartest-e2e-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(temp);
        var root = CsEndToEndTests.RealPath(temp);
        _temps.Add(root);
        CsEndToEndTests.CopyDirectory(CorpusPaths.Get("Modern"), root);
        Assert.False(File.Exists(Path.Combine(root, "global.json")));
        var restore = await new SystemProcessRunner(TimeProvider.System).RunAsync(
            new ProcessSpec("dotnet", ["restore", "Modern.sln", "--nologo", "--disable-parallel"], root, TimeSpan.FromMinutes(5)),
            TestContext.Current.CancellationToken);
        Assert.True(restore.ExitCode == 0, "dotnet restore 실패:\n" + restore.StdOut + restore.StdErr);
        return root;
    }

    private static async Task<(int Exit, RecordingUserInteraction Ui)> RunAsync(string file, string method)
    {
        var fs = new FileSystem();
        var overrides = new Dictionary<string, string?>
        {
            ["llm:endpoint"] = TestEnv.OllamaEndpoint,
            ["llm:model"] = TestEnv.OllamaModel,
        };
        var loaded = PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), overrides);
        var options = Assert.IsType<PellyOptions>(loaded.Options);

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var backend = new OllamaBackend(http, options.Llm, TimeProvider.System);
        var ui = new RecordingUserInteraction();
        var orchestrator = FsmOrchestratorFactory.CreateCharTest(fs, backend, ui, options, TimeProvider.System, new SystemProcessRunner(TimeProvider.System));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(30));
        var exit = await orchestrator.RunAsync(
            new CharTestRequest(file, null, method, null, null, CharTestLimits.DefaultCount, null, Yes: true, DryRun: false, MaxRetries: null, Verbose: false),
            cts.Token);
        return (exit, ui);
    }

    /// <summary>Writes the run's lines and summary to the test output and returns the newest run folder (kept under
    /// <c>PELLY_TEST_KEEP</c> when set).</summary>
    private string DumpRun(string root, RecordingUserInteraction ui, [CallerMemberName] string test = "")
    {
        foreach (var line in ui.Lines) output.WriteLine(line);
        var runs = Path.Combine(root, ".pelly", "runs");
        Assert.True(Directory.Exists(runs), "no run folder: " + runs);
        var run = Directory.GetDirectories(runs).Order(StringComparer.Ordinal).Last();
        var summary = Path.Combine(run, "summary.json");
        if (File.Exists(summary)) output.WriteLine(File.ReadAllText(summary));
        var keep = Environment.GetEnvironmentVariable("PELLY_TEST_KEEP");
        if (!string.IsNullOrEmpty(keep))
        {
            var dest = Path.Combine(keep, test + "-" + Path.GetFileName(root), Path.GetFileName(run));
            CsEndToEndTests.CopyDirectory(run, dest);
            File.WriteAllLines(Path.Combine(dest, "ui-lines.txt"), ui.Lines);
            output.WriteLine($"run kept at {dest}");
        }
        return run;
    }
}
