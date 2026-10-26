using System.IO.Abstractions;
using System.Security;
using System.Text;
using System.Text.Json;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>
/// <c>pelly cs --verify-test</c> up to the first write: LOCATE's preconditions, the test project union and APPLY's RED check
/// (design §5.2a, §5.3, §6).
/// </summary>
public sealed class TestFirstFlowTests : IDisposable
{
    private static readonly TimeSpan WallClock = TimeSpan.FromSeconds(60);

    private const string CalcSource =
        "namespace Demo;\n" +
        "\n" +
        "public class Calc\n" +
        "{\n" +
        "    public int Divide(int a, int b)\n" +
        "    {\n" +
        "        return a / b;\n" +
        "    }\n" +
        "}\n";

    private const string TestSource =
        "using Xunit;\n" +
        "\n" +
        "namespace Demo.Tests;\n" +
        "\n" +
        "public class CalcTests\n" +
        "{\n" +
        "    [Fact]\n" +
        "    public void Divide_ByZero_Throws()\n" +
        "    {\n" +
        "        Assert.Throws<System.DivideByZeroException>(() => new Calc().Divide(1, 0));\n" +
        "    }\n" +
        "}\n";

    private const string Original = "        return a / b;";
    private const string Guarded = "        if (b == 0) throw new DivideByZeroException();\n        return a / b;";
    private const string Wrong = "        return b == 0 ? 0 : a / b;";
    private const string GreenRequest = "The test cannot be changed. Make it pass by implementing the request.";

    private const string Name = "Divide_ByZero_Throws";
    private const string FullName = "Demo.Tests.CalcTests.Divide_ByZero_Throws";
    private const string OtherTest = "Demo.Tests.CalcTests.Divide_Positive";
    private const string ThirdTest = "Demo.Tests.CalcTests.Divide_Negative";

    private const string Sln =
        "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Demo\", \"Demo\\Demo.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\n" +
        "EndProject\n" +
        "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Demo.Tests\", \"Demo.Tests\\Demo.Tests.csproj\", \"{22222222-2222-2222-2222-222222222222}\"\n" +
        "EndProject\n";

    private const string TestCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\"..\\Demo\\Demo.csproj\" />\n  </ItemGroup>\n</Project>\n";

    private const string LegacyCsproj =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
        "  <PropertyGroup>\n    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>\n  </PropertyGroup>\n</Project>\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-test-first-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly ScriptedProcessRunner _processes = new();
    private readonly PellyOptions _options = new();
    private Func<bool>? _isWindows;

