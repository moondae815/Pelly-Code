using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Specs;

/// <summary><c>pelly apply</c> with acceptance-criterion tests on task groups (V2-2 §7.3), over fake builds, tests and tasks.</summary>
public sealed class SpecRunnerGroupTestFirstTests : IDisposable
{
    // GetOrder spans lines 5-8, Cancel 10-12.
    private const string OrderService =
        "namespace App;\n\npublic class OrderService\n{\n    public int GetOrder(int id)\n    {\n        return id;\n    }\n\n    public void Cancel()\n    {\n    }\n}\n";

    // Find spans lines 5-8.
    private const string Repo =
        "namespace App;\n\npublic class Repo\n{\n    public int Find(int id)\n    {\n        return id;\n    }\n}\n";

    private const string OrderTests =
        "namespace App.Tests;\n\npublic class OrderTests\n{\n    [Fact] public void GetOrder_works() { }\n    [Fact] public void Other_works() { }\n" +
        "    [Fact] public void Old_behavior() { }\n}\n";

    private const string Test = "OrderTests.GetOrder_works";
    private const string FullName = "App.Tests.OrderTests.GetOrder_works";

    private readonly string _root = Directory.CreateTempSubdirectory("pelly-group-tf-").FullName;
    private readonly FileSystem _fs = new();
    private readonly FileGuard _guard;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 9, 30, 0, TimeSpan.Zero));
    private readonly ScriptedUserInteraction _ui = new() { IsInteractive = true };
    private readonly FakeExecutor _exec;
    private readonly FakeGroupVerifier _builds = new();
    private readonly FakeGroupTester _tests = new();

    public SpecRunnerGroupTestFirstTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _guard = new FileGuard(_fs, TimeProvider.System);
        _exec = new FakeExecutor(_guard);
        _exec.Display["T1"] = "OrderService.GetOrder(int)";
        _exec.Display["T2"] = "Repo.Find(int)";
        _exec.Display["T3"] = "Repo.Find(int)";
        Make("App.sln", "");
        Make("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Make("src/App/OrderService.cs", OrderService);
        Make("src/App/Repo.cs", Repo);
        Make("tests/App.Tests/OrderTests.cs", OrderTests);
        Directory.CreateDirectory(P("changes"));
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Make(string rel, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), text, new UTF8Encoding(false));
    }

    private string Read(string rel) => File.ReadAllText(P(rel));

    private string SpecPath => P("changes/x.md");

    private void Spec(string criteria, string tasks) =>
        Make("changes/x.md", "---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n" + criteria + "\n## 작업\n" + tasks);

    private const string Criterion = $"- AC1: 하나\n  test: {Test}\n  breaks: OrderTests.Old_behavior\n";

    private const string GroupTasks =
        "- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 (AC1) [G1]\n" +
        "- [ ] T2 cs src/App/Repo.cs#Repo.Find 이름을 바꾼다 (AC1) [G1]\n";

    private static ApplyOptions Opts(bool cont = false, bool reviewAtEnd = false, bool yes = true, int? maxRetries = null) =>
        new(cont, false, reviewAtEnd, yes, false, maxRetries, false);

    private SpecRunner Runner(IGroupTester? tests) => new(_fs, _guard, _exec, _ui, _time, 160, groups: _builds, groupTests: tests);

    private Task<int> Run(ApplyOptions o) => Runner(_tests).RunAsync(SpecPath, o, TestContext.Current.CancellationToken);

    private static GroupTestBaseline Baseline(RedState state = RedState.Red) =>
        new([], [], new HashSet<int>(), new Dictionary<string, RedState> { [Test] = state }, null)
        {
            Tests = [new RedTest(Test, FullName, 0, state, null)],
        };

    private static GroupTestVerdict Passing(int breaks = 0) => new(null, [], [], [], breaks);

    private static GroupTestVerdict GreenFails() => new(null, [new GreenFailure(FullName, "Assert.Equal() Failure", null)], [], [], 0);

    private static Diagnostic Error(string file, int line, string code = "CS0029") => new(file, line, 9, "error", code, "Cannot convert");

    private void AssertGroupFilesUntouched()
    {
        Assert.Equal(OrderService, Read("src/App/OrderService.cs"));
        Assert.Equal(Repo, Read("src/App/Repo.cs"));
    }

    private JsonElement Criteria()
    {
        var dir = Assert.Single(Directory.GetDirectories(P(".pelly/runs"), "apply-*"));
        return Assert.Single(JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "apply.json"))).RootElement.GetProperty("criteria").EnumerateArray());
    }

    [Fact]
    public async Task Group_NoTests_UnchangedFlow()
    {
        Spec("- AC1: 하나\n", GroupTasks);

        Assert.Equal(0, await Run(Opts()));

        Assert.Equal(["T1", "T2"], _exec.Calls);
        Assert.Equal(0, _tests.Baselines);
        Assert.Equal(0, _tests.Verifies);
        Assert.Equal(2, _builds.Calls);
    }

    [Fact]
    public async Task Group_GreenFails_RerunsLastLinkedTaskWithFeedback_ThenPasses()
    {
        Spec(Criterion, GroupTasks);
        _tests.Baseline = Baseline(RedState.RedCompile);
        _tests.Verdicts.Enqueue(GreenFails());
        _tests.Verdicts.Enqueue(Passing(breaks: 1));

        Assert.Equal(0, await Run(Opts()));

        Assert.Equal(["T1", "T2", "T2"], _exec.Calls);
        var plan = Assert.Single(_tests.Plans);
        Assert.Equal([Test], plan.Tests);
        Assert.Equal([Test], plan.RedNames);
        Assert.Equal([Test], plan.GreenNames);
        Assert.Equal(["OrderTests.Old_behavior"], plan.Breaks);
        Assert.Contains(Path.Combine("group-tests", "G1"), plan.ReportPrefix);
        var feedback = _exec.Options[2].Group!.InitialFeedback!.Text;
        Assert.Contains("TEST NOT PASSING: " + FullName, feedback);
        Assert.Equal("Find", _exec.Options[2].MemberOverride);
        // Baseline build, then build + tests twice.
        Assert.Equal(3, _builds.Calls);
        Assert.Equal(2, _tests.Verifies);
        var ac = Criteria();
        Assert.Equal("RedCompile", ac.GetProperty("red").GetString());
        Assert.Equal("Met", ac.GetProperty("outcome").GetString());
        Assert.Equal(1, ac.GetProperty("breaksExcluded").GetInt32());
    }

    [Fact]
    public async Task Group_TwoCriteria_EachCountsOnlyItsOwnBreaks()
    {
        Make("tests/App.Tests/MoreTests.cs", "namespace App.Tests;\n\npublic class MoreTests\n{\n    [Fact] public void A_works() { }\n    [Fact] public void B_old() { }\n}\n");
        Spec(Criterion + "- AC2: 둘\n  test: MoreTests.A_works\n  breaks: MoreTests.B_old\n",
            GroupTasks.Replace("(AC1)", "(AC1, AC2)"));
        _tests.Baseline = new GroupTestBaseline([], [], new HashSet<int>(),
            new Dictionary<string, RedState> { [Test] = RedState.Red, ["MoreTests.A_works"] = RedState.Red }, null)
        {
            Tests = [new RedTest(Test, FullName, 0, RedState.Red, null), new RedTest("MoreTests.A_works", "App.Tests.MoreTests.A_works", 0, RedState.Red, null)],
        };
        _tests.Fallback = new GroupTestVerdict(null, [], [], [], 2)
        {
            ExcludedNames = ["App.Tests.OrderTests.Old_behavior", "App.Tests.MoreTests.B_old"],
        };

        Assert.Equal(0, await Run(Opts()));

        var dir = Assert.Single(Directory.GetDirectories(P(".pelly/runs"), "apply-*"));
        var acs = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "apply.json"))).RootElement.GetProperty("criteria").EnumerateArray().ToArray();
        Assert.Equal(2, acs.Length);
        Assert.All(acs, ac => Assert.Equal(1, ac.GetProperty("breaksExcluded").GetInt32()));
    }

    [Fact]
    public async Task Group_GreenNeverPasses_UndoneExit1()
    {
        Spec(Criterion, GroupTasks);
        _tests.Fallback = GreenFails();

        Assert.Equal(1, await Run(Opts(maxRetries: 1)));

        Assert.Equal(["T1", "T2", "T2"], _exec.Calls);
        AssertGroupFilesUntouched();
        var spec = Read("changes/x.md");
        Assert.Contains("<!-- failed: R-T1 GroupTestGreenFailed -->", spec);
        Assert.Contains("<!-- failed: R-T2-2 GroupTestGreenFailed -->", spec);
        Assert.Contains(_ui.Warnings, w => w.Contains("G1") && w.Contains(FullName));
        Assert.Equal("NotMet", Criteria().GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Group_Regression_UndoneExit1()
    {
        Spec(Criterion, GroupTasks);
        _tests.Fallback = new GroupTestVerdict(null, [], [], [new TestCaseResult("App.Tests.OrderTests.Other_works", false, "boom")], 0);

        Assert.Equal(1, await Run(Opts()));

        Assert.Equal(["T1", "T2"], _exec.Calls);
        AssertGroupFilesUntouched();
        Assert.Contains("그룹 G1: 테스트 회귀: App.Tests.OrderTests.Other_works", _ui.Warnings);
        Assert.Contains("<!-- failed: R-T2 GroupTestRegression -->", Read("changes/x.md"));
    }

    [Fact]
    public async Task Group_TestsUnverifiableBeforeEdit_Exit4_NoEdit()
    {
        Spec(Criterion, GroupTasks);
        _tests.Baseline = new GroupTestBaseline([], [], new HashSet<int>(), new Dictionary<string, RedState>(), "테스트 프로젝트 밖에 원래 컴파일 오류가 있습니다: x.cs");

        Assert.Equal(4, await Run(Opts()));

        Assert.Empty(_exec.Calls);
        AssertGroupFilesUntouched();
        Assert.Contains("그룹 G1: 테스트 프로젝트 밖에 원래 컴파일 오류가 있습니다: x.cs", _ui.Warnings);
        Assert.Matches(@"T1 .+ <!-- failed: apply-\S+ GroupTestUnverifiable -->", Read("changes/x.md"));
        Assert.Equal("NotChecked", Criteria().GetProperty("red").GetString());
    }

    [Fact]
    public async Task Group_TestsUnverifiableAfterEdit_UndoneExit4()
    {
        Spec(Criterion, GroupTasks);
        _tests.Fallback = new GroupTestVerdict("테스트가 300초 안에 끝나지 않았습니다 (test.timeoutSeconds)", [], [], [], 0);

        Assert.Equal(4, await Run(Opts()));

        Assert.Equal(["T1", "T2"], _exec.Calls);
        AssertGroupFilesUntouched();
        Assert.Contains("그룹 G1: 테스트가 300초 안에 끝나지 않았습니다 (test.timeoutSeconds)", _ui.Warnings);
        Assert.Contains("<!-- failed: R-T1 GroupTestUnverifiable -->", Read("changes/x.md"));
    }

    [Fact]
    public async Task Group_AlreadyPassed_Declined_Exit2_NoEdit()
    {
        Spec(Criterion, GroupTasks + "- [ ] T3 cs src/App/OrderService.cs#OrderService.Cancel 다른 일\n");
        _tests.Baseline = Baseline(RedState.AlreadyPassed);
        _ui.ConfirmAnswers.Enqueue(false);

        Assert.Equal(2, await Run(Opts(cont: true, yes: false)));

        Assert.Empty(_exec.Calls); // --continue does not go on to T3
        AssertGroupFilesUntouched();
        Assert.Contains($"이 테스트는 변경을 검증하지 못합니다 (원본 상태에서 이미 통과): {Test}", _ui.Warnings);
        Assert.Equal(["그래도 계속할까요?"], _ui.ConfirmMessages);
        Assert.Matches(@"T2 .+ <!-- failed: apply-\S+ TestRedDeclined -->", Read("changes/x.md"));
        var ac = Criteria();
        Assert.Equal("AlreadyPassed", ac.GetProperty("red").GetString());
        Assert.Equal("NotRun", ac.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Group_AlreadyPassed_Yes_WarnsAndContinues()
    {
        Spec(Criterion, GroupTasks);
        _tests.Baseline = Baseline(RedState.AlreadyPassed);

        Assert.Equal(0, await Run(Opts()));

        Assert.Equal(["T1", "T2"], _exec.Calls);
        Assert.Contains($"이 테스트는 변경을 검증하지 못합니다 (원본 상태에서 이미 통과): {Test}", _ui.Warnings);
        Assert.Empty(_ui.ConfirmMessages);
    }

    [Fact]
    public async Task Group_BaselineNotices_AreShown()
    {
        Spec(Criterion, GroupTasks);
        _tests.Baseline = Baseline(RedState.RedCompile) with { Notices = [GroupTester.StrictNotice + ": tests/App.Tests/App.Tests.csproj"] };

        Assert.Equal(0, await Run(Opts()));

        Assert.Contains(GroupTester.StrictNotice + ": tests/App.Tests/App.Tests.csproj", _ui.Warnings);
    }

    [Fact]
    public async Task Group_GreenForAcStartedOutsideGroup()
    {
        Spec(Criterion,
            "- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 먼저 (AC1)\n" +
            "- [ ] T2 cs src/App/Repo.cs#Repo.Find 이름을 바꾼다 (AC1) [G1]\n" +
            "- [ ] T3 cs src/App/OrderService.cs#OrderService.Cancel 고친다 (AC1) [G1]\n");
        _exec.Display["T3"] = "OrderService.Cancel()";
        _exec.TestFirst["T1"] = new TestFirstOutcome(new Dictionary<string, RedState> { [Test] = RedState.Red }, 0);
        _tests.Baseline = Baseline(RedState.Red) with { Red = new Dictionary<string, RedState> { [Test] = RedState.AlreadyPassed } };
        _tests.Verdicts.Enqueue(GreenFails());

        Assert.Equal(0, await Run(Opts()));

        Assert.Equal(["T1", "T2", "T3", "T3"], _exec.Calls);
        Assert.Equal(TestCheck.Red, _exec.Options[0].Tests!.Checks[Test]);
        var plan = Assert.Single(_tests.Plans);
        Assert.Empty(plan.RedNames);
        Assert.Equal([Test], plan.GreenNames);
        // The group re-ran T3 (the criterion's last linked task in the group), not T2.
        Assert.Contains("TEST NOT PASSING", _exec.Options[3].Group!.InitialFeedback!.Text);
        Assert.Empty(_ui.ConfirmMessages);
        Assert.Equal("Red", Criteria().GetProperty("red").GetString()); // from T1's run, not the group's baseline
    }

    [Fact]
    public async Task Group_RerunBudgetSharedWithBuildErrors()
    {
        Spec(Criterion, GroupTasks);
        _builds.Results.Enqueue(Built());
        _builds.Results.Enqueue(Built(Error("src/App/Repo.cs", 7)));
        _tests.Fallback = GreenFails();

        Assert.Equal(1, await Run(Opts(maxRetries: 1)));

        Assert.Equal(["T1", "T2", "T2"], _exec.Calls); // one build re-run used the whole budget
        Assert.Contains("BUILD FAILED", _exec.Options[2].Group!.InitialFeedback!.Text);
        Assert.Equal(1, _tests.Verifies);
        AssertGroupFilesUntouched();
        Assert.Contains("GroupTestGreenFailed -->", Read("changes/x.md"));
    }

    [Fact]
    public async Task Group_TestBuildErrors_RerunWithBuildFeedback()
    {
        Spec(Criterion, GroupTasks);
        _tests.Baseline = Baseline(RedState.RedCompile);
        _tests.Verdicts.Enqueue(new GroupTestVerdict(null, [], [Error("tests/App.Tests/OrderTests.cs", 5, "CS1501")], [], 0) 
        {
            Unbuilt = new Dictionary<string, IReadOnlyList<string>> { ["tests/App.Tests/App.Tests.csproj"] = [Test] },
        });

        Assert.Equal(0, await Run(Opts()));

        Assert.Equal(["T1", "T2", "T2"], _exec.Calls);
        var feedback = _exec.Options[2].Group!.InitialFeedback!.Text;
        Assert.Contains("BUILD FAILED", feedback);
        Assert.Contains("CS1501", feedback);
    }

    [Fact]
    public async Task Group_CompileRedTestGreenAfterGroup_UnverifiableExit4()
    {
        Spec(Criterion, GroupTasks + "- [ ] T3 cs src/App/OrderService.cs#OrderService.Cancel 마무리 (AC1)\n");
        _exec.Display["T3"] = "OrderService.Cancel()";
        _tests.Baseline = Baseline(RedState.RedCompile);
        _tests.Fallback = new GroupTestVerdict(null, [], [Error("tests/App.Tests/OrderTests.cs", 5, "CS1501")], [], 0)
        {
            Unbuilt = new Dictionary<string, IReadOnlyList<string>> { ["tests/App.Tests/App.Tests.csproj"] = [Test] },
        };

        Assert.Equal(4, await Run(Opts(maxRetries: 3)));

        Assert.Empty(Assert.Single(_tests.Plans).GreenNames);
        Assert.Equal(["T1", "T2"], _exec.Calls); // no re-run, and T3 never runs
        Assert.Equal(1, _tests.Verifies);
        AssertGroupFilesUntouched();
        Assert.Contains("그룹 G1: 테스트 프로젝트가 그룹 뒤에도 빌드되지 않습니다 — 수용 기준의 마지막 연결 작업까지 같은 그룹에 넣으세요: "
            + "tests/App.Tests/App.Tests.csproj", _ui.Warnings);
        Assert.Contains("<!-- failed: R-T2 GroupTestUnverifiable -->", Read("changes/x.md"));
    }

    [Fact]
    public async Task Group_UnbuiltProjectNotCompileRed_RerunWithBuildFeedback()
    {
        // The test project built in the baseline (RED, not compile RED): the group's edit broke it, which a re-run can fix.
        Spec(Criterion, GroupTasks + "- [ ] T3 cs src/App/OrderService.cs#OrderService.Cancel 마무리 (AC1)\n");
        _exec.Display["T3"] = "OrderService.Cancel()";
        _tests.Baseline = Baseline(RedState.Red);
        _tests.Verdicts.Enqueue(new GroupTestVerdict(null, [], [Error("tests/App.Tests/OrderTests.cs", 5, "CS1501")], [], 0)
        {
            Unbuilt = new Dictionary<string, IReadOnlyList<string>> { ["tests/App.Tests/App.Tests.csproj"] = [Test] },
        });

        Assert.Equal(0, await Run(Opts(maxRetries: 3)));

        Assert.Equal(["T1", "T2", "T2", "T3"], _exec.Calls);
        Assert.Contains("BUILD FAILED", _exec.Options[2].Group!.InitialFeedback!.Text);
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("그룹 뒤에도 빌드되지 않습니다", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Group_TestFailureWithoutOwner_FailsAtOnce()
    {
        Spec(Criterion, GroupTasks);
        _tests.Fallback = new GroupTestVerdict(null, [new GreenFailure("App.Tests.Elsewhere.Unknown", "boom", null)], [], [], 0);

        Assert.Equal(1, await Run(Opts(maxRetries: 3)));

        Assert.Equal(["T1", "T2"], _exec.Calls); // nothing to re-run: no build or test again
        Assert.Equal(1, _tests.Verifies);
        Assert.Equal(2, _builds.Calls);
        AssertGroupFilesUntouched();
        Assert.Contains("<!-- failed: R-T2 GroupTestGreenFailed -->", Read("changes/x.md"));
    }

    [Fact]
    public async Task Group_SameTestTwoSpellings_NoCrash()
    {
        Spec($"- AC1: 하나\n  test: {Test}\n- AC2: 둘\n  test: {FullName}\n",
            "- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 (AC1, AC2) [G1]\n" +
            "- [ ] T2 cs src/App/Repo.cs#Repo.Find 이름을 바꾼다 (AC1, AC2) [G1]\n");
        _tests.Baseline = new GroupTestBaseline([], [], new HashSet<int>(),
            new Dictionary<string, RedState> { [Test] = RedState.Red, [FullName] = RedState.Red }, null)
        {
            Tests = [new RedTest(Test, FullName, 0, RedState.Red, null), new RedTest(FullName, FullName, 0, RedState.Red, null)],
        };
        _tests.Verdicts.Enqueue(GreenFails());

        Assert.Equal(0, await Run(Opts()));

        Assert.Equal(["T1", "T2", "T2"], _exec.Calls);
        Assert.Contains("TEST NOT PASSING: " + FullName, _exec.Options[2].Group!.InitialFeedback!.Text);
    }

    [Fact]
    public async Task Group_TesterMissing_Exit4()
    {
        Spec(Criterion, GroupTasks);

        Assert.Equal(4, await Runner(null).RunAsync(SpecPath, Opts(), TestContext.Current.CancellationToken));

        Assert.Empty(_exec.Calls);
        Assert.Contains("그룹 테스트 실행기를 사용할 수 없습니다.", _ui.Warnings);
        AssertGroupFilesUntouched();
    }

    [Fact]
    public async Task Group_TestEditedDuringGroup_UndoneExit4()
    {
        Spec(Criterion, GroupTasks + "- [ ] T3 cs src/App/OrderService.cs#OrderService.Cancel 다른 일\n");
        var file = Path.Combine("tests", "App.Tests", "OrderTests.cs");
        _tests.Fallback = Passing() with { ChangedTestFile = file };

        Assert.Equal(4, await Run(Opts(cont: true)));

        Assert.Equal(["T1", "T2"], _exec.Calls); // exit 4 stops even --continue
        AssertGroupFilesUntouched();
        Assert.Contains("그룹 G1: 지정한 테스트가 실행 중에 바뀌었습니다: " + file, _ui.Warnings);
        var spec = Read("changes/x.md");
        Assert.Contains("<!-- failed: R-T1 GroupTestFileChanged -->", spec);
        Assert.Contains("<!-- failed: R-T2 GroupTestFileChanged -->", spec);
        Assert.Equal("NotMet", Criteria().GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Group_LastLinkedInGroup_UsesExpectedHashFromOutside()
    {
        Spec(Criterion,
            "- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 먼저 (AC1)\n" +
            "- [ ] T2 cs src/App/Repo.cs#Repo.Find 이름을 바꾼다 (AC1) [G1]\n" +
            "- [ ] T3 cs src/App/OrderService.cs#OrderService.Cancel 고친다 (AC1) [G1]\n");
        _exec.Display["T3"] = "OrderService.Cancel()";
        var before = TestDeclarationHash.Of(_fs, _root, Test);
        _exec.OnRun["T1"] = () => Make("tests/App.Tests/OrderTests.cs", OrderTests.Replace("GetOrder_works() { }", "GetOrder_works() { Assert.True(true); }"));

        Assert.Equal(0, await Run(Opts()));

        Assert.NotNull(before);
        Assert.NotEqual(before, TestDeclarationHash.Of(_fs, _root, Test));
        Assert.Null(_exec.Options[0].Tests!.ExpectedHashes); // T1's GREEN is the group's
        var expected = Assert.Single(Assert.Single(_tests.Plans).ExpectedHashes!);
        Assert.Equal((Test, before), (expected.Key, expected.Value));
    }

    [Fact]
    public async Task Group_FirstLinkedInGroup_NoExpectedHash()
    {
        Spec(Criterion, GroupTasks);

        Assert.Equal(0, await Run(Opts()));

        Assert.Null(Assert.Single(_tests.Plans).ExpectedHashes); // the group's own baseline is the reference
    }

    [Fact]
    public async Task Group_FirstLinkedInGroup_LastOutside_GetsHashFromBeforeGroup()
    {
        Spec(Criterion,
            "- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 (AC1) [G1]\n" +
            "- [ ] T2 cs src/App/Repo.cs#Repo.Find 이름을 바꾼다 [G1]\n" +
            "- [ ] T3 cs src/App/OrderService.cs#OrderService.Cancel 마무리 (AC1)\n");
        _exec.Display["T3"] = "OrderService.Cancel()";
        var before = TestDeclarationHash.Of(_fs, _root, Test);
        _exec.OnRun["T2"] = () => Make("tests/App.Tests/OrderTests.cs", OrderTests.Replace("GetOrder_works() { }", "GetOrder_works() { Assert.True(true); }"));

        Assert.Equal(0, await Run(Opts()));

        Assert.Equal(["T1", "T2", "T3"], _exec.Calls);
        var expected = Assert.Single(_exec.Options[2].Tests!.ExpectedHashes!);
        Assert.Equal((Test, before), (expected.Key, expected.Value));
    }

    private static GroupBuild Built(params Diagnostic[] errors) => new(true, null, null, errors);

    /// <summary>Scripted builds in order (then a clean one), counted.</summary>
    private sealed class FakeGroupVerifier : IGroupVerifier
    {
        public Queue<GroupBuild> Results { get; } = new();
        public int Calls { get; private set; }

        public Task<GroupBuild> BuildAsync(Workspace ws, IReadOnlyList<string> projects, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(Results.Count > 0 ? Results.Dequeue() : new GroupBuild(true, null, null, []));
        }
    }

    /// <summary>A scripted baseline, then scripted verdicts in order (then <see cref="Fallback"/>); records the plans.</summary>
    private sealed class FakeGroupTester : IGroupTester
    {
        public GroupTestBaseline Baseline { get; set; } = SpecRunnerGroupTestFirstTests.Baseline();
        public Queue<GroupTestVerdict> Verdicts { get; } = new();
        public GroupTestVerdict Fallback { get; set; } = Passing();
        public List<GroupTestPlan> Plans { get; } = [];
        public int Baselines { get; private set; }
        public int Verifies { get; private set; }

        public Task<GroupTestBaseline> BaselineAsync(Workspace ws, IReadOnlyList<string> scope, GroupTestPlan plan, CancellationToken ct)
        {
            Baselines++;
            Plans.Add(plan);
            return Task.FromResult(Baseline);
        }

        public Task<GroupTestVerdict> VerifyAsync(Workspace ws, GroupTestBaseline baseline, GroupTestPlan plan, CancellationToken ct)
        {
            Verifies++;
            return Task.FromResult(Verdicts.Count > 0 ? Verdicts.Dequeue() : Fallback);
        }
    }

    /// <summary>Appends <c>// &lt;id&gt;</c> through FileGuard; run ids are <c>R-&lt;id&gt;</c>, then <c>R-&lt;id&gt;-2</c>, …</summary>
    private sealed class FakeExecutor(FileGuard guard) : ITaskExecutor
    {
        public List<string> Calls { get; } = [];
        public List<TaskRunOptions> Options { get; } = [];
        public Dictionary<string, string> Display { get; } = [];
        public Dictionary<string, TestFirstOutcome> TestFirst { get; } = [];
        public Dictionary<string, Action> OnRun { get; } = [];

        public async Task<RunResult> ExecuteAsync(SpecTask task, Workspace ws, TaskRunOptions o, CancellationToken ct)
        {
            Calls.Add(task.Id);
            Options.Add(o);
            OnRun.GetValueOrDefault(task.Id)?.Invoke();
            int n = Calls.Count(c => c == task.Id);
            string runId = n == 1 ? "R-" + task.Id : $"R-{task.Id}-{n}";
            var path = Path.Combine(ws.Root, task.Path.Replace('/', Path.DirectorySeparatorChar));
            var original = guard.Read(path);
            var written = await guard.WriteAsync(original, original.Text + "// " + task.Id + "\n", CancellationToken.None);
            return new RunResult(0, runId, path, original, written.Snapshot, null, Display.GetValueOrDefault(task.Id),
                TestFirst: TestFirst.GetValueOrDefault(task.Id));
        }
    }
}
