using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Specs;

/// <summary><c>pelly apply</c> with acceptance-criterion tests (design §6 test-first), over a fake executor.</summary>
public sealed class SpecRunnerTestFirstTests : IDisposable
{
    private const string OrderService =
        "namespace App;\n\npublic class OrderService\n{\n    public void Cancel() { }\n\n    public void Ship() { }\n\n    public void Pay() { }\n\n    public void Refund() { }\n}\n";

    private const string OrderTests =
        "namespace App.Tests;\n\npublic class OrderTests\n{\n    [Fact] public void Cancel_works() { }\n    [Fact] public void Ship_works() { }\n" +
        "    [Fact] public void Pay_works() { }\n    [Fact] public void Refund_works() { }\n    [Fact] public void Old_behavior() { }\n}\n";

    private const string CancelTest = "OrderTests.Cancel_works";
    private const string ShipTest = "OrderTests.Ship_works";

    private readonly string _root = Directory.CreateTempSubdirectory("pelly-testfirst-").FullName;
    private readonly FileSystem _fs = new();
    private readonly FileGuard _guard;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 26, 9, 30, 0, TimeSpan.Zero));
    private readonly ScriptedUserInteraction _ui = new() { IsInteractive = true };
    private readonly FakeExecutor _exec;

    public SpecRunnerTestFirstTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _guard = new FileGuard(_fs, TimeProvider.System);
        _exec = new FakeExecutor(_guard);
        Make("App.sln", "");
        Make("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Make("src/App/OrderService.cs", OrderService);
        Make("src/Tests/OrderTests.cs", OrderTests);
        Make("db/a.sql", "CREATE PROCEDURE dbo.usp_A AS SELECT 1;\n");
        Directory.CreateDirectory(P("changes"));
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Make(string rel, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), text, new UTF8Encoding(false));
    }

    private string SpecPath => P("changes/x.md");

    private void Spec(string criteria, string tasks) =>
        Make("changes/x.md", "---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n" + criteria + "\n## 작업\n" + tasks);

    private static string Cs(string id, string member, string acs, bool done = false) =>
        $"- [{(done ? "x" : " ")}] {id} cs src/App/OrderService.cs#OrderService.{member} 요청 {acs}{(done ? " <!-- run: R-OLD -->" : "")}\n";

    private static ApplyOptions Opts(bool cont = false, bool resume = false, bool reviewAtEnd = false, bool yes = true) =>
        new(cont, resume, reviewAtEnd, yes, false, null, false);

    private SpecRunner Runner() => new(_fs, _guard, _exec, _ui, _time, 160, groups: new PassingGroups(), groupTests: new PassingGroupTests());

    private Task<int> Run(ApplyOptions o) => Runner().RunAsync(SpecPath, o, TestContext.Current.CancellationToken);

    private JsonElement ApplyJson()
    {
        var dir = Assert.Single(Directory.GetDirectories(P(".pelly/runs"), "apply-*"));
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "apply.json"))).RootElement;
    }

    private JsonElement[] Criteria() => ApplyJson().GetProperty("criteria").EnumerateArray().ToArray();

    private static TestFirstOutcome Outcome(string test, RedState red, int breaks = 0) =>
        new(new Dictionary<string, RedState> { [test] = red }, breaks);

    [Fact]
    public async Task Apply_SingleLinkedTask_RedAndGreen()
    {
        Spec($"- AC1: 하나\n  test: {CancelTest}\n  breaks: OrderTests.Old_behavior\n", Cs("T1", "Cancel", "(AC1)"));
        _exec.TestFirst["T1"] = Outcome(CancelTest, RedState.Red, 1);

        Assert.Equal(0, await Run(Opts()));

        var tests = Assert.Single(_exec.Options).Tests!;
        Assert.Equal(TestCheck.Red | TestCheck.Green, Assert.Single(tests.Checks).Value);
        Assert.Equal(["OrderTests.Old_behavior"], tests.Breaks);
        var ac = Assert.Single(Criteria());
        Assert.Equal("AC1", ac.GetProperty("acId").GetString());
        Assert.Equal("Red", ac.GetProperty("red").GetString());
        Assert.Equal("Met", ac.GetProperty("outcome").GetString());
        Assert.Equal("R-T1", ac.GetProperty("greenRunId").GetString());
        Assert.Equal(1, ac.GetProperty("breaksExcluded").GetInt32());
    }

    [Fact]
    public async Task Apply_TwoCriteriaEndOnOneTask_EachCountsOnlyItsOwnBreaks()
    {
        Spec($"- AC1: 하나\n  test: {CancelTest}\n  breaks: OrderTests.Old_behavior\n- AC2: 둘\n  test: {ShipTest}\n  breaks: OrderTests.Pay_works\n",
            Cs("T1", "Cancel", "(AC1, AC2)"));
        _exec.TestFirst["T1"] = new TestFirstOutcome(new Dictionary<string, RedState> { [CancelTest] = RedState.Red, [ShipTest] = RedState.Red }, 2,
            ["App.Tests.OrderTests.Old_behavior", "App.Tests.OrderTests.Pay_works"]);

        Assert.Equal(0, await Run(Opts()));

        Assert.All(Criteria(), ac => Assert.Equal(1, ac.GetProperty("breaksExcluded").GetInt32()));
        var (applies, _) = PellyCoder.Core.Reporting.ApplyHistory.Load(_fs, new Workspace(_root, P(".pelly")), null);
        Assert.Equal(2, PellyCoder.Core.Reporting.ApplyReport.Build(applies).Criteria!.BreaksExcluded);
    }

    [Fact]
    public async Task Apply_TaskLastForAc1FirstForAc2()
    {
        Spec($"- AC1: 하나\n  test: {CancelTest}\n- AC2: 둘\n  test: {ShipTest}\n",
            Cs("T1", "Cancel", "(AC1)") + Cs("T2", "Ship", "(AC1, AC2)") + Cs("T3", "Pay", "(AC2)"));
        _exec.TestFirst["T1"] = Outcome(CancelTest, RedState.Red);
        _exec.TestFirst["T2"] = Outcome(ShipTest, RedState.RedCompile);

        Assert.Equal(0, await Run(Opts()));

        Assert.Equal(["T1", "T2", "T3"], _exec.Calls);
        Assert.Equal(TestCheck.Red, _exec.Options[0].Tests!.Checks[CancelTest]);
        Assert.Equal(TestCheck.Green, _exec.Options[1].Tests!.Checks[CancelTest]);
        Assert.Equal(TestCheck.Red, _exec.Options[1].Tests!.Checks[ShipTest]);
        Assert.Equal(TestCheck.Green, _exec.Options[2].Tests!.Checks[ShipTest]);
        var acs = Criteria();
        Assert.Equal(("Red", "Met", "R-T2"), (acs[0].GetProperty("red").GetString(), acs[0].GetProperty("outcome").GetString(), acs[0].GetProperty("greenRunId").GetString()));
        Assert.Equal(("RedCompile", "Met", "R-T3"), (acs[1].GetProperty("red").GetString(), acs[1].GetProperty("outcome").GetString(), acs[1].GetProperty("greenRunId").GetString()));
    }

    [Fact]
    public async Task Apply_Resume_RedSkippedGreenChecked()
    {
        Spec($"- AC1: 하나\n  test: {CancelTest}\n",
            Cs("T1", "Cancel", "(AC1)", done: true) + "- [ ] T2 sql db/a.sql 한 줄 추가\n" + Cs("T3", "Pay", "(AC1)"));

        Assert.Equal(0, await Run(Opts(resume: true)));

        Assert.Equal(["T2", "T3"], _exec.Calls);
        Assert.Null(_exec.Options[0].Tests);
        Assert.Equal(TestCheck.Green, Assert.Single(_exec.Options[1].Tests!.Checks).Value);
        var ac = Assert.Single(Criteria());
        Assert.Equal("Skipped", ac.GetProperty("red").GetString());
        Assert.Equal("Met", ac.GetProperty("outcome").GetString());
        Assert.Equal("R-T3", ac.GetProperty("greenRunId").GetString());
    }

    [Fact]
    public async Task Apply_Resume_AllLinkedDone_NotRun()
    {
        Spec($"- AC1: 하나\n  test: {CancelTest}\n",
            Cs("T1", "Cancel", "(AC1)", done: true) + Cs("T2", "Ship", "(AC1)", done: true) + "- [ ] T3 sql db/a.sql 한 줄 추가\n");

        Assert.Equal(0, await Run(Opts(resume: true)));

        Assert.Equal(["T3"], _exec.Calls);
        var ac = Assert.Single(Criteria());
        Assert.Equal("Skipped", ac.GetProperty("red").GetString());
        Assert.Equal("NotRun", ac.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, ac.GetProperty("greenRunId").ValueKind);
        Assert.Equal(0, ac.GetProperty("breaksExcluded").GetInt32());
    }

    [Fact]
    public async Task Apply_RedDeclined_StopsEvenWithContinue()
    {
        Spec($"- AC1: 하나\n  test: {CancelTest}\n", Cs("T1", "Cancel", "(AC1)") + Cs("T2", "Ship", ""));
        _exec.Exit["T1"] = 2;
        _exec.Kind["T1"] = "TestRedDeclined";
        _exec.TestFirst["T1"] = Outcome(CancelTest, RedState.AlreadyPassed);

        Assert.Equal(2, await Run(Opts(cont: true, yes: false)));

        Assert.Equal(["T1"], _exec.Calls);
        Assert.Contains("<!-- failed: R-T1 TestRedDeclined -->", File.ReadAllText(SpecPath));
        var ac = Assert.Single(Criteria());
        Assert.Equal("AlreadyPassed", ac.GetProperty("red").GetString());
        Assert.Equal("NotRun", ac.GetProperty("outcome").GetString());
        Assert.Equal(2, ApplyJson().GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task Apply_ReviewAtEnd_ListsAlreadyPassedAcs()
    {
        Spec($"- AC1: 하나\n  test: {CancelTest}\n- AC2: 둘\n  test: {ShipTest}\n", Cs("T1", "Cancel", "(AC1)") + Cs("T2", "Ship", "(AC2)"));
        _exec.TestFirst["T1"] = Outcome(CancelTest, RedState.AlreadyPassed);
        _exec.TestFirst["T2"] = Outcome(ShipTest, RedState.Red);
        _ui.ConfirmAnswers.Enqueue(true);

        Assert.Equal(0, await Run(Opts(reviewAtEnd: true, yes: false)));

        Assert.All(_exec.Options, o => Assert.True(o.Yes));
        Assert.Contains("이미 통과한 테스트로 판정된 수용 기준: AC1", _ui.Warnings);
        Assert.Equal(["모두 적용할까요?"], _ui.ConfirmMessages);
        Assert.All(Criteria(), ac => Assert.Equal("Met", ac.GetProperty("outcome").GetString()));
    }

    [Fact]
    public async Task Apply_ReviewAtEnd_Rejected_AllNotRun()
    {
        Spec($"- AC1: 하나\n  test: {CancelTest}\n- AC2: 둘\n  test: {ShipTest}\n", Cs("T1", "Cancel", "(AC1)") + Cs("T2", "Ship", "(AC2)"));
        _ui.ConfirmAnswers.Enqueue(false);

        Assert.Equal(2, await Run(Opts(reviewAtEnd: true, yes: false)));

        Assert.DoesNotContain(_ui.Warnings, w => w.StartsWith("이미 통과한 테스트로", StringComparison.Ordinal));
        Assert.Equal(OrderService, File.ReadAllText(P("src/App/OrderService.cs")));
        var acs = Criteria();
        Assert.Equal(2, acs.Length);
        Assert.All(acs, ac => Assert.Equal("NotRun", ac.GetProperty("outcome").GetString()));
    }

    [Fact]
    public async Task Apply_Criteria_MetNotMetNotRunUnverified()
    {
        Spec($"- AC1: 하나\n  test: {CancelTest}\n- AC2: 둘\n  test: {ShipTest}\n- AC3: 셋\n  test: OrderTests.Pay_works\n- AC4: 넷\n  test: OrderTests.Refund_works\n",
            Cs("T1", "Cancel", "(AC1)") + Cs("T2", "Ship", "(AC2)") + Cs("T3", "Pay", "(AC3)") + "- [ ] T4 sql db/a.sql 한 줄 추가 (AC4)\n");
        _exec.Exit["T2"] = 1;
        _exec.Kind["T2"] = "TestGreenFailed";
        _exec.TestFirst["T2"] = Outcome(ShipTest, RedState.Red);
        _exec.Exit["T3"] = 4; // stops --continue: T3's own exit 4 is NotMet

        Assert.Equal(4, await Run(Opts(cont: true)));

        Assert.Equal(["T1", "T2", "T3"], _exec.Calls);
        var acs = Criteria().ToDictionary(a => a.GetProperty("acId").GetString()!);
        Assert.Equal("Met", acs["AC1"].GetProperty("outcome").GetString());
        Assert.Equal("NotChecked", acs["AC1"].GetProperty("red").GetString()); // no TestFirst: not judged
        Assert.Equal("NotMet", acs["AC2"].GetProperty("outcome").GetString());
        Assert.Equal("Red", acs["AC2"].GetProperty("red").GetString());
        Assert.Equal("R-T2", acs["AC2"].GetProperty("greenRunId").GetString());
        Assert.Equal("NotMet", acs["AC3"].GetProperty("outcome").GetString());
        Assert.Equal("Unverified", acs["AC4"].GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, acs["AC4"].GetProperty("greenRunId").ValueKind);
    }

    [Fact]
    public async Task Apply_Criteria_StopBeforeLastLinkedTask_NotRun()
    {
        Spec($"- AC1: 하나\n  test: {CancelTest}\n", Cs("T1", "Cancel", "(AC1)") + Cs("T2", "Ship", "") + Cs("T3", "Pay", "(AC1)"));
        _exec.TestFirst["T1"] = Outcome(CancelTest, RedState.Red);
        _exec.Exit["T2"] = 1;

        Assert.Equal(1, await Run(Opts()));

        var ac = Assert.Single(Criteria());
        Assert.Equal("Red", ac.GetProperty("red").GetString());
        Assert.Equal("NotRun", ac.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, ac.GetProperty("greenRunId").ValueKind);
    }

    [Fact]
    public async Task Apply_GroupLastLinkedTask_MetByGroupResult()
    {
        Spec($"- AC1: 하나\n  test: {CancelTest}\n",
            "- [ ] T1 cs src/App/OrderService.cs#OrderService.Cancel 요청 (AC1) [G1]\n- [ ] T2 cs src/App/OrderService.cs#OrderService.Ship 요청 (AC1) [G1]\n");

        Assert.Equal(0, await Run(Opts()));

        Assert.All(_exec.Options, o => Assert.Null(o.Tests)); // the FSM does not test in group mode: the group does
        var ac = Assert.Single(Criteria());
        Assert.Equal("Red", ac.GetProperty("red").GetString());
        Assert.Equal("Met", ac.GetProperty("outcome").GetString());
        Assert.Equal("R-T2", ac.GetProperty("greenRunId").GetString());
    }

    [Fact]
    public async Task Apply_NoTestCriteria_NoCriteriaField()
    {
        Spec("- AC1: 하나\n", Cs("T1", "Cancel", "(AC1)"));

        Assert.Equal(0, await Run(Opts()));

        Assert.Null(Assert.Single(_exec.Options).Tests);
        Assert.False(ApplyJson().TryGetProperty("criteria", out _));
    }

    [Fact]
    public async Task Apply_ReviewAtEnd_Rejected_PartialRollback_KeepsOutcome()
    {
        Spec($"- AC1: 하나\n  test: {CancelTest}\n", Cs("T1", "Cancel", "(AC1)"));
        _ui.ConfirmAnswers.Enqueue(false);
        // Changed outside pelly before the answer: the rollback cannot restore it, so T1's edit and [x] stay.
        var ui = new ConfirmHookUi(_ui, () => File.AppendAllText(P("src/App/OrderService.cs"), "// outside\n"));
        var runner = new SpecRunner(_fs, _guard, _exec, ui, _time, 160, groups: new PassingGroups());

        Assert.Equal(4, await runner.RunAsync(SpecPath, Opts(reviewAtEnd: true, yes: false), TestContext.Current.CancellationToken));

        Assert.Contains("- [x] T1", File.ReadAllText(SpecPath));
        Assert.Equal("Met", Assert.Single(Criteria()).GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Apply_ReviewAtEnd_Rejected_PartialRollback_RestoredTaskCriterionNotRun()
    {
        Make("src/App/Billing.cs", "namespace App;\n\npublic class Billing\n{\n    public void Charge() { }\n}\n");
        Spec($"- AC1: 하나\n  test: {CancelTest}\n- AC2: 둘\n  test: {ShipTest}\n",
            Cs("T1", "Cancel", "(AC1)") + "- [ ] T2 cs src/App/Billing.cs#Billing.Charge 요청 (AC2)\n");
        _ui.ConfirmAnswers.Enqueue(false);
        // Only T1's file changes outside pelly: T1 keeps its edit and [x], T2 is restored and loses its [x].
        var ui = new ConfirmHookUi(_ui, () => File.AppendAllText(P("src/App/OrderService.cs"), "// outside\n"));
        var runner = new SpecRunner(_fs, _guard, _exec, ui, _time, 160, groups: new PassingGroups());

        Assert.Equal(4, await runner.RunAsync(SpecPath, Opts(reviewAtEnd: true, yes: false), TestContext.Current.CancellationToken));

        var spec = File.ReadAllText(SpecPath);
        Assert.Contains("- [x] T1", spec);
        Assert.Contains("- [ ] T2", spec);
        var acs = Criteria();
        Assert.Equal("Met", acs[0].GetProperty("outcome").GetString());
        Assert.Equal("NotRun", acs[1].GetProperty("outcome").GetString());
    }

    /// <summary>Delegates to the scripted UI and runs <paramref name="onConfirm"/> just before each confirm.</summary>
    private sealed class ConfirmHookUi(ScriptedUserInteraction inner, Action onConfirm) : IUserInteraction
    {
        public bool IsInteractive => inner.IsInteractive;

        public Task<bool> ConfirmAsync(string message, bool defaultValue, CancellationToken ct)
        {
            onConfirm();
            return inner.ConfirmAsync(message, defaultValue, ct);
        }

        public Task<T> SelectAsync<T>(string title, IReadOnlyList<T> choices, Func<T, string> label, CancellationToken ct) =>
            inner.SelectAsync(title, choices, label, ct);

        public void ShowDiff(string path, string before, string after) => inner.ShowDiff(path, before, after);
        public void ShowStatus(RunPhase phase, int attempt, string message) => inner.ShowStatus(phase, attempt, message);
        public void Warn(string message) => inner.Warn(message);
        public void ShowRunSummary(PellyCoder.Core.Logging.RunSummary summary, string? runDirectory) => inner.ShowRunSummary(summary, runDirectory);
    }

    /// <summary>Every build is verifiable and clean.</summary>
    private sealed class PassingGroups : IGroupVerifier
    {
        public Task<GroupBuild> BuildAsync(Workspace ws, IReadOnlyList<string> projects, CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(new GroupBuild(true, null, null, []));
    }

    /// <summary>Every group test baseline is RED for every plan test and every verdict passes.</summary>
    private sealed class PassingGroupTests : IGroupTester
    {
        public Task<GroupTestBaseline> BaselineAsync(Workspace ws, IReadOnlyList<string> scope, GroupTestPlan plan, CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(new GroupTestBaseline([], [], new HashSet<int>(),
                plan.Tests.ToDictionary(t => t, _ => RedState.Red), null));

        public Task<GroupTestVerdict> VerifyAsync(Workspace ws, GroupTestBaseline baseline, GroupTestPlan plan, CancellationToken ct) =>
            System.Threading.Tasks.Task.FromResult(new GroupTestVerdict(null, [], [], [], 0));
    }

    /// <summary>Appends a comment to the task's file through FileGuard and reports a scripted exit, kind and test-first outcome.</summary>
    private sealed class FakeExecutor(FileGuard guard) : ITaskExecutor
    {
        public List<string> Calls { get; } = [];
        public List<TaskRunOptions> Options { get; } = [];
        public Dictionary<string, int> Exit { get; } = [];
        public Dictionary<string, string> Kind { get; } = [];
        public Dictionary<string, TestFirstOutcome> TestFirst { get; } = [];

        public async Task<RunResult> ExecuteAsync(SpecTask task, Workspace ws, TaskRunOptions o, CancellationToken ct)
        {
            Calls.Add(task.Id);
            Options.Add(o);
            var path = Path.Combine(ws.Root, task.Path.Replace('/', Path.DirectorySeparatorChar));
            var original = guard.Read(path);
            int exit = Exit.GetValueOrDefault(task.Id);
            var outcome = TestFirst.GetValueOrDefault(task.Id);
            if (exit != 0)
                return new RunResult(exit, "R-" + task.Id, path, original, original, Kind.GetValueOrDefault(task.Id, "Verify"), TestFirst: outcome);
            var written = await guard.WriteAsync(original, original.Text + "// " + task.Id + "\n", CancellationToken.None);
            return new RunResult(0, "R-" + task.Id, path, original, written.Snapshot, null, TestFirst: outcome);
        }
    }
}
