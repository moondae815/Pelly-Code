using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Processes;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Testing;
using PellyCoder.Integration.Tests.Corpus;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>
/// Real-model runs of the M3a features against a restored copy of the Modern corpus: the DTO slice in the prompt,
/// <c>--test</c> (a passing and a test-contradicting edit) and <c>pelly report</c> / <c>--sanitize</c> over real runs.
/// Needs Ollama (<c>PELLY_TEST_OLLAMA</c>, <c>PELLY_TEST_MODEL</c>).
/// </summary>
public sealed class M3aEndToEndTests(ITestOutputHelper output) : IDisposable
{
    /// <summary>cs-modern-002. The tests assert 28,000 and 22,000, already multiples of 10, so rounding keeps them green.</summary>
    private const string RefundRequest = "환불액의 10원 미만을 버리지 말고 10원 단위로 반올림하도록 바꿔줘";

    /// <summary>Contradicts <c>CancelAsync_WhenShipped_CurrentlyCancels</c> (a shipped order must still be cancelled).</summary>
    private const string ShippedCancelRequest = "배송 중(Shipped)인 주문을 취소하려고 하면 InvalidOperationException을 던지도록 수정해줘";

    /// <summary>How the contradicting <c>--test</c> run may end: its tests failed, or its last attempt undid the change.</summary>
    private static readonly string?[] ContradictionKinds = [FailureKinds.TestErrors, FailureKinds.NoChange];

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
    public async Task Dto_InPrompt_AndEditSucceeds()
    {
        var root = await PrepareModernAsync();
        var file = OrderServiceFile(root);
        var before = File.ReadAllText(file);

        var (exit, ui) = await RunAsync(file, "CalculateRefundAsync", RefundRequest, runTests: false);
        var run = DumpRun(root, ui);

        Assert.Equal(ExitCodes.Success, exit);
        var prompt = File.ReadAllText(Path.Combine(run, "attempt-1", "prompt.txt"));
        Assert.Contains("[READ-ONLY: dto]", prompt, StringComparison.Ordinal);
        Assert.Contains("IsGiftItem", DtoPart(prompt), StringComparison.Ordinal);
        var after = File.ReadAllText(file);
        Assert.NotEqual(before, after);
        Assert.Contains("Math.Round", Target(after, "CalculateRefundAsync"), StringComparison.Ordinal);
    }

    [OllamaFact]
    public async Task Test_PassingEdit_RunsTests_Exit0()
    {
        var root = await PrepareModernAsync();
        var file = OrderServiceFile(root);
        var before = File.ReadAllText(file);

        var (exit, ui) = await RunAsync(file, "CalculateRefundAsync", RefundRequest, runTests: true);
        var run = DumpRun(root, ui);

        Assert.Equal(ExitCodes.Success, exit);
        Assert.NotEqual(before, File.ReadAllText(file));
        AssertTestsActuallyRan(run, ui);
        var summary = ReadSummary(run);
        Assert.Null(summary.GetProperty("failureKind").GetString());
    }

