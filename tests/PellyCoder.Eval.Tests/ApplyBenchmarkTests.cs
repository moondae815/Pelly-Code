using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Workspaces;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

/// <summary>
/// The apply benchmark on a throwaway corpus (one SDK project, one class) with a fake task run and a fake independent build:
/// no LLM, no SQL Server, no build process.
/// </summary>
public sealed class ApplyBenchmarkTests : IDisposable
{
    private const string Source =
        "namespace Demo;\n\npublic class Calc\n{\n    public int Add(int a, int b)\n    {\n        return a + b;\n    }\n\n"
        + "    public int Sub(int a, int b)\n    {\n        return a - b;\n    }\n\n    public int Mul(int a, int b)\n    {\n        return a * b;\n    }\n}\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-eval-apply-" + Guid.NewGuid().ToString("N")[..8]);

    public ApplyBenchmarkTests()
    {
        var project = Path.Combine(_root, "corpus", "Modern", "App");
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "App.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(project, "Calc.cs"), Source);
        File.WriteAllText(Path.Combine(_root, "global.json"), "{}\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp folder, best effort */ }
    }

    private string Out => Path.Combine(_root, "out");

    private static ApplyCase Case(string id, string scope, params string[] taskLines)
    {
        var text = $"---\nid: {id}\nstatus: draft\nscope: [{scope}]\n---\n# 시험 명세\n\n## 작업\n" + string.Join("\n", taskLines) + "\n";
        var spec = ChangeSpecParser.Parse(text);
        Assert.Empty(spec.Issues);
        return new ApplyCase(id, scope.Split(", "), text, spec.Tasks.Count,
            spec.Tasks.Any(t => t.Kind == TaskKind.Sql), spec.Tasks.Any(t => t.Group is not null));
    }

    private static readonly string[] ThreeTasks =
    [
        "- [ ] T1 cs Modern/App/Calc.cs#Add 오버플로를 검사한다",
        "- [ ] T2 cs Modern/App/Calc.cs#Sub 오버플로를 검사한다",
        "- [ ] T3 cs Modern/App/Calc.cs#Mul 오버플로를 검사한다",
    ];

    /// <summary>Edits the target file (appends a comment) and succeeds; a request containing 「실패」 fails without editing.</summary>
    private static Func<RunRequest, CancellationToken, Task<RunResult>> EditingRun(Action<RunRequest>? observe = null) => (request, _) =>
    {
        observe?.Invoke(request);
        if (request.Request.Contains("실패", StringComparison.Ordinal))
            return Task.FromResult(new RunResult(1, "run-failed", request.FilePath, null, null, "Exhausted"));
        File.AppendAllText(request.FilePath, "// edited\n");
        return Task.FromResult(new RunResult(0, "run-" + request.MethodName, request.FilePath, null, null, null));
    };

    private ApplyBenchSetup Setup(Func<RunRequest, CancellationToken, Task<RunResult>> run, IGroupVerifier verifier, bool isWindows = true, bool sql = true)
    {
        var fs = new FileSystem();
        var options = Assert.IsType<PellyOptions>(
            PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), new Dictionary<string, string?>()).Options);
        return new ApplyBenchSetup(fs, options, new NoProcesses(), TimeProvider.System, () => isWindows, sql, _ => run, verifier,
            Path.Combine(_root, "corpus"), [Path.Combine(_root, "global.json")], _ => { });
    }

    [Fact]
    public async Task Legacy_case_is_skipped_off_windows()
    {
        var c = Case("x1-legacy", "Legacy", "- [ ] T1 cs Legacy/App/Calc.cs#Add 오버플로를 검사한다");
        var result = await ApplyBenchmark.RunCaseAsync(c, 1, Setup(EditingRun(), new FixedBuild([]), isWindows: false), Out,
            TestContext.Current.CancellationToken);

        Assert.Equal(("skipped", "Windows 전용"), (result.Outcome, result.SkipReason));
        Assert.False(Directory.Exists(Path.Combine(Out, "work", "x1-legacy-1")));
    }

    [Fact]
    public async Task Sql_case_is_skipped_without_connection()
    {
        var c = Case("x2-sql", "Sql", "- [ ] T1 sql Sql/objects/usp_X.sql 조건을 더한다");
        var result = await ApplyBenchmark.RunCaseAsync(c, 1, Setup(EditingRun(), new FixedBuild([]), sql: false), Out,
            TestContext.Current.CancellationToken);

        Assert.Equal(("skipped", "SQL 연결 없음"), (result.Outcome, result.SkipReason));
    }

    [Fact]
    public async Task Work_copy_is_one_workspace_with_a_git_marker()
    {
        var corpus = Path.Combine(Out, "work", "t1-calc-2", "corpus");
        var spec = Path.Combine(corpus, "changes", "t1-calc.md");
        string? gitEntries = null, specRoot = null, hostRoot = null;
        var specExisted = false;
        var run = EditingRun(request =>
        {
            var git = Path.Combine(corpus, ".git");
            gitEntries = Directory.Exists(git) ? string.Join(",", Directory.EnumerateFileSystemEntries(git)) : null;
            specExisted = File.Exists(spec);
            specRoot = WorkspaceLocator.Resolve(new FileSystem(), spec).Root;
            hostRoot = request.Host?.Workspace.Root;
        });

        var result = await ApplyBenchmark.RunCaseAsync(Case("t1-calc", "Modern", ThreeTasks[0]), 2, Setup(run, new FixedBuild([])), Out,
            TestContext.Current.CancellationToken);

        Assert.Equal("", gitEntries); // the .git folder exists and is empty
        Assert.True(specExisted);
        Assert.Equal(corpus, specRoot);
        Assert.Equal(corpus, hostRoot);
        Assert.True(File.Exists(Path.Combine(Out, "work", "t1-calc-2", "global.json")));
        Assert.True(File.Exists(Path.Combine(corpus, "Modern", "App", "Calc.cs")));
        Assert.Equal(("completed", 0, true, 2), (result.Outcome, result.ExitCode, result.Archived, result.Repeat));
        Assert.Equal(["T1"], result.Tasks.Select(t => t.TaskId));
        Assert.True(result.IndependentBuildOk);
    }

    [Fact]
    public async Task Exit_0_with_independent_build_errors_is_an_invariant_violation()
    {
        var verifier = new FixedBuild([new Diagnostic("Calc.cs", 5, 9, "error", "CS0103", "The name 'x' does not exist")]);
        var result = await ApplyBenchmark.RunCaseAsync(Case("t2-calc", "Modern", ThreeTasks), 1, Setup(EditingRun(), verifier), Out,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.IndependentBuildOk);
        // The changed file's project, found by comparing the work copy with the pristine corpus.
        var built = Assert.Single(verifier.Calls);
        Assert.Equal(Path.Combine(Out, "work", "t2-calc-1", "corpus", "Modern", "App", "App.csproj"), Assert.Single(built));
        Assert.Equal(1, ApplyBenchmark.ExitCode([result], cancelled: false));
        Assert.Equal(2, ApplyBenchmark.ExitCode([result], cancelled: true));
    }

    [Fact]
    public async Task Independent_build_unverifiable_is_null_and_exit_stays_0()
    {
        var verifier = new FixedBuild(null);
        var result = await ApplyBenchmark.RunCaseAsync(Case("t3-calc", "Modern", ThreeTasks[0]), 1, Setup(EditingRun(), verifier), Out,
            TestContext.Current.CancellationToken);

        Assert.Null(result.IndependentBuildOk);
        Assert.Equal(0, ApplyBenchmark.ExitCode([result], cancelled: false));
    }

    [Fact]
    public async Task Summary_splits_3_to_8_tasks_and_reports_skips()
    {
        var setup = Setup(EditingRun(), new FixedBuild([]), isWindows: false);
        var ct = TestContext.Current.CancellationToken;
        var failing = ThreeTasks.Take(2).Append("- [ ] T3 cs Modern/App/Calc.cs#Mul 실패하는 작업이다").ToArray();
        List<ApplyCaseResult> results =
        [
            await ApplyBenchmark.RunCaseAsync(Case("s1-three", "Modern", ThreeTasks), 1, setup, Out, ct),
            await ApplyBenchmark.RunCaseAsync(Case("s2-fails", "Modern", failing), 1, setup, Out, ct),
            await ApplyBenchmark.RunCaseAsync(Case("s3-one", "Modern", ThreeTasks[0]), 1, setup, Out, ct),
            await ApplyBenchmark.RunCaseAsync(Case("s4-legacy", "Legacy", "- [ ] T1 cs Legacy/App/Calc.cs#Add 오버플로를 검사한다"), 1, setup, Out, ct),
        ];
        Assert.Equal(["completed", "failed", "completed", "skipped"], results.Select(r => r.Outcome));

        var md = ApplyBenchmarkReport.Write(new FileSystem(), Out, results, "* 시험");

        Assert.True(File.Exists(Path.Combine(Out, "apply-results.json")));
        Assert.Equal(md, File.ReadAllText(Path.Combine(Out, "apply-summary.md")));
        var lines = md.Split('\n');
        Assert.Contains(lines, l => l.StartsWith("* 명세 완료율: 66.7% (2/3건)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("* 명세 완료율(작업 3~8개): 50.0% (1/2건)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("* 그룹 빌드 통과율: ", StringComparison.Ordinal));
        Assert.Contains("## 건너뛴 사례", md, StringComparison.Ordinal);
        Assert.Contains("- s4-legacy (회차 1): Windows 전용", md, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Run_without_apply_json_counts_as_not_completed()
    {
        var setup = Setup(EditingRun(), new FixedBuild([]));
        var ct = TestContext.Current.CancellationToken;
        // The spec check fails (unknown member): pelly apply exits 4 before it writes apply.json.
        var unknown = ThreeTasks.Take(2).Append("- [ ] T3 cs Modern/App/Calc.cs#Div 오버플로를 검사한다").ToArray();
        List<ApplyCaseResult> results =
        [
            await ApplyBenchmark.RunCaseAsync(Case("m1-three", "Modern", ThreeTasks), 1, setup, Out, ct),
            await ApplyBenchmark.RunCaseAsync(Case("m2-nocheck", "Modern", unknown), 1, setup, Out, ct),
            await ApplyBenchmark.RunCaseAsync(Case("m3-nocheck-one", "Modern", "- [ ] T1 cs Modern/App/Calc.cs#Div 오버플로를 검사한다"), 1, setup, Out, ct),
        ];
        Assert.Equal([(ApplyBenchmark.Completed, 0), (ApplyBenchmark.Failed, 4), (ApplyBenchmark.Failed, 4)],
            results.Select(r => (r.Outcome, r.ExitCode)));

        var lines = ApplyBenchmarkReport.Write(new FileSystem(), Out, results, "* 시험").Split('\n');

        Assert.Contains(lines, l => l.StartsWith("* 명세 완료율: 33.3% (1/3건)", StringComparison.Ordinal) && l.Contains("apply.json 없는 실행 2건", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("* 명세 완료율(작업 3~8개): 50.0% (1/2건)", StringComparison.Ordinal));
    }

    [Fact]
    public void Exit_code_precedence_is_cancelled_then_violation_then_error()
    {
        ApplyCaseResult Row(string outcome, bool? independent) =>
            new("x", 1, outcome, null, outcome == ApplyBenchmark.Errored ? 4 : 0, false, 1, [], independent, 0);
        var ok = Row(ApplyBenchmark.Completed, true);
        var violation = Row(ApplyBenchmark.Completed, false);
        var error = Row(ApplyBenchmark.Errored, null);

        Assert.Equal(0, ApplyBenchmark.ExitCode([ok], cancelled: false));
        Assert.Equal(4, ApplyBenchmark.ExitCode([ok, error], cancelled: false));
        Assert.Equal(4, ApplyBenchmark.ExitCode([error, error], cancelled: false));
        Assert.Equal(1, ApplyBenchmark.ExitCode([violation, error], cancelled: false));
        Assert.Equal(2, ApplyBenchmark.ExitCode([violation, error], cancelled: true));
    }

    [Fact]
    public void Summary_without_results_says_not_judged()
    {
        var outDir = Path.Combine(_root, "empty-out");
        var md = ApplyBenchmarkReport.Write(new FileSystem(), outDir, [], "* 시험");
        Assert.Contains("* 명세 완료율: 판정 불가 (0건)", md, StringComparison.Ordinal);
        Assert.Contains("- 없음", md, StringComparison.Ordinal);
    }

    private sealed class FixedBuild(IReadOnlyList<Diagnostic>? errors) : IGroupVerifier
    {
        public List<IReadOnlyList<string>> Calls { get; } = [];

        public Task<GroupBuild> BuildAsync(Workspace ws, IReadOnlyList<string> projects, CancellationToken ct)
        {
            Calls.Add(projects);
            return Task.FromResult(errors is null
                ? new GroupBuild(false, "그룹 빌드 검증 불가 (x): 빌드 도구 없음", GroupFailureKinds.Unverifiable, [])
                : new GroupBuild(true, null, null, errors));
        }
    }

    private sealed class NoProcesses : IProcessRunner
    {
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct) =>
            throw new InvalidOperationException($"unexpected process: {spec.FileName}");
    }
}
