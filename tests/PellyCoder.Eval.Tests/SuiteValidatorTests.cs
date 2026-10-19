using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Testing;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

public sealed class SuiteValidatorTests : IDisposable
{
    static SuiteValidatorTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private readonly string _tmp = Path.Combine(Path.GetTempPath(), "pelly-validate-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _work;
    private readonly string _suiteDir;
    private readonly Dictionary<string, string[]> _tasks = new()
    {
        ["cs-modern-001"] = ["id: cs-modern-001", "kind: cs", "file: App/Sdk/Calc.cs", "method: Add", "request: \"make it subtract\"",
            "judge:", "  build: true", "  mustContain: [\"a - b\"]", "  mustNotContain: [\"a + b\"]"],
        ["cs-legacy-001"] = ["id: cs-legacy-001", "kind: cs", "file: App/Old/Svc.cs", "method: Run", "request: \"double it\"",
            "judge:", "  build: true", "  mustContain: [\"x * 2\"]"],
        ["sql-001"] = ["id: sql-001", "kind: sql", "file: App/Db/usp_A.sql", "object: dbo.usp_A", "request: \"select two\"",
            "judge:", "  sqlCompile: true", "  mustContain: [\"SELECT 2\"]"],
    };
    private string? _supportJson;

    public SuiteValidatorTests()
    {
        _work = Path.Combine(_tmp, "work");
        _suiteDir = Path.Combine(_work, "suite");
        Directory.CreateDirectory(Path.Combine(_suiteDir, "tasks"));
        Write("App/Sdk/Sdk.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>");
        Write("App/Sdk/Calc.cs", "namespace N;\npublic class Calc\n{\n    public int Add(int a, int b)\n    {\n        return a + b;\n    }\n}\n");
        Write("App/Old/Old.csproj", "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"></Project>");
        Write("App/Old/Svc.cs", "namespace N;\npublic class Svc\n{\n    public int Run(int x)\n    {\n        return x + 1;\n    }\n}\n");
        Write("App/Db/usp_A.sql", "CREATE PROCEDURE dbo.usp_A\nAS\nBEGIN\n    SELECT 1;\nEND\nGO\n");
    }

    public void Dispose()
    {
        try { Directory.Delete(_tmp, recursive: true); }
        catch (IOException) { /* temp folder, best effort */ }
    }

    private void Write(string relative, string text) => WriteBytes(relative, new UTF8Encoding(false).GetBytes(text));

    private void WriteBytes(string relative, byte[] bytes)
    {
        var path = Path.Combine(_work, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
    }

    /// <summary>Edits one line of a base task: <paramref name="repl"/> null deletes it.</summary>
    private void Edit(string task, string find, string? repl)
    {
        var lines = _tasks[task].ToList();
        var i = lines.IndexOf(find);
        Assert.True(i >= 0, $"{task}: '{find}' 줄이 없음");
        if (repl is null) lines.RemoveAt(i); else lines[i] = repl;
        _tasks[task] = [.. lines.SelectMany(l => l.Split('\n'))];
    }

    private IReadOnlyList<SuiteIssue> Issues(int compat = 160)
    {
        foreach (var (name, lines) in _tasks)
            File.WriteAllText(Path.Combine(_suiteDir, "tasks", name + ".yaml"), string.Join("\n", lines) + "\n");
        File.WriteAllText(Path.Combine(_suiteDir, "suite.json"),
            "{ \"formatVersion\": 1, \"name\": \"t\", \"roots\": [ { \"name\": \"App\", \"path\": \"../App\" } ]" + (_supportJson ?? "") + " }");
        var load = BenchmarkSuite.Load(_suiteDir);
        Assert.True(load.Suite is not null, string.Join("; ", load.Errors));
        var (tasks, errors) = BenchmarkTask.TryLoadAll(load.Suite.TasksDir);
        return SuiteValidator.Validate(load.Suite, tasks, errors, new FileSystem(), compat);
    }

    private static void Has(IReadOnlyList<SuiteIssue> issues, IssueLevel level, string text) =>
        Assert.True(issues.Any(i => i.Level == level && i.Message.Contains(text, StringComparison.Ordinal)),
            $"{level} '{text}' 없음. 실제: " + string.Join(" | ", issues.Select(i => $"{i.Level} {i.TaskId}: {i.Message}")));

    [Fact]
    public void Valid_HasNoIssues() => Assert.Empty(Issues());

    [Theory]
    [InlineData("cs-modern-001", "id: cs-modern-001", "id: cs-modern-1", "id 형식")]
    [InlineData("cs-modern-001", "id: cs-modern-001", "id: cs-modern-002", "파일 이름")]
    [InlineData("cs-modern-001", "request: \"make it subtract\"", "request: \"\"", "request")]
    [InlineData("cs-modern-001", "method: Add", "method: Add\nline: 4", "method와 line 중 정확히 하나")]
    [InlineData("cs-modern-001", "method: Add", null, "method와 line 중 정확히 하나")]
    [InlineData("cs-modern-001", "method: Add", "method: Add\nobject: X", "object")]
    [InlineData("cs-modern-001", "  build: true", "  build: false", "judge.build")]
    [InlineData("cs-modern-001", "  build: true", "  build: true\n  sqlCompile: true", "sqlCompile")]
    [InlineData("cs-modern-001", "kind: cs", "kind: py", "알 수 없는 kind")]
    [InlineData("cs-modern-001", "method: Add", "method: Nope", "대상을 찾지 못함")]
    [InlineData("cs-modern-001", "method: Add", "line: 99", "줄 수")]
    [InlineData("cs-modern-001", "file: App/Sdk/Calc.cs", "file: App/Sdk/Missing.cs", "대상 파일이 없습니다")]
    [InlineData("cs-modern-001", "file: App/Sdk/Calc.cs", "file: Nope/Calc.cs", "알 수 없는 루트")]
    [InlineData("cs-modern-001", "  mustContain: [\"a - b\"]", "  mustContain: [\"a + b\"]", "mustContain 'a + b'")]
    [InlineData("cs-modern-001", "  mustNotContain: [\"a + b\"]", "  mustNotContain: [\"zzz\"]", "mustNotContain 'zzz'")]
    [InlineData("sql-001", "object: dbo.usp_A", "object: dbo.usp_A\nmethod: X", "method")]
    [InlineData("sql-001", "  sqlCompile: true", "  sqlCompile: false", "judge.sqlCompile")]
    [InlineData("sql-001", "  sqlCompile: true", "  sqlCompile: true\n  build: true", "build")]
    [InlineData("sql-001", "id: sql-001", "id: cs-modern-005", "sql-로 시작")]
    [InlineData("sql-001", "object: dbo.usp_A", "object: dbo.usp_Missing", "대상 객체 배치 0개")]
    [InlineData("cs-legacy-001", "  build: true", "  build: true\n  tests: [\"X\"]", "Legacy 작업에는 동작 판정을 둘 수 없음 (Windows에서도 생략됨)")]
    [InlineData("cs-legacy-001", "  build: true", "  build: true\n  failingTests: [\"X\"]", "Legacy 작업에는 동작 판정을 둘 수 없음")]
    public void SingleEdit_IsError(string task, string find, string? repl, string expected)
    {
        Edit(task, find, repl);
        Has(Issues(), IssueLevel.Error, expected);
    }

    [Fact]
    public void NoContentOrBehaviorCriterion_IsError()
    {
        Edit("cs-modern-001", "  mustContain: [\"a - b\"]", null);
        Edit("cs-modern-001", "  mustNotContain: [\"a + b\"]", null);
        Has(Issues(), IssueLevel.Error, "판정");
    }

    [Fact]
    public void YamlSyntaxError_NamesTheFile()
    {
        _tasks["cs-modern-009"] = ["id: [unclosed"];
        Has(Issues(), IssueLevel.Error, "cs-modern-009.yaml: YAML을 읽을 수 없음");
    }

    [Fact]
    public void MissingJudge_IsError()
    {
        _tasks["cs-modern-001"] = ["id: cs-modern-001", "kind: cs", "file: App/Sdk/Calc.cs", "method: Add", "request: \"r\""];
        Has(Issues(), IssueLevel.Error, "judge");
    }

    [Fact]
    public void DuplicateId_IsError()
    {
        _tasks["cs-modern-002"] = _tasks["cs-modern-001"];
        Has(Issues(), IssueLevel.Error, "중복");
    }

    [Fact]
    public void CsFileWithoutCsproj_IsError()
    {
        Write("App/Loose/Loose.cs", "namespace N;\npublic class Loose\n{\n    public int Add(int a, int b) => a + b;\n}\n");
        Edit("cs-modern-001", "file: App/Sdk/Calc.cs", "file: App/Loose/Loose.cs");
        Has(Issues(), IssueLevel.Error, "csproj를 찾지 못함");
    }

    [Fact]
    public void IdPrefix_DisagreeingWithProjectStyle_IsError()
    {
        Edit("cs-legacy-001", "file: App/Old/Svc.cs", "file: App/Sdk/Calc.cs");
        Edit("cs-legacy-001", "method: Run", "method: Add");
        Edit("cs-modern-001", "file: App/Sdk/Calc.cs", "file: App/Old/Svc.cs");
        Edit("cs-modern-001", "method: Add", "method: Run");
        var issues = Issues();
        Has(issues, IssueLevel.Error, "cs-legacy 작업인데 대상 프로젝트가 SDK 형식");
        Has(issues, IssueLevel.Error, "cs-modern 작업인데 대상 프로젝트가 Legacy 형식");
    }

    [Fact]
    public void SqlTargetWithSyntaxError_IsError()
    {
        Write("App/Db/usp_A.sql", "CREATE PROCEDURE dbo.usp_A\nAS\nBEGIN\n    SELECT 1;\nGO\n");
        Has(Issues(), IssueLevel.Error, "구문 오류");
    }

    [Fact]
    public void BuildInputAbove_NotInSupportFiles_WarnsAndWalksPastTheCommonAncestor()
    {
        // 스위트 폴더(work/suite)와 루트(work/App)의 공통 조상은 work다. 파일은 그보다 위(_tmp)에 둔다.
        File.WriteAllText(Path.Combine(_tmp, "Directory.Build.props"), "<Project />");
        var issues = Issues();
        Has(issues, IssueLevel.Warning, "루트 밖 빌드 입력: " + Path.Combine(_tmp, "Directory.Build.props")
            + " — supportFiles에 넣지 않으면 작업 복사본에 없습니다");
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
    }

    [Fact]
    public void BuildInputAbove_ListedInSupportFiles_IsQuiet()
    {
        File.WriteAllText(Path.Combine(_tmp, "global.json"), "{}");
        File.WriteAllText(Path.Combine(_work, "global.json"), "{}"); // 같은 이름의 지원 파일(대소문자 무시 비교)
        _supportJson = ", \"supportFiles\": [ \"../GLOBAL.JSON\" ]";
        Assert.DoesNotContain(Issues(), i => i.Message.Contains("루트 밖 빌드 입력", StringComparison.Ordinal) && i.Message.Contains("global.json", StringComparison.Ordinal));
    }

    [Fact]
    public void PackagesConfig_WithoutPackagesFolder_Warns_AndWithOneIsQuiet()
    {
        Write("App/Old/packages.config", "<packages />");
        Has(Issues(), IssueLevel.Warning, "packages.config가 있지만 packages/ 폴더가 없습니다 (폐쇄망에서 복원할 수 없음)");
        Directory.CreateDirectory(Path.Combine(_work, "App", "packages"));
        Assert.DoesNotContain(Issues(), i => i.Message.Contains("packages.config", StringComparison.Ordinal));
    }

    [Fact]
    public void EmptyCategory_Warns()
    {
        _tasks.Remove("sql-001");
        var issues = Issues();
        Has(issues, IssueLevel.Warning, "sql");
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
    }

    [Fact]
    public void Validate_Cp949SourceWithCrlf_FindsMethod()
    {
        var text = "namespace N;\r\npublic class Calc\r\n{\r\n    // 합계를 구한다\r\n    public int Add(int a, int b)\r\n    {\r\n        return a + b;\r\n    }\r\n}\r\n";
        WriteBytes("App/Sdk/Calc.cs", Encoding.GetEncoding(949).GetBytes(text));
        Assert.Empty(Issues());
    }

    [Fact]
    public void Validate_Cp949SqlWithCrlf_FindsObject()
    {
        var text = "-- 주문 조회\r\nCREATE PROCEDURE dbo.usp_A\r\nAS\r\nBEGIN\r\n    SELECT 1;\r\nEND\r\nGO\r\n";
        WriteBytes("App/Db/usp_A.sql", Encoding.GetEncoding(949).GetBytes(text));
        Assert.Empty(Issues());
    }

    [Fact]
    public void SchemaIssues_NeedsNoFiles()
    {
        var task = new BenchmarkTask { Id = "cs-modern-001", Kind = "cs", File = "App/x.cs", Method = "M", Request = "r", Judge = new BenchmarkJudge { Build = true } };
        Assert.Empty(SuiteValidator.SchemaIssues(task, "cs-modern-001"));
        task.Judge.Build = null;
        Assert.Contains(SuiteValidator.SchemaIssues(task, null), i => i.Level == IssueLevel.Error);
    }

    [Fact]
    public void ValidateCommand_PrintsHeaderTableAndTotals_AndExitsByErrors()
    {
        Edit("cs-modern-001", "request: \"make it subtract\"", "request: \"\"");
        Issues(); // writes the files
        var output = new StringWriter();
        var code = ValidateCommand.Run(EvalArguments.Parse(["validate", "--suite", _suiteDir]), output, new StringWriter());

        Assert.Equal(4, code);
        var lines = output.ToString().Replace("\r\n", "\n").Split('\n');
        Assert.Equal($"스위트 t ({_suiteDir}) · 작업 3건 · cs-legacy 1 · cs-modern 1 · sql 1", lines[0]);
        Assert.Contains("| 레벨 | 작업 | 내용 |", lines);
        Assert.Contains(lines, l => l.StartsWith("| 오류 | cs-modern-001 | request", StringComparison.Ordinal));
        Assert.Contains("오류 1건 · 경고 0건", lines);
    }

    [Fact]
    public void ValidateCommand_WithoutIssues_ExitsZero()
    {
        Issues();
        var output = new StringWriter();
        Assert.Equal(0, ValidateCommand.Run(EvalArguments.Parse(["validate", "--suite", _suiteDir]), output, new StringWriter()));
        Assert.Contains("오류 0건 · 경고 0건", output.ToString());
    }

    [Fact]
    public void ValidateCommand_UnreadableSuite_Exits4()
    {
        var error = new StringWriter();
        Assert.Equal(4, ValidateCommand.Run(EvalArguments.Parse(["validate", "--suite", Path.Combine(_tmp, "none")]), new StringWriter(), error));
        Assert.Contains("suite.json", error.ToString());
    }

    // ---- --deep ----

    private sealed class NoProcesses : IProcessRunner
    {
        public List<ProcessSpec> Calls { get; } = [];
        public int ExitCode { get; set; }
        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Calls.Add(spec);
            return Task.FromResult(new ProcessResult(ExitCode, "", "boom", [], TimeSpan.Zero, false));
        }
    }

    private (BenchmarkSuite Suite, List<SuiteTask> Tasks) DeepTasks(string judgeLines)
    {
        _tasks["cs-modern-001"] = ["id: cs-modern-001", "kind: cs", "file: App/Sdk/Calc.cs", "method: Add", "request: \"r\"",
            "judge:", "  build: true", .. judgeLines.Split('\n')];
        Issues(); // writes the files
        var suite = BenchmarkSuite.Load(_suiteDir).Suite!;
        var fs = new FileSystem();
        var tasks = BenchmarkTask.TryLoadAll(suite.TasksDir).Tasks.Select(t => suite.Resolve(t, fs, out _)!).ToList();
        return (suite, tasks);
    }

    private async Task<(IReadOnlyList<SuiteIssue> Issues, List<string> Roots)> DeepAsync(string judgeLines, JudgeTestRun run, NoProcesses? procs = null)
    {
        var (suite, tasks) = DeepTasks(judgeLines);
        var roots = new List<string>();
        var scratch = Path.Combine(_tmp, "scratch");
        var issues = await SuiteValidator.ValidateJudgesAsync(suite, tasks, (_, _, root, _) =>
        {
            roots.Add(root);
            return Task.FromResult(run);
        }, procs ?? new NoProcesses(), scratch, TestContext.Current.CancellationToken);
        return (issues, roots);
    }

    private static JudgeTestRun Run(params (string Name, bool Passed)[] results) =>
        new(results.ToDictionary(r => r.Name, r => new TestCaseResult(r.Name, r.Passed, null)), null);

    [Fact]
    public async Task Deep_JudgeTestPassingOnOriginal_IsError()
    {
        var (issues, roots) = await DeepAsync("  tests: [\"T.A\"]", Run(("T.A", true)));
        Has(issues, IssueLevel.Error, "판정 테스트가 원본에서 이미 통과: T.A (빈 판정)");
        Assert.Equal("cs-modern-001", issues.Single().TaskId);
        Assert.EndsWith("App", roots.Single());
    }

    [Fact]
    public async Task Deep_FailingJudgeTest_IsFine()
    {
        var (issues, _) = await DeepAsync("  tests: [\"T.A\"]", Run(("T.A", false)));
        Assert.Empty(issues);
    }

    [Fact]
    public async Task Deep_FailingTestFailingOnOriginal_IsError()
    {
        var (issues, _) = await DeepAsync("  failingTests: [\"T.P\"]", Run(("T.P", false)));
        Has(issues, IssueLevel.Error, "수정 전 동작 테스트가 원본에서 실패: T.P");
    }

    [Fact]
    public async Task Deep_NameMissingFromResults_IsError()
    {
        var (issues, _) = await DeepAsync("  tests: [\"T.A\"]\n  failingTests: [\"T.P\"]", Run());
        Has(issues, IssueLevel.Error, "결과에 없음: T.A");
        Has(issues, IssueLevel.Error, "결과에 없음: T.P");
    }

    [Fact]
    public async Task Deep_ToolingError_IsWarning_NotError()
    {
        var run = new JudgeTestRun(new Dictionary<string, TestCaseResult>(), "복원 실패", ToolingError: true);
        var (issues, _) = await DeepAsync("  tests: [\"T.A\"]", run);
        Has(issues, IssueLevel.Warning, "판정 도구 오류로 확인하지 못함: 복원 실패");
        Assert.DoesNotContain(issues, i => i.Level == IssueLevel.Error);
    }

    [Fact]
    public async Task Deep_SkipsTasksWithoutJudgeTests_AndLegacy()
    {
        var (suite, tasks) = DeepTasks("  mustContain: [\"a - b\"]");
        var legacy = tasks.Single(t => t.Task.Id == "cs-legacy-001");
        legacy.Task.Judge!.Tests = ["T.X"]; // Validate would reject it; the deep path must still not run it
        var calls = 0;
        var issues = await SuiteValidator.ValidateJudgesAsync(suite, tasks, (_, _, _, _) =>
        {
            calls++;
            return Task.FromResult(Run());
        }, new NoProcesses(), Path.Combine(_tmp, "scratch"), TestContext.Current.CancellationToken);
        Assert.Equal(0, calls);
        Assert.Empty(issues);
    }

    [Fact]
    public async Task Deep_DeletesScratchCopies()
    {
        var scratch = Path.Combine(_tmp, "scratch");
        await DeepAsync("  tests: [\"T.A\"]", Run(("T.A", false)));
        Assert.True(!Directory.Exists(scratch) || Directory.GetFileSystemEntries(scratch).Length == 0);
    }

    [Fact]
    public async Task Deep_RestoreFailure_IsWarning_AndSkipsTheTask()
    {
        Write("App/App.sln", "");
        var procs = new NoProcesses { ExitCode = 1 };
        var (issues, roots) = await DeepAsync("  tests: [\"T.A\"]", Run(("T.A", true)), procs);
        Assert.Empty(roots);
        Assert.Equal("restore", procs.Calls.Single().Arguments[0]);
        Assert.Equal(IssueLevel.Warning, issues.Single().Level);
    }

    [Fact]
    public void Deep_OnlyValidateAcceptsTheOption()
    {
        Assert.True(EvalArguments.Parse(["validate", "--deep"]).Deep);
        Assert.NotNull(EvalArguments.Parse(["--deep"]).Error);
        Assert.NotNull(EvalArguments.Parse(["compare", "--deep", "a", "b"]).Error);
    }

    [Fact]
    public void ValidateCommand_Deep_WithStaticErrors_DoesNotRun_Exits4()
    {
        Edit("cs-modern-001", "request: \"make it subtract\"", "request: \"\"");
        Issues();
        var procs = new NoProcesses();
        var output = new StringWriter();
        Assert.Equal(4, ValidateCommand.Run(EvalArguments.Parse(["validate", "--deep", "--suite", _suiteDir]), output, new StringWriter(), processes: procs));
        Assert.Empty(procs.Calls);
        Assert.Contains("깊은 검사", output.ToString());
    }
}
