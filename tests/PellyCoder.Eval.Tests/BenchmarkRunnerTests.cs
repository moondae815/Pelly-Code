using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using System.Text.Json;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Processes;
using PellyCoder.Core.Workspaces;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

/// <summary>
/// The runner on a throwaway eval root with one plain .cs file (no csproj: syntax-only verification, no restore, no
/// build process), a canned model and a substitute independent verifier.
/// </summary>
public sealed class BenchmarkRunnerTests : IDisposable
{
    private const string Source = "namespace Demo;\n\npublic class Calc\n{\n    public int Add(int a, int b)\n    {\n        return a + b;\n    }\n}\n";
    private const string Edit = "        return a + b;\n=======\n        return checked(a + b);\n>>>>>>> REPLACE\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-eval-runner-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp folder, best effort */ }
    }

    [Fact]
    public async Task Runner_VerifierThrows_KeepsExitCodeAndAttempts()
    {
        var suite = PlainSuite();
        var task = new BenchmarkTask
        {
            Id = "cs-modern-901", Kind = "cs", File = "Plain/Calc.cs", Method = "Add", Request = "오버플로를 검사해줘",
            Judge = new BenchmarkJudge { Build = true, MustContain = ["checked("] },
        };
        var fs = new FileSystem();
        var options = Assert.IsType<PellyOptions>(
            PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), new Dictionary<string, string?>()).Options);
        var runner = new BenchmarkRunner(new RunnerSetup(fs, new SystemProcessRunner(TimeProvider.System), new OneShotBackend(Edit),
            options, TimeProvider.System, suite, null, "no SQL", _ => { }, Verifier: new ThrowingVerifier()));

        var result = await runner.RunAsync(Resolve(suite, task, fs), Path.Combine(_root, "out"), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(["Ok"], result.EditOutcomes);
        Assert.Equal(VerificationResult.Skipped, result.Independent!.Status);
        Assert.Equal("독립 검증 오류: verifier exploded", result.Independent.Detail);
        Assert.False(result.InvariantViolated);
        Assert.Equal(VerificationResult.Passed, result.Status);
    }

    // Exit 0, the independent build passed, a behaviour judge failed: the model's miss (Judge), not an invariant violation.
    // A judge that could not run (tooling) is its own failure type; an independent build failure is the violation.
    [Theory]
    [InlineData("judge", "Judge", false)]
    [InlineData("tooling", "JudgeTooling", false)]
    [InlineData("build", "InvariantViolated", true)]
    public async Task Runner_FailureTypeFollowsTheBuildVerdict(string kind, string failureType, bool violation)
    {
        var suite = PlainSuite();
        var task = new BenchmarkTask
        {
            Id = "cs-modern-901", Kind = "cs", File = "Plain/Calc.cs", Method = "Add", Request = "오버플로를 검사해줘",
            Judge = new BenchmarkJudge { Build = true, Tests = ["Shop.Api.JudgeTests.T.cs_modern_901_X"] },
        };
        var verdict = kind switch
        {
            "judge" => new VerificationResult(VerificationResult.Failed, "판정 테스트 실패: x", VerificationResult.Passed),
            "tooling" => new VerificationResult(VerificationResult.Skipped, "판정 도구 오류: 복원 실패", VerificationResult.Passed),
            _ => VerificationResult.Fail("빌드 오류: CS0103"),
        };
        var fs = new FileSystem();
        var options = Assert.IsType<PellyOptions>(
            PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), new Dictionary<string, string?>()).Options);
        var runner = new BenchmarkRunner(new RunnerSetup(fs, new SystemProcessRunner(TimeProvider.System), new OneShotBackend(Edit),
            options, TimeProvider.System, suite, null, "no SQL", _ => { }, Verifier: new FixedVerifier(verdict)));

        var result = await runner.RunAsync(Resolve(suite, task, fs), Path.Combine(_root, "out"), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(VerificationResult.Failed, result.Status);
        Assert.Equal(failureType, result.FailureType);
        Assert.Equal(violation, result.InvariantViolated);
    }

    [Fact]
    public async Task Runner_KeepWorkFailed_DeletesPassedTaskFolder_KeepsFailed()
    {
        var suite = PlainSuite();
        var fs = new FileSystem();
        var outDir = Path.Combine(_root, "out");
        var passed = await RunWithAsync(suite, fs, KeepWork.Failed, "cs-modern-901", VerificationResult.Pass, outDir);
        var failed = await RunWithAsync(suite, fs, KeepWork.Failed, "cs-modern-902", VerificationResult.Fail("빌드 오류: CS0103"), outDir);

        Assert.Equal(VerificationResult.Passed, passed.Status);
        Assert.Equal(VerificationResult.Failed, failed.Status);
        Assert.False(Directory.Exists(Path.Combine(outDir, "work", "cs-modern-901")));
        Assert.True(Directory.Exists(Path.Combine(outDir, "work", "cs-modern-902")));
        Assert.Equal(1, passed.Attempts);
    }

    [Fact]
    public async Task Runner_KeepWorkNone_DeletesBoth_AfterArtifactsAreRead()
    {
        var suite = PlainSuite();
        var fs = new FileSystem();
        var outDir = Path.Combine(_root, "out");
        var passed = await RunWithAsync(suite, fs, KeepWork.None, "cs-modern-901", VerificationResult.Pass, outDir);
        var failed = await RunWithAsync(suite, fs, KeepWork.None, "cs-modern-902", VerificationResult.Fail("빌드 오류: CS0103"), outDir);

        Assert.False(Directory.Exists(Path.Combine(outDir, "work", "cs-modern-901")));
        Assert.False(Directory.Exists(Path.Combine(outDir, "work", "cs-modern-902")));
        Assert.All(new[] { passed, failed }, r => { Assert.Equal(1, r.Attempts); Assert.NotNull(r.TtftSeconds); });
    }

    [Fact]
    public async Task Runner_RestoreFailure_SkipDetailCarriesTheLastRestoreLines()
    {
        var suite = PlainSuite();
        File.WriteAllText(Path.Combine(suite.Roots[0].Path, "Plain.sln"), "");
        var task = new BenchmarkTask
        {
            Id = "cs-modern-901", Kind = "cs", File = "Plain/Calc.cs", Method = "Add", Request = "오버플로를 검사해줘",
            Judge = new BenchmarkJudge { Build = true, MustContain = ["checked("] },
        };
        var fs = new FileSystem();
        var options = Assert.IsType<PellyOptions>(
            PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), new Dictionary<string, string?>()).Options);
        var procs = new FailingRestore("  Determining projects to restore...\nerror NU1301: 원본 'offline'을 불러올 수 없습니다.\n");
        var runner = new BenchmarkRunner(new RunnerSetup(fs, procs, new OneShotBackend(Edit),
            options, TimeProvider.System, suite, null, "no SQL", _ => { }, Verifier: new FixedVerifier(VerificationResult.Pass)));

        var result = await runner.RunAsync(Resolve(suite, task, fs), Path.Combine(_root, "out"), TestContext.Current.CancellationToken);

        Assert.Equal(VerificationResult.Skipped, result.Status);
        Assert.Equal("환경 오류(복원 실패): dotnet restore 종료 1: Determining projects to restore... / error NU1301: 원본 'offline'을 불러올 수 없습니다.",
            result.SkipReason);
        Assert.Equal(["restore", Path.Combine(_root, "out", "work", "cs-modern-901", "Plain", "Plain.sln")], procs.Calls.Single().Arguments.Take(2));
    }

    [Fact]
    public async Task TestFirst_SkipsTasksWithoutJudgeTests()
    {
        var suite = PlainSuite();
        var task = new BenchmarkTask
        {
            Id = "cs-modern-901", Kind = "cs", File = "Plain/Calc.cs", Method = "Add", Request = "오버플로를 검사해줘",
            Judge = new BenchmarkJudge { Build = true, MustContain = ["checked("] },
        };
        var fs = new FileSystem();
        var runs = new List<RunRequest>();
        var runner = new BenchmarkRunner(new RunnerSetup(fs, new RecordingProcesses(), new OneShotBackend(Edit), Options(fs),
            TimeProvider.System, suite, null, "no SQL", _ => { }, Verifier: new FixedVerifier(VerificationResult.Pass),
            TestFirst: true, Orchestrate: (r, _) => { runs.Add(r); return Task.FromResult(0); }));
        var outDir = Path.Combine(_root, "out");

        var result = await runner.RunAsync(Resolve(suite, task, fs), outDir, TestContext.Current.CancellationToken);

        Assert.Equal(VerificationResult.Skipped, result.Status);
        Assert.Equal("테스트 우선 대상 아님", result.SkipReason);
        Assert.Empty(runs);
        Assert.False(Directory.Exists(Path.Combine(outDir, "work", "cs-modern-901")));
    }

    // The request names the judge's tests (and its pinned old-behaviour tests as breaks); the judge projects are copied
    // inside the workspace and restored after the solution, with the same command the independent judge uses.
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task TestFirst_RequestCarriesVerifyTestsAndBreaks(bool withBreaks)
    {
        var suite = JudgeSuite();
        var task = new BenchmarkTask
        {
            Id = "cs-modern-901", Kind = "cs", File = "Modern/App/Calc.cs", Method = "Add", Request = "오버플로를 검사해줘",
            Judge = new BenchmarkJudge
            {
                Build = true, Tests = ["App.JudgeTests.CalcJudgeTests.Add_Overflow_Throws"],
                FailingTests = withBreaks ? ["App.Tests.CalcTests.Add_Overflow_Wraps"] : [],
            },
        };
        var fs = new FileSystem();
        var procs = new RecordingProcesses();
        var runs = new List<RunRequest>();
        var runner = new BenchmarkRunner(new RunnerSetup(fs, procs, new OneShotBackend(Edit), Options(fs), TimeProvider.System, suite,
            null, "no SQL", _ => { }, Verifier: new FixedVerifier(VerificationResult.Pass), TestFirst: true,
            Orchestrate: (r, _) =>
            {
                runs.Add(r);
                WriteSummary(fs, r.FilePath, """{ "attempts": 2, "testFirst": { "tests": 1, "red": "Red", "green": "Passed", "regressions": 1, "breaksExcluded": 3, "inApply": false, "firstAttemptGreen": "Failed" } }""");
                return Task.FromResult(0);
            }));
        var outDir = Path.Combine(_root, "out");

        var result = await runner.RunAsync(Resolve(suite, task, fs), outDir, TestContext.Current.CancellationToken);

        var request = Assert.Single(runs);
        Assert.Equal(["App.JudgeTests.CalcJudgeTests.Add_Overflow_Throws"], request.VerifyTests);
        if (withBreaks) Assert.Equal(["App.Tests.CalcTests.Add_Overflow_Wraps"], request.ExpectBreaks);
        else Assert.Null(request.ExpectBreaks);
        Assert.True(request.Yes);

        var rootCopy = Path.Combine(outDir, "work", "cs-modern-901", "Modern");
        var copied = Path.Combine(rootCopy, "JudgeTests", "App.JudgeTests", "App.JudgeTests.csproj");
        Assert.True(File.Exists(copied));
        Assert.Equal(2, procs.Calls.Count);
        Assert.Equal(["restore", Path.Combine(rootCopy, "Modern.sln")], procs.Calls[0].Arguments.Take(2));
        Assert.Equal(["restore", copied, "--nologo", $"-p:{IndependentVerifier.JudgeTargetRootProperty}={Path.GetFullPath(rootCopy)}"], procs.Calls[1].Arguments);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(2, result.Attempts);
        Assert.Equal(new TestFirstResult("Red", "Passed", "Failed", 1, 3), result.TestFirst);
    }

    [Fact]
    public async Task TestFirst_JudgeRestoreFails_EnvironmentSkipped()
    {
        var suite = JudgeSuite();
        var task = new BenchmarkTask
        {
            Id = "cs-modern-901", Kind = "cs", File = "Modern/App/Calc.cs", Method = "Add", Request = "오버플로를 검사해줘",
            Judge = new BenchmarkJudge { Build = true, Tests = ["App.JudgeTests.CalcJudgeTests.Add_Overflow_Throws"] },
        };
        var fs = new FileSystem();
        var procs = new RecordingProcesses { FailWhen = spec => spec.Arguments[1].EndsWith("App.JudgeTests.csproj", StringComparison.Ordinal) };
        var runs = new List<RunRequest>();
        var runner = new BenchmarkRunner(new RunnerSetup(fs, procs, new OneShotBackend(Edit), Options(fs), TimeProvider.System, suite,
            null, "no SQL", _ => { }, Verifier: new FixedVerifier(VerificationResult.Pass), TestFirst: true,
            Orchestrate: (r, _) => { runs.Add(r); return Task.FromResult(0); }));

        var result = await runner.RunAsync(Resolve(suite, task, fs), Path.Combine(_root, "out"), TestContext.Current.CancellationToken);

        Assert.Equal(VerificationResult.Skipped, result.Status);
        Assert.Equal("환경 오류(복원 실패): dotnet restore App.JudgeTests.csproj 종료 1: error NU1301: offline", result.SkipReason);
        Assert.Empty(runs);
    }

    // The default run's rows keep exactly their keys: testFirst appears only when a test-first run set it, and the run
    // conditions of a summary gain testFirst only with --test-first.
    [Fact]
    public async Task Run_WithoutTestFirst_ResultsJsonUnchanged()
    {
        var suite = PlainSuite();
        var fs = new FileSystem();
        var row = await RunWithAsync(suite, fs, KeepWork.All, "cs-modern-901", VerificationResult.Pass, Path.Combine(_root, "out"));
        Assert.Null(row.TestFirst);

        string[] keys =
        [
            "id", "kind", "status", "exitCode", "attempts", "reasons", "independent", "buildCheck", "invariantViolated", "failureType",
            "wallSeconds", "ttftSeconds", "thinkTokens", "thinkTruncated", "editOutcomes", "skipReason", "isLegacy", "formatErrors",
        ];
        Assert.Equal(keys, Keys(JsonSerializer.Serialize(row, ResultsJson.Options)));
        var withTestFirst = row with { TestFirst = new TestFirstResult("Red", "Passed", "Passed", 0, 0) };
        Assert.Equal([.. keys, "testFirst"], Keys(JsonSerializer.Serialize(withTestFirst, ResultsJson.Options)));
        Assert.Contains("\"testFirst\": {\n    \"red\": \"Red\",", JsonSerializer.Serialize(withTestFirst, ResultsJson.Options).ReplaceLineEndings("\n"), StringComparison.Ordinal);

        var conditions = new RunConditions("m", "c", "s", "Auto", 1, new SamplingOptions(0.6, 0.95, 20, 1.0, 0.0), new SamplingOptions(0.1, 0.9, 20, 1.05, 0.0), "v", null, null);
        Assert.DoesNotContain("testFirst", RepeatSummary.ToJson(Summary(conditions)), StringComparison.Ordinal);
        Assert.Contains("\"testFirst\": true", RepeatSummary.ToJson(Summary(conditions with { TestFirst = true })), StringComparison.Ordinal);
        Assert.True(RepeatSummary.FromJson(RepeatSummary.ToJson(Summary(conditions with { TestFirst = true }))).Conditions!.TestFirst);
        Assert.Null(RepeatSummary.FromJson(RepeatSummary.ToJson(Summary(conditions))).Conditions!.TestFirst);

        static string[] Keys(string json)
        {
            using var doc = JsonDocument.Parse(json);
            return [.. doc.RootElement.EnumerateObject().Select(p => p.Name)];
        }

        static RepeatSummaryData Summary(RunConditions c) => RepeatSummary.Build([[]]) with { Conditions = c };
    }

    // Real dotnet end to end: a Data task whose judge project compiles the corpus sources (Compile Include, no project
    // reference) is found by pelly inside the workspace, built against the edited copy and turns GREEN.
    [Fact]
    public async Task TestFirst_DataJudgeCompilesEditedSource()
    {
        const string giftLast = "ORDER BY OrderItemId\";\n=======\nORDER BY IsGiftItem, OrderItemId\";\n>>>>>>> REPLACE\n";
        var suite = TestPaths.Suite;
        var fs = new FileSystem();
        var log = new List<string>();
        var runner = new BenchmarkRunner(new RunnerSetup(fs, new SystemProcessRunner(TimeProvider.System), new OneShotBackend(giftLast),
            Options(fs), TimeProvider.System, suite, null, "no SQL", log.Add, TestFirst: true));
        // Link-free root: through the macOS /var -> /private/var link the restore skips the transitive ProjectReference.
        Directory.CreateDirectory(_root);
        var outDir = Path.Combine(RealPath(_root), "out");

        var result = await runner.RunAsync(TestPaths.Resolve("cs-modern-003"), outDir, TestContext.Current.CancellationToken);

        var detail = string.Join("\n", log) + "\n" + string.Join("; ", result.Reasons) + " / " + result.Independent + " / " + result.SkipReason;
        Assert.True(result.Status == VerificationResult.Passed, detail);
        Assert.Equal(0, result.ExitCode);
        Assert.NotNull(result.TestFirst);
        Assert.Equal("Red", result.TestFirst!.Red);
        Assert.Equal("Passed", result.TestFirst.Green);
        Assert.Equal("Passed", result.TestFirst.FirstAttemptGreen);
        Assert.Equal(0, result.TestFirst.Regressions);
        Assert.Equal(VerificationResult.Passed, result.Independent!.Status);
    }

    /// <summary><paramref name="path"/> with every symbolic link resolved (macOS temp folders live behind <c>/var</c>).</summary>
    private static string RealPath(string path)
    {
        var current = Path.GetPathRoot(path)!;
        foreach (var segment in path[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if (new DirectoryInfo(current).ResolveLinkTarget(returnFinalTarget: true) is { } target) current = target.FullName;
        }
        return current;
    }

    private static PellyOptions Options(IFileSystem fs) => Assert.IsType<PellyOptions>(
        PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), new Dictionary<string, string?>()).Options);

    /// <summary>Writes a run's <c>summary.json</c> where pelly would for <paramref name="file"/>.</summary>
    private static void WriteSummary(IFileSystem fs, string file, string json)
    {
        var dir = Path.Combine(WorkspaceLocator.Resolve(fs, file).PellyDir, "runs", "20261026-000000-0000");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "summary.json"), json);
    }

    /// <summary>A suite with one root <c>Modern</c> (<c>Modern.sln</c>, <c>App/Calc.cs</c>) and one judge project <c>App.JudgeTests</c>.</summary>
    private BenchmarkSuite JudgeSuite()
    {
        var dir = Path.Combine(_root, "suite");
        foreach (var (f, text) in new[]
                 {
                     ("Modern/App/Calc.cs", Source), ("Modern/Modern.sln", ""),
                     ("judges/Modern/App.JudgeTests/App.JudgeTests.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\" />"),
                     ("judges/Modern/App.JudgeTests/Directory.Build.props", "<Project>\n</Project>\n"),
                 })
        {
            var path = Path.Combine(dir, f.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        File.WriteAllText(Path.Combine(dir, BenchmarkSuite.FileName),
            """{ "formatVersion": 1, "name": "judged", "roots": [ { "name": "Modern", "path": "Modern" } ] }""");
        var load = BenchmarkSuite.Load(dir);
        return load.Suite ?? throw new InvalidDataException(string.Join("; ", load.Errors));
    }

    /// <summary>Records every process; succeeds unless <see cref="FailWhen"/> matches (then exit 1 with a NuGet error).</summary>
    private sealed class RecordingProcesses : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];
        public Func<ProcessSpec, bool> FailWhen { get; init; } = _ => false;

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Calls.Add(spec);
            return Task.FromResult(FailWhen(spec)
                ? new ProcessResult(1, "", "error NU1301: offline\n", [], TimeSpan.Zero, false)
                : new ProcessResult(0, "", "", [], TimeSpan.Zero, false));
        }
    }

    private sealed class FailingRestore(string stdErr) : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Calls.Add(spec);
            return Task.FromResult(new ProcessResult(1, "", stdErr, [], TimeSpan.Zero, false));
        }
    }

    private async Task<TaskResult> RunWithAsync(BenchmarkSuite suite, IFileSystem fs, KeepWork keep, string id, VerificationResult verdict, string outDir)
    {
        var task = new BenchmarkTask
        {
            Id = id, Kind = "cs", File = "Plain/Calc.cs", Method = "Add", Request = "오버플로를 검사해줘",
            Judge = new BenchmarkJudge { Build = true, MustContain = ["checked("] },
        };
        var runner = new BenchmarkRunner(new RunnerSetup(fs, new SystemProcessRunner(TimeProvider.System), new OneShotBackend(Edit),
            Options(fs), TimeProvider.System, suite, null, "no SQL", _ => { }, Verifier: new FixedVerifier(verdict), KeepWork: keep));
        return await runner.RunAsync(Resolve(suite, task, fs), outDir, TestContext.Current.CancellationToken);
    }

    /// <summary>A suite with one root <c>Plain</c> holding <c>Calc.cs</c>.</summary>
    private BenchmarkSuite PlainSuite()
    {
        var dir = Path.Combine(_root, "suite");
        Directory.CreateDirectory(Path.Combine(dir, "Plain"));
        File.WriteAllText(Path.Combine(dir, "Plain", "Calc.cs"), Source);
        File.WriteAllText(Path.Combine(dir, BenchmarkSuite.FileName),
            """{ "formatVersion": 1, "name": "plain", "roots": [ { "name": "Plain", "path": "Plain" } ] }""");
        var load = BenchmarkSuite.Load(dir);
        return load.Suite ?? throw new InvalidDataException(string.Join("; ", load.Errors));
    }

    private static SuiteTask Resolve(BenchmarkSuite suite, BenchmarkTask task, IFileSystem fs) =>
        suite.Resolve(task, fs, out var error) ?? throw new InvalidDataException(error);

    private sealed class FixedVerifier(VerificationResult verdict) : IIndependentVerifier
    {
        public Task<VerificationResult> VerifyCsAsync(BenchmarkTask task, string file, string corpusRoot, CancellationToken ct) => Task.FromResult(verdict);

        public Task<VerificationResult> VerifySqlAsync(BenchmarkTask task, string file, CancellationToken ct) => Task.FromResult(verdict);
    }

    private sealed class ThrowingVerifier : IIndependentVerifier
    {
        public Task<VerificationResult> VerifyCsAsync(BenchmarkTask task, string file, string corpusRoot, CancellationToken ct) =>
            throw new InvalidOperationException("verifier exploded");

        public Task<VerificationResult> VerifySqlAsync(BenchmarkTask task, string file, CancellationToken ct) =>
            throw new InvalidOperationException("verifier exploded");
    }

    /// <summary>Answers the first generate request with <paramref name="text"/>; any further request fails the run.</summary>
    private sealed class OneShotBackend(string text) : ILlmBackend
    {
        private int _calls;

        public async IAsyncEnumerable<LlmChunk> GenerateAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) > 1) throw new InvalidOperationException("only one response");
            await Task.Yield();
            yield return new LlmChunk(text, false, null, null, null, null, null);
            yield return new LlmChunk("", true, 100, 0, 10, TimeSpan.FromMilliseconds(1), "stop");
        }

        public Task<LlmServerStatus> GetStatusAsync(CancellationToken ct) =>
            Task.FromResult(new LlmServerStatus(true, "canned", true, true, "qwen3.5", TimeSpan.Zero, null));
    }
}