    /// <summary>
    /// Known evasion pelly cannot detect: the model comments out its throw (the target still differs) and the tests pass.
    /// Its rate is recorded in docs/m3a-e2e.md; the expectation stays exit 1.
    /// </summary>
    [OllamaFact]
    public async Task Test_ContradictingEdit_FailsWithTestErrors_Restored()
    {
        var root = await PrepareModernAsync();
        var file = OrderServiceFile(root);
        var before = File.ReadAllBytes(file);

        var (exit, ui) = await RunAsync(file, "CancelAsync", ShippedCancelRequest, runTests: true);
        var run = DumpRun(root, ui);

        Assert.Equal(ExitCodes.VerifyFailed, exit);
        AssertTestsActuallyRan(run, ui);
        var summary = ReadSummary(run);
        // Ruling (T14): the last attempt may also be an undo that the no-change guard rejected.
        Assert.Contains(summary.GetProperty("failureKind").GetString(), ContradictionKinds);
        Assert.Contains("TEST", summary.GetProperty("errorCodes").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(before, File.ReadAllBytes(file));
    }

    [OllamaFact]
    public async Task Report_AndSanitize_OverRealRuns()
    {
        var root = await PrepareModernAsync();
        var file = OrderServiceFile(root);

        // Two real runs in one workspace: a passing edit and a test-contradicting --test edit.
        var (passExit, passUi) = await RunAsync(file, "CalculateRefundAsync", RefundRequest, runTests: false);
        DumpRun(root, passUi);
        var (failExit, failUi) = await RunAsync(file, "CancelAsync", ShippedCancelRequest, runTests: true);
        DumpRun(root, failUi);
        Assert.Equal(ExitCodes.Success, passExit);
        Assert.Equal(ExitCodes.VerifyFailed, failExit);

        var report = await CliAsync(root, ["report"]);
        Assert.Equal(ExitCodes.Success, report.ExitCode);
        Assert.Contains("실행 2건", report.StdOut, StringComparison.Ordinal);
        Assert.Contains("최종 작업 성공률", report.StdOut, StringComparison.Ordinal);
        Assert.Contains(ContradictionKinds, k => report.StdOut.Contains(k!, StringComparison.Ordinal));

        var outDir = Path.Combine(root, "sanitized");
        var sanitized = await CliAsync(root, ["report", "--sanitize", "--out", outDir]);
        Assert.Equal(ExitCodes.Success, sanitized.ExitCode);
        var files = Directory.GetFiles(outDir);
        Assert.Equal(2, files.Length);
        Assert.Single(files, f => f.EndsWith(".json", StringComparison.Ordinal));
        Assert.Single(files, f => f.EndsWith(".md", StringComparison.Ordinal));

        var runIds = Directory.GetDirectories(Path.Combine(root, ".pelly", "runs")).Select(Path.GetFileName).ToList();
        string[] forbidden =
        [
            "OrderService", "Shop.Api", "CalculateRefundAsync", "CancelAsync", TestEnv.OllamaModel!, root,
            Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar), Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "/private/", "/var/", "/Users/", ".pelly", ".cs", "pelly-m3a-",
        ];
        foreach (var path in files)
        {
            var text = File.ReadAllText(path);
            output.WriteLine($"--- {Path.GetFileName(path)}");
            output.WriteLine(text);
            foreach (var word in forbidden)
                Assert.False(text.Contains(word, StringComparison.OrdinalIgnoreCase), $"{Path.GetFileName(path)} contains '{word}'");
            foreach (var id in runIds)
                Assert.DoesNotContain(id!, text, StringComparison.Ordinal);
        }
        var md = File.ReadAllText(files.Single(f => f.EndsWith(".md", StringComparison.Ordinal)));
        Assert.Contains("| 실행 수 | 2 |", md, StringComparison.Ordinal);
        Assert.Contains(ContradictionKinds, k => md.Contains(k!, StringComparison.Ordinal));
        Assert.Contains("TEST", md, StringComparison.Ordinal);
        KeepText(root, "report-stdout.txt", report.StdOut + report.StdErr);
    }

    // ---- judging ----

    /// <summary>
    /// The test step really ran: the original tests and this run's tests both produced TRX reports with results, and the
    /// "could not run the original tests" bypass did not fire (that path passes the step without testing anything).
    /// </summary>
    private static void AssertTestsActuallyRan(string run, RecordingUserInteraction ui)
    {
        var tests = Path.Combine(run, "tests");
        Assert.True(Directory.Exists(tests), "tests folder missing: " + tests);
        var baseline = Directory.GetFiles(tests, "baseline-*.trx");
        var verify = Directory.GetFiles(tests, "verify-*.trx");
        Assert.NotEmpty(baseline);
        Assert.NotEmpty(verify);
        foreach (var trx in baseline.Concat(verify))
            Assert.True(TrxParser.Parse(File.ReadAllText(trx)).Count >= 3, "too few results in " + trx);
        Assert.DoesNotContain(ui.Lines, l => l.Contains("원본 상태 테스트를 실행하지 못해", StringComparison.Ordinal));
    }