    public TestFirstFlowTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Demo"));
        Directory.CreateDirectory(Path.Combine(_root, "Demo.Tests"));
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), Sln);
        File.WriteAllText(CsprojPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(TestCsprojPath, TestCsproj);
        File.WriteAllText(CalcPath, CalcSource);
        File.WriteAllText(TestSourcePath, TestSource);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string CalcPath => Path.Combine(_root, "Demo", "Calc.cs");
    private string CsprojPath => Path.Combine(_root, "Demo", "Demo.csproj");
    private string TestCsprojPath => Path.Combine(_root, "Demo.Tests", "Demo.Tests.csproj");
    private string TestSourcePath => Path.Combine(_root, "Demo.Tests", "CalcTests.cs");

    private static RunRequest Req(string path, string[] names, bool yes = true, bool dryRun = false, string method = "Divide",
        string[]? breaks = null, bool verbose = false, IReadOnlyDictionary<string, TestCheck>? checks = null) =>
        new(path, null, method, null, "Throw DivideByZeroException when b is zero.", yes, dryRun, null,
            Verbose: verbose, VerifyTests: names, ExpectBreaks: breaks, TestChecks: checks);

    private static string Block(string search, string replace) => search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    private Task<RunResult> RunDetailed(RunRequest request) =>
        FsmOrchestratorFactory.Create(_fs, _backend, _ui, _options, TimeProvider.System, _processes, _isWindows)
            .RunDetailedAsync(request, TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);

    private Task<int> Run(RunRequest request) =>
        FsmOrchestratorFactory.Create(_fs, _backend, _ui, _options, TimeProvider.System, _processes, _isWindows)
            .RunAsync(request, TestContext.Current.CancellationToken)
            .WaitAsync(WallClock, CancellationToken.None);

    private static ProcessResult Result(int exit = 0, params string[] lines)
    {
        var stdout = string.Join("\n", lines);
        return new ProcessResult(exit, stdout, "", Encoding.UTF8.GetBytes(stdout), TimeSpan.FromSeconds(1), false);
    }

    private static ProcessResult Ok() => Result();

    private static string TrxPathOf(ProcessSpec spec)
    {
        const string Logger = "trx;LogFileName=";
        return spec.Arguments.Single(a => a.StartsWith(Logger, StringComparison.Ordinal))[Logger.Length..];
    }

    /// <summary>A minimal TRX: one top-level result per (name, passed); a failure carries <c>message</c>.</summary>
    private static string Trx(params (string Name, bool Passed)[] results)
    {
        var sb = new StringBuilder("<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>");
        foreach (var (name, passed) in results)
        {
            sb.Append($"<UnitTestResult testName=\"{SecurityElement.Escape(name)}\" outcome=\"{(passed ? "Passed" : "Failed")}\"");
            sb.Append(passed ? " />" : "><Output><ErrorInfo><Message>Assert.Throws() Failure: No exception was thrown\nExpected: DivideByZeroException</Message></ErrorInfo></Output></UnitTestResult>");
        }
        sb.Append($"</Results><ResultSummary outcome=\"{(results.All(r => r.Passed) ? "Completed" : "Failed")}\" /></TestRun>");
        return sb.ToString();
    }

    private void EnqueueTests(string trx) =>
        _processes.Enqueue((spec, _) =>
        {
            File.WriteAllText(TrxPathOf(spec), trx);
            return Task.FromResult(Result(trx.Contains("outcome=\"Failed\"", StringComparison.Ordinal) ? 1 : 0));
        });

    private static string Red() => Trx((FullName, false), (OtherTest, true));
    private static string Green() => Trx((FullName, true), (OtherTest, true));

    private bool IsTestRun(ProcessSpec spec) => spec.Arguments.FirstOrDefault() == "test";

    private JsonElement Summary()
    {
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, ".pelly", "runs"), "summary.json", SearchOption.AllDirectories));
        return JsonDocument.Parse(File.ReadAllText(file)).RootElement;
    }

    private string? FailureKind() => Summary().GetProperty("failureKind").GetString();

    private string[] RecordedMessages()
    {
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, ".pelly", "runs"), "messages.txt", SearchOption.AllDirectories));
        return File.ReadAllLines(file);
    }

    [Fact]
    public async Task VerifyTest_Summary_HasTestFirst()
    {
        EnqueueBaseline(Red());
        EnqueueAttempt(Green());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(0, exit);
        var tf = Summary().GetProperty("testFirst");
        Assert.Equal("Passed", tf.GetProperty("green").GetString());
        Assert.Equal("Red", tf.GetProperty("red").GetString());
    }

    [Fact]
    public async Task VerifyTest_Red_ProceedsToEdit()
    {
        _processes.Enqueue(Ok());   // baseline build
        _processes.Enqueue(Ok());   // baseline: test project build
        EnqueueTests(Red());        // baseline: the named test fails
        _processes.Enqueue(Ok());   // attempt 1: build
        _processes.Enqueue(Ok());
        EnqueueTests(Green());      // GREEN is Task 5's; here the regression check just passes
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(0, exit);
        Assert.Single(_backend.Requests);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
        Assert.Equal(6, _processes.Calls.Count);
        Assert.DoesNotContain(_ui.Warnings, w => w.StartsWith("이 테스트는 변경을 검증하지 못합니다", StringComparison.Ordinal));
        Assert.DoesNotContain(_ui.Warnings, w => w.StartsWith("RED 확인", StringComparison.Ordinal));
        // Design §5.3: the baseline failure's first line is always in the run record, on screen only with --verbose.
        Assert.Contains($"RED 확인: {FullName} 실패 — Assert.Throws() Failure: No exception was thrown", RecordedMessages());
    }

    [Fact]
    public async Task VerifyTest_Red_Verbose_ShowsFirstMessage()
    {
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests(Red());
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests(Green());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name], verbose: true));

        Assert.Equal(0, exit);
        Assert.Single(_ui.Warnings, $"RED 확인: {FullName} 실패 — Assert.Throws() Failure: No exception was thrown");
        Assert.Contains($"RED 확인: {FullName} 실패 — Assert.Throws() Failure: No exception was thrown", RecordedMessages());
    }

    [Fact]
    public async Task VerifyTest_AlreadyPassed_Yes_WarnsAndContinues()
    {
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests(Green());      // the named test already passes on the original code
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests(Green());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(0, exit);
        Assert.Contains($"이 테스트는 변경을 검증하지 못합니다 (원본 상태에서 이미 통과): {Name}", _ui.Warnings);
        Assert.Empty(_ui.ConfirmMessages);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
    }

    [Fact]
    public async Task VerifyTest_AlreadyPassed_Interactive_Declined_Exit2()
    {
        _ui.IsInteractive = true;
        _ui.ConfirmAnswers.Enqueue(false);
        var originalBytes = File.ReadAllBytes(CalcPath);
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests(Green());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name], yes: false));

        Assert.Equal(2, exit);
        Assert.Equal(["그래도 계속할까요?"], _ui.ConfirmMessages);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Equal("TestRedDeclined", FailureKind());
        // Shown once before the question, and kept in the run record.
        var warning = $"이 테스트는 변경을 검증하지 못합니다 (원본 상태에서 이미 통과): {Name}";
        Assert.Single(_ui.Warnings, warning);
        Assert.Contains(warning, RecordedMessages());
    }

    [Fact]
    public async Task VerifyTest_ExpectBreak_NotPassingInBaseline_Warns()
    {
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests(Red());
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests(Green());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name], breaks: ["Missing_Break", "Divide_Positive"]));

        Assert.Equal(0, exit);
        Assert.Contains("깨질 것으로 정한 테스트 Missing_Break이(가) 원본 상태에서 통과하지 않습니다", _ui.Warnings);
        Assert.DoesNotContain("깨질 것으로 정한 테스트 Divide_Positive이(가) 원본 상태에서 통과하지 않습니다", _ui.Warnings);
    }

    [Fact]
    public async Task VerifyTest_NotInIndex_Exit4_BeforeModel()
    {
        var exit = await Run(Req(CalcPath, ["Missing_Test"]));

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.Empty(_processes.Calls);
        Assert.Contains("테스트 Missing_Test을(를) 작업 공간에서 찾지 못했습니다 (테스트 특성 Fact·Theory·Test·TestCase·TestMethod·DataTestMethod가 붙은 메서드만 찾습니다)", _ui.Warnings);
        Assert.Equal("TestNotFound", FailureKind());
    }

    [Fact]
    public async Task VerifyTest_NoTestRunner_Unverifiable_NothingStarted()
    {
        // A LOCATE wired without a test runner must not fall back to a run without baseline tests.
        var guard = new FileGuard(_fs, TimeProvider.System);
        var snapshot = guard.Read(CalcPath);
        var ws = WorkspaceLocator.Resolve(_fs, CalcPath);
        var ctx = new RunContext("run", Req(CalcPath, [Name]), _options)
        {
            FilePath = CalcPath, Original = snapshot, Current = snapshot, Workspace = ws,
        };
        var phase = new LocatePhase(_fs, _ui, new BuildToolResolver(_fs, _processes, _options.Build, () => false),
            new BuildVerifier(_processes, _options.Build),
            new ModelMemberPicker(_backend, QwenChatTemplate.LoadDefault(), PromptResources.LoadDefault(), _options.Llm, _options.Sampling),
            () => false, tests: null);

        var outcome = await phase.ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.EnvError, outcome);
        Assert.Equal("TestUnverifiable", ctx.FailureKind);
        Assert.Null(ctx.Baseline);
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public async Task VerifyTest_Ambiguous_Exit4()
    {
        File.WriteAllText(Path.Combine(_root, "Demo.Tests", "OtherTests.cs"),
            TestSource.Replace("class CalcTests", "class OtherTests"));

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.Empty(_processes.Calls);
        Assert.Contains($"테스트 이름 {Name}이(가) 여러 테스트와 맞습니다: {FullName}, Demo.Tests.OtherTests.{Name}", _ui.Warnings);
        Assert.Equal("TestAmbiguous", FailureKind());
    }

    [Fact]
    public async Task VerifyTest_TargetItself_Exit4()
    {
        var exit = await Run(Req(TestSourcePath, [Name], method: Name));

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.Empty(_processes.Calls);
        Assert.Contains("--verify-test가 수정 대상 멤버 자신을 가리킵니다", _ui.Warnings);
        Assert.Equal("TestUnverifiable", FailureKind());
    }

    [Fact]
    public async Task VerifyTest_LegacyOnMac_Exit4()
    {
        _isWindows = () => false;
        File.WriteAllText(CsprojPath, LegacyCsproj);

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(4, exit);
        Assert.Empty(_processes.Calls);
        Assert.Contains("레거시 테스트는 Windows에서만 실행합니다 — 테스트 우선 검증을 할 수 없습니다", _ui.Warnings);
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("구문 검사만", StringComparison.Ordinal));
        Assert.Equal("TestUnverifiable", FailureKind());
    }

    [Fact]
    public async Task VerifyTest_NoCsproj_Exit4()
    {
        File.Delete(CsprojPath);

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(4, exit);
        Assert.Empty(_processes.Calls);
        Assert.Contains("csproj가 없어 테스트 우선 검증을 할 수 없습니다", _ui.Warnings);
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("구문 검사만", StringComparison.Ordinal));
        Assert.Equal("TestUnverifiable", FailureKind());
    }

    [Fact]
    public async Task VerifyTest_TestOutsideAnyProject_Exit4()
    {
        var loose = Path.Combine(_root, "loose");
        Directory.CreateDirectory(loose);
        File.Move(TestSourcePath, Path.Combine(loose, "CalcTests.cs"));

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(4, exit);
        Assert.Empty(_processes.Calls);
        Assert.Contains($"테스트 {Name}이(가) 어떤 테스트 프로젝트에도 속하지 않습니다", _ui.Warnings);
        Assert.Equal("TestUnverifiable", FailureKind());
    }

    [Fact]
    public async Task VerifyTest_ExitCodeOnlyProject_Exit4()
    {
        File.WriteAllText(TestCsprojPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <IsTestingPlatformApplication>true</IsTestingPlatformApplication>\n  </PropertyGroup>\n" +
            "  <ItemGroup>\n    <ProjectReference Include=\"..\\Demo\\Demo.csproj\" />\n  </ItemGroup>\n</Project>\n");
        var originalBytes = File.ReadAllBytes(CalcPath);
        _processes.Enqueue(Ok());   // baseline build
        _processes.Enqueue(Ok());   // baseline: test project build
        _processes.Enqueue(Ok());   // baseline: dotnet run, exit code only
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(4, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Contains("테스트별 결과가 없어 지정한 테스트를 판정할 수 없습니다 (레거시는 test.command에 {trx}를 넣으세요)", _ui.Warnings);
        Assert.Equal("TestUnverifiable", FailureKind());
    }

    [Fact]
    public async Task VerifyTest_BaselineBuildFails_Exit4()
    {
        var testError = $"{TestSourcePath}(10,60): error CS1501: No overload for method 'Divide' takes 2 arguments [{TestCsprojPath}]";
        var originalBytes = File.ReadAllBytes(CalcPath);
        _processes.Enqueue(Ok());                        // baseline build
        _processes.Enqueue(Result(1, testError));        // baseline: the test project does not build
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(4, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Contains(_ui.Warnings, w => w.StartsWith("테스트 프로젝트가 원본 상태에서 빌드되지 않습니다", StringComparison.Ordinal));
        Assert.DoesNotContain(_ui.Warnings, w => w.StartsWith("원본 상태에서 테스트 프로젝트가 빌드되지 않아", StringComparison.Ordinal));
        Assert.Equal("TestUnverifiable", FailureKind());
    }

    [Fact]
    public async Task VerifyTest_BaselineTestsCannotRun_Exit4_NoV1Notice()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _processes.Enqueue(Ok());                                    // baseline build
        _processes.Enqueue(Ok());                                    // baseline: test project build
        _processes.Enqueue(Result(1));                               // baseline: tests ran but wrote no TRX
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(4, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Contains("기준 테스트를 실행하지 못해 지정한 테스트를 판정할 수 없습니다", _ui.Warnings);
        Assert.DoesNotContain(_ui.Warnings, w => w.StartsWith("원본 상태 테스트를 실행하지 못해 테스트 판정을 건너뜁니다", StringComparison.Ordinal));
        Assert.Equal("TestUnverifiable", FailureKind());
    }

    [Fact]
    public async Task VerifyTest_TestProjectNotReferencingTarget_IsStillRun()
    {
        File.WriteAllText(TestCsprojPath, "<Project Sdk=\"Microsoft.NET.Sdk\">\n</Project>\n");
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests(Red());
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests(Green());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(0, exit);
        var testRuns = _processes.Calls.Where(IsTestRun).ToList();
        Assert.Equal(2, testRuns.Count);
        Assert.All(testRuns, c => Assert.Equal(TestCsprojPath, c.Arguments[1]));
        Assert.DoesNotContain(_ui.Warnings, w => w.StartsWith("테스트 프로젝트를 찾지 못해", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyTest_DryRun_NoProcesses()
    {
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name, "Other_Test"], dryRun: true));

        Assert.Equal(0, exit);
        Assert.Empty(_processes.Calls);
        Assert.Contains($"테스트 우선 검증: {Name}, Other_Test (dry-run에서는 실행하지 않음)", _ui.Warnings);
        Assert.Equal(CalcSource, File.ReadAllText(CalcPath));
    }

    [Fact]
    public void FileHash_OfTargetFile_IsTheTestDeclarationOnly()
    {
        var fs = new FileSystem();
        var other = TestFirstState.HashOf(fs, TestSourcePath, CalcPath, [FullName]);
        Assert.Equal(PellyCoder.Core.Files.TextFileCodec.HashHex(File.ReadAllBytes(TestSourcePath)), other);

        var before = TestFirstState.HashOf(fs, TestSourcePath, TestSourcePath, [FullName]);
        File.WriteAllText(TestSourcePath, TestSource.Replace("public class CalcTests\n{\n", "public class CalcTests\n{\n    private int _x;\n"));
        Assert.Equal(before, TestFirstState.HashOf(fs, TestSourcePath, TestSourcePath, [FullName]));
        File.WriteAllText(TestSourcePath, TestSource.Replace("Divide(1, 0)", "Divide(2, 0)"));
        Assert.NotEqual(before, TestFirstState.HashOf(fs, TestSourcePath, TestSourcePath, [FullName]));
    }
    /// <summary>One attempt's VERIFY processes: the target build, the test project build and the test run writing <paramref name="trx"/>.</summary>
    private void EnqueueAttempt(string trx)
    {
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests(trx);
    }

    private void EnqueueBaseline(string trx) => EnqueueAttempt(trx);

    [Fact]
    public async Task VerifyTest_GreenFails_RetriesWithGreenFeedback_ThenPasses()
    {
        EnqueueBaseline(Red());
        EnqueueAttempt(Red());      // attempt 1: the named test still fails
        EnqueueAttempt(Green());
        _backend.Enqueue(Block(Original, Wrong));
        _backend.Enqueue("Throw instead.</think>", evalCount: 10);
        _backend.Enqueue(Block(Wrong, Guarded));

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(0, exit);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
        var prompt = _backend.Requests[1].RawPrompt;
        Assert.Contains("TEST NOT PASSING: " + FullName, prompt);
        Assert.Contains("Assert.Throws() Failure: No exception was thrown\nExpected: DivideByZeroException", prompt);
        Assert.Contains(GreenRequest, prompt);
        Assert.DoesNotContain("Keep the requested change", prompt);
    }

    [Fact]
    public async Task VerifyTest_GreenNeverPasses_Exit1_RolledBack()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        EnqueueBaseline(Red());
        EnqueueAttempt(Red());
        EnqueueAttempt(Red());
        _backend.Enqueue(Block(Original, Wrong));
        _backend.Enqueue("Throw instead.</think>", evalCount: 10);
        _backend.Enqueue(Block(Wrong, Guarded));

        var exit = await Run(Req(CalcPath, [Name]) with { MaxRetries = 1 });

        Assert.Equal(1, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Equal("TestGreenFailed", FailureKind());
        Assert.Contains("TEST", Summary().GetProperty("errorCodes").EnumerateArray().Select(e => e.GetString()));
    }

    [Fact]
    public async Task VerifyTest_Regression_Exit1()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        EnqueueBaseline(Red());
        EnqueueAttempt(Trx((FullName, true), (OtherTest, false)));   // GREEN, but another test now fails
        EnqueueAttempt(Trx((FullName, true), (OtherTest, false)));
        _backend.Enqueue(Block(Original, Wrong));
        _backend.Enqueue("Keep the other test.</think>", evalCount: 10);
        _backend.Enqueue(Block(Wrong, Guarded));

        var exit = await Run(Req(CalcPath, [Name]) with { MaxRetries = 1 });

        Assert.Equal(1, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Equal("TestRegression", FailureKind());
        var prompt = _backend.Requests[1].RawPrompt;
        Assert.Contains("TESTS FAILED (1 new failure)\n" + OtherTest + ": Assert.Throws() Failure", prompt);
        Assert.DoesNotContain("TEST NOT PASSING", prompt);
    }

    [Fact]
    public async Task VerifyTest_HostCrash_IsGreenFailure()
    {
        EnqueueBaseline(Red());
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _processes.Enqueue(Result(exit: 134));  // attempt 1: the host died, no TRX was written
        EnqueueAttempt(Green());
        _backend.Enqueue(Block(Original, Wrong));
        _backend.Enqueue("Avoid the crash.</think>", evalCount: 10);
        _backend.Enqueue(Block(Wrong, Guarded));

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(0, exit);
        var prompt = _backend.Requests[1].RawPrompt;
        Assert.Contains("TEST NOT PASSING: " + FullName + "\nnot run", prompt);
        Assert.Contains(OtherTest + ": not run (test host may have crashed)", prompt);
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("테스트를 실행하지 못했습니다", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpectBreak_ExcludesOnlyThatTest()
    {
        EnqueueBaseline(Red());
        EnqueueAttempt(Trx((FullName, true), (OtherTest, false)));   // only the expected break fails
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name], breaks: ["Divide_Positive"]));

        Assert.Equal(0, exit);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
        Assert.DoesNotContain(_ui.Warnings, w => w.StartsWith("깨질 것으로 정한 테스트가 여전히 통과합니다", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ExpectBreak_WithAnotherFailure_IsRegression()
    {
        var baseline = Trx((FullName, false), (OtherTest, true), (ThirdTest, true));
        var broken = Trx((FullName, true), (OtherTest, false), (ThirdTest, false));
        EnqueueBaseline(baseline);
        EnqueueAttempt(broken);
        EnqueueAttempt(broken);
        _backend.Enqueue(Block(Original, Wrong));
        _backend.Enqueue("Keep the other test.</think>", evalCount: 10);
        _backend.Enqueue(Block(Wrong, Guarded));

        var exit = await Run(Req(CalcPath, [Name], breaks: ["Divide_Positive"]) with { MaxRetries = 1 });

        Assert.Equal(1, exit);
        Assert.Equal("TestRegression", FailureKind());
        var prompt = _backend.Requests[1].RawPrompt;
        Assert.Contains("TESTS FAILED (1 new failure)\n" + ThirdTest + ":", prompt);
        Assert.DoesNotContain(OtherTest, prompt);
    }

    [Fact]
    public async Task ExpectBreak_StillPassing_Notice()
    {
        EnqueueBaseline(Red());
        EnqueueAttempt(Green());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name], breaks: ["Divide_Positive"]));

        Assert.Equal(0, exit);
        Assert.Single(_ui.Warnings, w => w == "깨질 것으로 정한 테스트가 여전히 통과합니다: Divide_Positive");
    }

    [Fact]
    public async Task VerifyTest_TestInTargetFile_HashIgnoresTargetEdit()
    {
        const string Helper = "    public static int Seed() => 1;\n";
        File.WriteAllText(TestSourcePath, TestSource.Replace("public class CalcTests\n{\n", "public class CalcTests\n{\n" + Helper + "\n"));
        EnqueueBaseline(Red());
        EnqueueAttempt(Green());
        _backend.Enqueue(Block(Helper.TrimEnd('\n'), "    public static int Seed() => 2;"));

        var exit = await Run(Req(TestSourcePath, [Name], method: "Seed"));

        Assert.Equal(0, exit);
        Assert.Contains("public static int Seed() => 2;", File.ReadAllText(TestSourcePath));
        Assert.DoesNotContain(_ui.Warnings, w => w.StartsWith("지정한 테스트 파일이 실행 중에 바뀌었습니다", StringComparison.Ordinal));
    }

    [Fact]
    public async Task VerifyTest_TestFileEditedDuringRun_Exit4()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        EnqueueBaseline(Red());
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _processes.Enqueue((spec, _) =>
        {
            // Something outside pelly weakens the test while VERIFY runs it.
            File.WriteAllText(TestSourcePath, TestSource.Replace("Assert.Throws<System.DivideByZeroException>(() => new Calc().Divide(1, 0));", "Assert.True(true);"));
            File.WriteAllText(TrxPathOf(spec), Green());
            return Task.FromResult(Ok());
        });
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(4, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Equal("TestFileChanged", FailureKind());
        Assert.Contains($"지정한 테스트 파일이 실행 중에 바뀌었습니다: {Path.Combine("Demo.Tests", "CalcTests.cs")}", _ui.Warnings);
    }
    private static Dictionary<string, TestCheck> Checks(params (string Name, TestCheck Check)[] checks) =>
        checks.ToDictionary(c => c.Name, c => c.Check, StringComparer.Ordinal);

    private const string AlreadyPassedWarning = "이 테스트는 변경을 검증하지 못합니다 (원본 상태에서 이미 통과)";

    [Fact]
    public async Task Roles_RedOnly_NoGreenCheck()
    {
        EnqueueBaseline(Red());
        EnqueueAttempt(Red());      // the named test still fails: not judged (RED role only), and failed in the baseline
        _backend.Enqueue(Block(Original, Wrong));

        var exit = await Run(Req(CalcPath, [Name], checks: Checks((Name, TestCheck.Red))));

        Assert.Equal(0, exit);
        Assert.Single(_backend.Requests);
        Assert.Equal(CalcSource.Replace(Original, Wrong), File.ReadAllText(CalcPath));
        var tf = Summary().GetProperty("testFirst");
        Assert.Equal("Red", tf.GetProperty("red").GetString());
        Assert.Equal("NotChecked", tf.GetProperty("green").GetString());
        Assert.Equal(0, tf.GetProperty("regressions").GetInt32());
        Assert.False(tf.GetProperty("inApply").GetBoolean());
    }

    [Fact]
    public async Task Roles_GreenOnlyName_NoAlreadyPassedWarning()
    {
        EnqueueBaseline(Green());   // already passes on the original code: no RED role, so no warning
        EnqueueAttempt(Green());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name], checks: Checks((Name, TestCheck.Green))));

        Assert.Equal(0, exit);
        Assert.DoesNotContain(_ui.Warnings, w => w.StartsWith(AlreadyPassedWarning, StringComparison.Ordinal));
        // Nothing was said at all, so the run wrote no messages.txt either.
        Assert.Empty(Directory.GetFiles(Path.Combine(_root, ".pelly", "runs"), "messages.txt", SearchOption.AllDirectories));
        var tf = Summary().GetProperty("testFirst");
        Assert.Equal("NotChecked", tf.GetProperty("red").GetString());
        Assert.Equal("Passed", tf.GetProperty("green").GetString());
    }

    [Fact]
    public async Task Roles_GreenOnly_Fails_Retries()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        EnqueueBaseline(Red());
        EnqueueAttempt(Red());
        EnqueueAttempt(Red());
        _backend.Enqueue(Block(Original, Wrong));
        _backend.Enqueue("Throw instead.</think>", evalCount: 10);
        _backend.Enqueue(Block(Wrong, Guarded));

        var exit = await Run(Req(CalcPath, [Name], checks: Checks((Name, TestCheck.Green))) with { MaxRetries = 1 });

        Assert.Equal(1, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Equal("TestGreenFailed", FailureKind());
        Assert.Contains("TEST NOT PASSING: " + FullName, _backend.Requests[1].RawPrompt);
        Assert.Equal("Failed", Summary().GetProperty("testFirst").GetProperty("green").GetString());
    }

    [Fact]
    public async Task Roles_None_RegressionOnly()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        EnqueueBaseline(Green());   // passes in the baseline: no warning (no RED role)
        EnqueueAttempt(Trx((FullName, false), (OtherTest, true)));   // now fails: a regression, not a GREEN failure
        EnqueueAttempt(Trx((FullName, false), (OtherTest, true)));
        _backend.Enqueue(Block(Original, Wrong));
        _backend.Enqueue("Keep the test passing.</think>", evalCount: 10);
        _backend.Enqueue(Block(Wrong, Guarded));

        var exit = await Run(Req(CalcPath, [Name], checks: Checks((Name, TestCheck.None))) with { MaxRetries = 1 });

        Assert.Equal(1, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Equal("TestRegression", FailureKind());
        Assert.DoesNotContain(_ui.Warnings, w => w.StartsWith(AlreadyPassedWarning, StringComparison.Ordinal));
        var prompt = _backend.Requests[1].RawPrompt;
        Assert.Contains("TESTS FAILED (1 new failure)\n" + FullName, prompt);
        Assert.DoesNotContain("TEST NOT PASSING", prompt);
        var tf = Summary().GetProperty("testFirst");
        Assert.Equal("NotChecked", tf.GetProperty("red").GetString());
        Assert.Equal("NotChecked", tf.GetProperty("green").GetString());
        Assert.Equal(1, tf.GetProperty("regressions").GetInt32());
    }

    [Fact]
    public async Task RunResult_CarriesRedStates()
    {
        File.WriteAllText(TestSourcePath, TestSource.Replace("public class CalcTests\n{\n",
            "public class CalcTests\n{\n    [Fact]\n    public void Divide_Positive() { }\n\n"));
        EnqueueBaseline(Red());     // Name fails, Divide_Positive passes
        EnqueueAttempt(Green());
        _backend.Enqueue(Block(Original, Guarded));

        var result = await RunDetailed(Req(CalcPath, [Name, "Divide_Positive"],
            checks: Checks((Name, TestCheck.Red | TestCheck.Green), ("Divide_Positive", TestCheck.Green))));

        Assert.Equal(0, result.ExitCode);
        var tf = Assert.IsType<TestFirstOutcome>(result.TestFirst);
        Assert.Equal(new Dictionary<string, RedState> { [Name] = RedState.Red }, tf.Red);
        Assert.Equal(0, tf.BreaksExcluded);
    }

    [Fact]
    public async Task RunResult_CarriesExcludedBreakNames()
    {
        EnqueueBaseline(Red());
        EnqueueAttempt(Trx((FullName, true), (OtherTest, false)));   // only the expected break fails
        _backend.Enqueue(Block(Original, Guarded));

        var result = await RunDetailed(Req(CalcPath, [Name], breaks: ["Divide_Positive"]));

        Assert.Equal(0, result.ExitCode);
        var tf = Assert.IsType<TestFirstOutcome>(result.TestFirst);
        Assert.Equal(1, tf.BreaksExcluded);
        Assert.Equal([OtherTest], tf.ExcludedNames);
    }

    [Fact]
    public async Task RunResult_NotTestFirst_NoOutcome()
    {
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _backend.Enqueue(Block(Original, Guarded));

        var result = await RunDetailed(new RunRequest(CalcPath, null, "Divide", null, "Throw.", true, false, null));

        Assert.Equal(0, result.ExitCode);
        Assert.Null(result.TestFirst);
    }

    [Fact]
    public async Task Summary_InApply_WhenHosted()
    {
        var ws = new Workspace(_root, Path.Combine(_root, ".pelly"));
        using var held = WorkspaceLock.TryAcquire(_fs, ws);
        Assert.NotNull(held);
        EnqueueBaseline(Red());
        EnqueueAttempt(Green());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name]) with { Host = new ApplyHost(ws) });

        Assert.Equal(0, exit);
        Assert.True(Summary().GetProperty("testFirst").GetProperty("inApply").GetBoolean());
    }

    [Fact]
    public async Task VerifyTest_ExpectedHashMismatch_Exit4()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        EnqueueBaseline(Red());
        EnqueueAttempt(Green());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name]) with { ExpectedTestHashes = new Dictionary<string, string> { [Name] = new string('0', 64) } });

        Assert.Equal(4, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Equal("TestFileChanged", FailureKind());
        Assert.Equal("Passed", Summary().GetProperty("testFirst").GetProperty("green").GetString());
        Assert.Contains($"지정한 테스트 파일이 실행 중에 바뀌었습니다: {Path.Combine("Demo.Tests", "CalcTests.cs")}", _ui.Warnings);
    }

    [Fact]
    public async Task VerifyTest_ExpectedHashMatch_Passes()
    {
        var expected = TestDeclarationHash.Of(_fs, _root, Name);
        Assert.NotNull(expected);
        EnqueueBaseline(Red());
        EnqueueAttempt(Green());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name]) with { ExpectedTestHashes = new Dictionary<string, string> { [Name] = expected } });

        Assert.Equal(0, exit);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
    }

    [Fact]
    public async Task Summary_FirstAttemptGreen_FailedThenPassed()
    {
        EnqueueBaseline(Red());
        EnqueueAttempt(Red());      // attempt 1: the named test still fails
        EnqueueAttempt(Green());
        _backend.Enqueue(Block(Original, Wrong));
        _backend.Enqueue("Throw instead.</think>", evalCount: 10);
        _backend.Enqueue(Block(Wrong, Guarded));

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(0, exit);
        var tf = Summary().GetProperty("testFirst");
        Assert.Equal("Failed", tf.GetProperty("firstAttemptGreen").GetString());
        Assert.Equal("Passed", tf.GetProperty("green").GetString());
    }

    [Fact]
    public async Task Summary_FirstAttemptGreen_PassedOnFirst()
    {
        EnqueueBaseline(Red());
        EnqueueAttempt(Green());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(0, exit);
        Assert.Equal("Passed", Summary().GetProperty("testFirst").GetProperty("firstAttemptGreen").GetString());
    }

    [Fact]
    public async Task Summary_FirstAttemptBuildFails_NotChecked()
    {
        EnqueueBaseline(Red());
        // Attempt 1: the target does not build, so the test step is never reached.
        _processes.Enqueue(Result(1, $"{CalcPath}(7,9): error CS0103: The name 'x' does not exist in the current context [{CsprojPath}]"));
        EnqueueAttempt(Green());
        _backend.Enqueue(Block(Original, "        return x / b;"));
        _backend.Enqueue("Use a.</think>", evalCount: 10);
        _backend.Enqueue(Block("        return x / b;", Guarded));

        var exit = await Run(Req(CalcPath, [Name]));

        Assert.Equal(0, exit);
        var tf = Summary().GetProperty("testFirst");
        Assert.Equal("NotChecked", tf.GetProperty("firstAttemptGreen").GetString());
        Assert.Equal("Passed", tf.GetProperty("green").GetString());
    }
}
