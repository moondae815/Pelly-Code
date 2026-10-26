using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
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

    private ApplyBenchSetup Setup(Func<RunRequest, CancellationToken, Task<RunResult>> run, IGroupVerifier verifier, bool isWindows = true, bool sql = true,
        IProcessRunner? processes = null)
    {
        var fs = new FileSystem();
        var options = Assert.IsType<PellyOptions>(
            PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), new Dictionary<string, string?>()).Options);
        return new ApplyBenchSetup(fs, options, processes ?? new NoProcesses(), TimeProvider.System, () => isWindows, sql, _ => run, verifier,
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

    // --- V2-2c: test-first case folders (apply-test-first/<id>/), the files/ overlay and the independent test re-run ---

    private const string CalcTests =
        "namespace Demo.Tests;\n\npublic class CalcTests\n{\n    [Fact]\n    public void Add_checks_overflow() { }\n}\n";

    private string TestFirstDir => Path.Combine(_root, "apply-test-first");

    private static string FolderSpec(string id, string scope = "Modern") =>
        $"---\nid: {id}\nstatus: draft\nscope: [{scope}]\n---\n# 시험 명세\n\n## 수용 기준\n- AC1: 더하기가 오버플로를 검사한다\n"
        + "  test: CalcTests.Add_checks_overflow\n\n## 작업\n- [ ] T1 cs Modern/App/Calc.cs#Add 오버플로를 검사한다 (AC1)\n";

    /// <summary>A case folder: its spec, the RED test project under <c>files/</c> and an expected.json.</summary>
    private string Folder(string id, string? spec = null, bool files = true)
    {
        var dir = Path.Combine(TestFirstDir, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "spec.md"), spec ?? FolderSpec(id));
        File.WriteAllText(Path.Combine(dir, "expected.json"), "[{ \"acId\": \"AC1\", \"red\": \"Red\", \"outcome\": \"Met\" }]\n");
        if (files)
        {
            var tests = Path.Combine(dir, "files", "Modern", "App.Tests");
            Directory.CreateDirectory(tests);
            File.WriteAllText(Path.Combine(tests, "App.Tests.csproj"),
                "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
            File.WriteAllText(Path.Combine(tests, "CalcTests.cs"), CalcTests);
        }
        return dir;
    }

    private void DefaultCase(string id)
    {
        var dir = Path.Combine(_root, "apply");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".md"),
            $"---\nid: {id}\nstatus: draft\nscope: [Modern]\n---\n# 시험\n\n## 작업\n{ThreeTasks[0]}\n");
    }

    private IReadOnlyList<ApplyCase> LoadSet(string set, string? glob = null) =>
        ApplyBenchmark.LoadSet(Path.Combine(_root, "apply"), TestFirstDir, set, glob);

    [Fact]
    public void Set_Default_OnlyExistingCases()
    {
        DefaultCase("a1-one");
        Folder("t1");
        Assert.Equal("default", EvalArguments.Parse(["apply"]).Set);
        Assert.Equal(["a1-one"], LoadSet("default").Select(c => c.Id));
        // The default set never reads the folder cases: a missing folder is no error.
        Directory.Delete(TestFirstDir, recursive: true);
        Assert.Equal(["a1-one"], LoadSet("default").Select(c => c.Id));
    }

    [Fact]
    public void Set_TestFirst_OnlyFolders()
    {
        DefaultCase("a1-one");
        Folder("t2");
        Folder("t1");
        Assert.Equal("test-first", EvalArguments.Parse(["apply", "--set", "test-first"]).Set);
        var cases = LoadSet("test-first");
        Assert.Equal(["t1", "t2"], cases.Select(c => c.Id));
        Assert.Equal(Path.Combine(TestFirstDir, "t1", "files"), cases[0].FilesDir);
        Assert.Equal([new ExpectedCriterion("AC1", "Red", "Met")], cases[0].Expected!);
        Assert.Equal((1, false, false), (cases[0].TaskCount, cases[0].HasSql, cases[0].HasGroup));
        Assert.Equal(["Modern"], cases[0].Scope);
        Assert.Equal(["t2"], LoadSet("test-first", "t2").Select(c => c.Id));
    }

    [Fact]
    public void Set_All_Both()
    {
        DefaultCase("a1-one");
        Folder("t1");
        Assert.Equal("all", EvalArguments.Parse(["apply", "--set", "all"]).Set);
        Assert.Equal("--set는 default, test-first, all 중 하나: x", EvalArguments.Parse(["apply", "--set", "x"]).Error);
        Assert.Equal("알 수 없는 인자: --set", EvalArguments.Parse(["run", "--set", "all"]).Error);
        Assert.Equal(["a1-one", "t1"], LoadSet("all").Select(c => c.Id));
        Assert.Equal(["t1"], LoadSet("all", "t*").Select(c => c.Id));
    }

    [Fact]
    public void LoadTestFirstCases_IdMustMatchFolder()
    {
        Folder("t1", spec: FolderSpec("t9"));
        var ex = Assert.Throws<InvalidDataException>(() => ApplyBenchmark.LoadTestFirstCases(TestFirstDir, null, new HashSet<string>()));
        Assert.Contains("t1", ex.Message, StringComparison.Ordinal);
        Assert.Contains("폴더 이름", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadTestFirstCases_ApprovedFlag()
    {
        Folder("t1");
        Folder("t2");
        // No APPROVED.json: nothing is approved.
        Assert.Empty(ApplyBenchmark.LoadApproved(TestFirstDir));
        Assert.All(LoadSet("test-first"), c => Assert.False(c.Approved));

        File.WriteAllText(Path.Combine(TestFirstDir, "APPROVED.json"),
            "{ \"approvedBy\": \"someone\", \"approvedAt\": \"2026-10-26\", \"cases\": [\"t1\"] }\n");
        var approved = ApplyBenchmark.LoadApproved(TestFirstDir);
        Assert.Equal(["t1"], approved.Order(StringComparer.Ordinal));
        Assert.Equal([("t1", true), ("t2", false)],
            ApplyBenchmark.LoadTestFirstCases(TestFirstDir, null, approved).Select(c => (c.Id, c.Approved)));
        // The flat cases are approved S6 gold.
        DefaultCase("a1-one");
        Assert.True(Assert.Single(LoadSet("default")).Approved);
    }

    [Fact]
    public async Task Apply_FilesOverlayCopied()
    {
        Folder("t1");
        var c = Assert.Single(LoadSet("test-first"));
        string? seen = null;
        var run = EditingRun(_ => seen = File.ReadAllText(Path.Combine(Out, "work", "t1-1", "corpus", "Modern", "App.Tests", "CalcTests.cs")));

        var result = await ApplyBenchmark.RunCaseAsync(c, 1, Setup(run, new FixedBuild([]), processes: new TrxProcesses(_ => true)), Out,
            TestContext.Current.CancellationToken);

        Assert.Equal(CalcTests, seen); // overlaid before the apply ran
        Assert.True(File.Exists(Path.Combine(Out, "work", "t1-1", "corpus", "Modern", "App", "Calc.cs")));
        Assert.Equal(ApplyBenchmark.Completed, result.Outcome);
    }

    [Fact]
    public async Task Apply_FilesOverlayIsBaselineForChangedCs()
    {
        Folder("t1");
        // An overlaid copy of a corpus file is its own baseline too: overlay Calc.cs with the original text plus a comment.
        var overlaid = Path.Combine(TestFirstDir, "t1", "files", "Modern", "App");
        Directory.CreateDirectory(overlaid);
        File.WriteAllText(Path.Combine(overlaid, "Calc.cs"), Source + "// overlaid\n");
        var c = Assert.Single(LoadSet("test-first"));
        var verifier = new FixedBuild([]);
        // The run leaves Calc.cs as overlaid and edits nothing else: no C# change against the overlay baseline.
        Func<RunRequest, CancellationToken, Task<RunResult>> noEdit = (r, _) => Task.FromResult(new RunResult(0, "run-x", r.FilePath, null, null, null));

        var unchanged = await ApplyBenchmark.RunCaseAsync(c, 1, Setup(noEdit, verifier, processes: new TrxProcesses(_ => true)), Out,
            TestContext.Current.CancellationToken);
        Assert.Equal(ApplyBenchmark.NoCsChange, unchanged.IndependentDetail);
        Assert.Empty(verifier.Calls);

        // An edit of Calc.cs: only its project is rebuilt, never the overlaid test project.
        var edited = await ApplyBenchmark.RunCaseAsync(c, 2, Setup(EditingRun(), verifier, processes: new TrxProcesses(_ => true)), Out,
            TestContext.Current.CancellationToken);
        Assert.True(edited.IndependentBuildOk);
        Assert.Equal([Path.Combine(Out, "work", "t1-2", "corpus", "Modern", "App", "App.csproj")], Assert.Single(verifier.Calls));
    }

    [Fact]
    public async Task Apply_MetCriterion_IndependentTestFails_Mismatch()
    {
        Folder("t1");
        var c = Assert.Single(LoadSet("test-first"));
        var ct = TestContext.Current.CancellationToken;

        var failing = new TrxProcesses(_ => false);
        var mismatch = await ApplyBenchmark.RunCaseAsync(c, 1, Setup(EditingRun(), new FixedBuild([]), processes: failing), Out, ct);
        Assert.Equal((0, ApplyBenchmark.Completed), (mismatch.ExitCode, mismatch.Outcome));
        var ac = Assert.Single(mismatch.Criteria!);
        Assert.Equal(("AC1", AcOutcome.Met), (ac.AcId, ac.Outcome));
        Assert.Equal(1, mismatch.TestJudgeMismatches);
        Assert.Equal(ApplyBenchmark.TestMismatch, Assert.Single(mismatch.TestChecks!).Verdict);
        // The declaring project (found by TestMethodIndex in the work copy) was built and run outside pelly.
        Assert.Contains(failing.Calls, s => s.Arguments.Contains("build") && s.Arguments.Any(a => a.EndsWith("App.Tests.csproj", StringComparison.Ordinal)));
        Assert.Contains(failing.Calls, s => s.Arguments.Contains("test"));
        Assert.Equal(1, ApplyBenchmark.ExitCode([mismatch], cancelled: false));
        Assert.Equal(1, ApplyBenchmark.ExitCode([mismatch, mismatch with { Outcome = ApplyBenchmark.Errored }], cancelled: false));
        Assert.Equal(2, ApplyBenchmark.ExitCode([mismatch], cancelled: true));

        var passing = await ApplyBenchmark.RunCaseAsync(c, 2, Setup(EditingRun(), new FixedBuild([]), processes: new TrxProcesses(_ => true)), Out, ct);
        Assert.Equal(0, passing.TestJudgeMismatches);
        Assert.Equal(ApplyBenchmark.TestPassed, Assert.Single(passing.TestChecks!).Verdict);
        Assert.Equal(0, ApplyBenchmark.ExitCode([passing], cancelled: false));
    }

    [Fact]
    public async Task Apply_MetCriterion_TestBuild_CompilerErrorIsMismatch_OtherErrorUnverifiable()
    {
        Folder("t1");
        var c = Assert.Single(LoadSet("test-first"));
        var ct = TestContext.Current.CancellationToken;
        var project = Path.Combine("Modern", "App.Tests", "App.Tests.csproj");
        var file = Path.Combine("Modern", "App.Tests", "CalcTests.cs");

        var compile = await ApplyBenchmark.RunCaseAsync(c, 1, Setup(EditingRun(), new FixedBuild([]),
            processes: new TrxProcesses(_ => true, $"{file}(6,9): error CS0103: The name 'x' does not exist [{project}]")), Out, ct);
        var check = Assert.Single(compile.TestChecks!);
        Assert.Equal(ApplyBenchmark.TestMismatch, check.Verdict);
        Assert.Contains("CS0103", check.Detail, StringComparison.Ordinal);
        Assert.Equal(1, compile.TestJudgeMismatches);

        var restore = await ApplyBenchmark.RunCaseAsync(c, 2, Setup(EditingRun(), new FixedBuild([]),
            processes: new TrxProcesses(_ => true, $"/sdk/Microsoft.PackageDependencyResolution.targets(266,5): error NETSDK1004: Assets file not found [{project}]")), Out, ct);
        Assert.Equal(ApplyBenchmark.TestUnverifiable, Assert.Single(restore.TestChecks!).Verdict);
        Assert.Equal(0, restore.TestJudgeMismatches);
    }

    [Fact]
    public async Task Apply_LegacyTestProject_ReRunSkippedOffWindows()
    {
        Folder("t1");
        File.WriteAllText(Path.Combine(TestFirstDir, "t1", "files", "Modern", "App.Tests", "App.Tests.csproj"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n"
            + "  <PropertyGroup>\n    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>\n  </PropertyGroup>\n</Project>\n");
        var c = Assert.Single(LoadSet("test-first"));

        var result = await ApplyBenchmark.RunCaseAsync(c, 1, Setup(EditingRun(), new FixedBuild([]), isWindows: false, processes: new TrxProcesses(_ => false)),
            Out, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.TestJudgeMismatches);
        var check = Assert.Single(result.TestChecks!);
        Assert.Equal(ApplyBenchmark.TestSkipped, check.Verdict);
        Assert.Contains("Windows", check.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_PreliminaryTitle_WhenUnapproved()
    {
        Folder("t1");
        Folder("t2");
        File.WriteAllText(Path.Combine(TestFirstDir, "APPROVED.json"), "{ \"cases\": [\"t1\", \"t2\"] }\n");
        var approved = LoadSet("test-first");
        var md = ApplyBenchmarkReport.Write(new FileSystem(), Out, [], "* 시험", approved);
        Assert.StartsWith("# pelly-eval apply 결과\n", md, StringComparison.Ordinal);

        File.WriteAllText(Path.Combine(TestFirstDir, "APPROVED.json"), "{ \"cases\": [\"t1\"] }\n");
        var preliminary = ApplyBenchmarkReport.Write(new FileSystem(), Path.Combine(_root, "out2"), [], "* 시험", LoadSet("test-first"));
        Assert.StartsWith("# pelly-eval apply 결과 — 예비(사람 승인 전)\n", preliminary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_CriteriaRowsAndPerCaseTable()
    {
        Folder("t1");
        var c = Assert.Single(LoadSet("test-first"));
        var ct = TestContext.Current.CancellationToken;
        List<ApplyCaseResult> results =
        [
            await ApplyBenchmark.RunCaseAsync(c, 1, Setup(EditingRun(), new FixedBuild([]), processes: new TrxProcesses(_ => false)), Out, ct),
            await ApplyBenchmark.RunCaseAsync(c, 2, Setup(EditingRun(), new FixedBuild([]), processes: new TrxProcesses(_ => true)), Out, ct),
        ];

        var md = ApplyBenchmarkReport.Write(new FileSystem(), Out, results, "* 시험", [c]);
        var lines = md.Split('\n');

        // The fake run never judges RED (NotChecked): the criteria are counted the way pelly report counts them.
        var data = ApplyReport.Build(results.SelectMany(r =>
        {
            var corpus = ApplyBenchmark.CorpusOf(ApplyBenchmark.CaseDir(Out, r.Id, r.Repeat));
            return ApplyHistory.Load(new FileSystem(), new Workspace(corpus, Path.Combine(corpus, ".pelly")), null).Applies;
        }).ToList());
        Assert.NotNull(data.Criteria);
        Assert.Contains("## 테스트 우선", lines);
        Assert.Contains("| 수용 기준 충족률 | 판정 불가 (0건) |", lines);
        Assert.Contains("| RED 확인 경고율 | 판정 불가 (0건) |", lines);
        Assert.Contains("| 테스트로 검증되지 않은 수용 기준 | 0건 |", lines);
        Assert.Contains("| breaks로 뺀 테스트 | 0건 |", lines);
        Assert.Contains("| 테스트 판정 불일치 | 1건 |", lines);

        Assert.Contains("## 사례별 수용 기준", lines);
        Assert.Contains("| 사례 | 회차 | AC | 기대 RED/결과 | 실제 RED/결과 | 독립 테스트 |", lines);
        Assert.Contains("| t1 | 1 | AC1 | Red/Met | NotChecked/Met | 불일치: 실패: Demo.Tests.CalcTests.Add_checks_overflow |", lines);
        Assert.Contains("| t1 | 2 | AC1 | Red/Met | NotChecked/Met | 통과 |", lines);

        // The new fields are in apply-results.json only for the rows that have them.
        var json = File.ReadAllText(Path.Combine(Out, "apply-results.json"));
        Assert.Contains("\"testJudgeMismatches\": 1", json, StringComparison.Ordinal);
        Assert.Contains("\"criteria\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Summary_CriteriaRows_CountMetAndRed()
    {
        // Rates from apply.json records written by hand (RED judged), through the same ApplyReport.Build as pelly report.
        var ct = TestContext.Current.CancellationToken;
        Folder("t1");
        var c = Assert.Single(LoadSet("test-first"));
        var r = await ApplyBenchmark.RunCaseAsync(c, 1, Setup(EditingRun(), new FixedBuild([]), processes: new TrxProcesses(_ => true)), Out, ct);
        var corpus = ApplyBenchmark.CorpusOf(ApplyBenchmark.CaseDir(Out, "t1", 1));
        var applyJson = Directory.GetFiles(Path.Combine(corpus, ".pelly", "runs"), "apply.json", SearchOption.AllDirectories).Single();
        File.WriteAllText(applyJson, File.ReadAllText(applyJson).Replace("\"NotChecked\"", "\"Red\"", StringComparison.Ordinal));

        var lines = ApplyBenchmarkReport.Write(new FileSystem(), Out, [r], "* 시험", [c]).Split('\n');

        Assert.Contains("| 수용 기준 충족률 | 100.0% (1/1건) |", lines);
        Assert.Contains("| RED 확인 경고율 | 0.0% (0/1건) |", lines);
        Assert.Contains("| 테스트 판정 불일치 | 0건 |", lines);
    }

    [Fact]
    public async Task ApplyDefault_SummaryUnchanged()
    {
        var setup = Setup(EditingRun(), new FixedBuild([]), isWindows: false);
        var ct = TestContext.Current.CancellationToken;
        var failing = ThreeTasks.Take(2).Append("- [ ] T3 cs Modern/App/Calc.cs#Mul 실패하는 작업이다").ToArray();
        List<ApplyCaseResult> results =
        [
            await ApplyBenchmark.RunCaseAsync(Case("s1-three", "Modern", ThreeTasks), 1, setup, Out, ct),
            await ApplyBenchmark.RunCaseAsync(Case("s2-fails", "Modern", failing), 1, setup, Out, ct),
            await ApplyBenchmark.RunCaseAsync(Case("s4-legacy", "Legacy", "- [ ] T1 cs Legacy/App/Calc.cs#Add 오버플로를 검사한다"), 1, setup, Out, ct),
        ];
        results = results.Select((r, i) => r with { Seconds = i + 0.25 }).ToList();
        DefaultCase("a1-one");

        var md = ApplyBenchmarkReport.Write(new FileSystem(), Out, results, "* 시험", LoadSet("default"));

        // The 12-case format as it was before V2-2c (no test-first section, no title suffix), byte for byte.
        const string Expected =
            "# pelly-eval apply 결과\n"
            + "\n"
            + "* 시험\n"
            + "\n"
            + "## 요약\n"
            + "* 명세 완료율: 50.0% (1/2건)\n"
            + "* 명세 완료율(작업 3~8개): 50.0% (1/2건)\n"
            + "* 그룹 빌드 통과율: 판정 불가 (0건)\n"
            + "* 독립 빌드(종료 0 사례 1건): 통과 1 · 위반 0 · 독립 검증 불가 0 · C# 변경 없음 0\n"
            + "* 사례 실행 3건: 완료 1 · 실패 1 · 실행기 오류 0 · 건너뜀 1\n"
            + "\n"
            + "## 건너뛴 사례\n"
            + "- s4-legacy (회차 1): Windows 전용\n"
            + "\n"
            + "## 사례별 결과\n"
            + "| 사례 | 회차 | 결과 | 종료 | 작업 | 보관 | 독립 빌드 | 시간(초) |\n"
            + "| :--- | ---: | :--- | ---: | ---: | :--- | :--- | ---: |\n"
            + "| s1-three | 1 | completed | 0 | 3 | 예 | 통과 | 0.3 |\n"
            + "| s2-fails | 1 | failed | 1 | 3 | 아니오 | - | 1.3 |\n"
            + "| s4-legacy | 1 | skipped | - | 1 | - | - | 2.3 |\n";
        Assert.Equal(Expected, md);
        var json = File.ReadAllText(Path.Combine(Out, "apply-results.json"));
        Assert.DoesNotContain("criteria", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("testJudge", json, StringComparison.Ordinal);
        Assert.DoesNotContain("testChecks", json, StringComparison.Ordinal);
    }

    /// <summary>
    /// Builds succeed without output; a <c>dotnet test</c> run writes a TRX in which every test of <see cref="CalcTests"/> passes
    /// when <paramref name="passes"/> says so.
    /// </summary>
    private sealed class TrxProcesses(Func<string, bool> passes, string? buildError = null) : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Calls.Add(spec);
            if (spec.Arguments.FirstOrDefault() == "test")
            {
                var trx = spec.Arguments.Single(a => a.StartsWith("trx;LogFileName=", StringComparison.Ordinal))["trx;LogFileName=".Length..];
                const string Name = "Demo.Tests.CalcTests.Add_checks_overflow";
                var passed = passes(Name);
                File.WriteAllText(trx,
                    "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>"
                    + $"<UnitTestResult testName=\"{Name}\" outcome=\"{(passed ? "Passed" : "Failed")}\" />"
                    + $"</Results><ResultSummary outcome=\"{(passed ? "Completed" : "Failed")}\" /></TestRun>");
                return Task.FromResult(new ProcessResult(passed ? 0 : 1, "", "", [], TimeSpan.Zero, false));
            }
            if (buildError is not null && spec.Arguments.FirstOrDefault() == "build")
                return Task.FromResult(new ProcessResult(1, buildError, "", System.Text.Encoding.UTF8.GetBytes(buildError), TimeSpan.Zero, false));
            return Task.FromResult(new ProcessResult(0, "", "", [], TimeSpan.Zero, false));
        }
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