    private static JsonElement ReadSummary(string run) =>
        JsonDocument.Parse(File.ReadAllText(Path.Combine(run, "summary.json"))).RootElement.Clone();

    /// <summary>The dto part of the prompt: from its marker to the next part marker.</summary>
    private static string DtoPart(string prompt)
    {
        var start = prompt.IndexOf("[READ-ONLY: dto]", StringComparison.Ordinal);
        var next = prompt.IndexOf("\n[", start + 1, StringComparison.Ordinal);
        return next < 0 ? prompt[start..] : prompt[start..next];
    }

    private static string Target(string text, string member) =>
        CsEndToEndTests.Split(CsEndToEndTests.Lf(text), Microsoft.CodeAnalysis.CSharp.LanguageVersion.Latest, member).Target;

    // ---- run plumbing ----

    private static string OrderServiceFile(string root) => Path.Combine(root, "Shop.Api", "Services", "OrderService.cs");

    /// <summary>
    /// A restored copy of the Modern corpus (no global.json) under a link-free root. The restore uses a relative path from
    /// the resolved root: through the macOS /var → /private/var link the restore dropped the test project's transitive
    /// ProjectReference and the built test app missed Shop.Api.dll (TestRunnerSdkTests).
    /// </summary>
    private async Task<string> PrepareModernAsync()
    {
        var temp = Path.Combine(Path.GetTempPath(), "pelly-m3a-" + Guid.NewGuid().ToString("N")[..8]);
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

    private static async Task<(int Exit, RecordingUserInteraction Ui)> RunAsync(string file, string method, string request, bool runTests)
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
        var orchestrator = FsmOrchestratorFactory.Create(fs, backend, ui, options, TimeProvider.System, new SystemProcessRunner(TimeProvider.System));

        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15));
        var exit = await orchestrator.RunAsync(
            new RunRequest(file, null, method, null, request, Yes: true, DryRun: false, MaxRetries: null, RunTests: runTests), cts.Token);
        return (exit, ui);
    }

    private static async Task<ProcessResult> CliAsync(string workDir, IReadOnlyList<string> args) =>
        await new SystemProcessRunner(TimeProvider.System).RunAsync(
            new ProcessSpec("dotnet", [CliDll(), .. args], workDir, TimeSpan.FromMinutes(2)),
            TestContext.Current.CancellationToken);

    /// <summary>The pelly CLI built next to this test assembly's configuration (built first through the project reference).</summary>
    private static string CliDll()
    {
        var baseDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var repo = Directory.GetParent(CorpusPaths.Root)!.Parent!.FullName;
        var dll = Path.Combine(repo, "src", "PellyCoder.Cli", "bin", baseDir.Parent!.Name, baseDir.Name, "pelly.dll");
        Assert.True(File.Exists(dll), $"pelly CLI가 빌드되어 있지 않습니다: {dll}");
        return dll;
    }

    /// <summary>Writes the run's lines and summary to the test output and returns the newest run folder.</summary>
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

    private void KeepText(string root, string name, string text, [CallerMemberName] string test = "")
    {
        var keep = Environment.GetEnvironmentVariable("PELLY_TEST_KEEP");
        if (string.IsNullOrEmpty(keep)) return;
        var dest = Path.Combine(keep, test + "-" + Path.GetFileName(root));
        Directory.CreateDirectory(dest);
        File.WriteAllText(Path.Combine(dest, name), text);
        var sanitized = Path.Combine(root, "sanitized");
        if (Directory.Exists(sanitized)) CsEndToEndTests.CopyDirectory(sanitized, Path.Combine(dest, "sanitized"));
    }
}
