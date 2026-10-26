using PellyCoder.Core.Models;
using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Tests.Testing;

public class TestFirstJudgeTests
{
    private static TestRun Run(IEnumerable<TestCaseResult> results, bool perTest = true, IEnumerable<string>? skipped = null,
        IReadOnlyList<Diagnostic>? errors = null) =>
        new(results.ToList(), errors ?? [], 0, false, perTest, null, Skipped: skipped?.ToList());

    private static TestCaseResult Pass(string n) => new(n, true, null);
    private static TestCaseResult Fail(string n, string msg = "boom") => new(n, false, msg, "detail-" + n, "stack-" + n);

    private static RedResult Red(TestRun? run, params string[] names) =>
        TestFirstJudge.Red(["p"], [run], names);

    [Fact]
    public void Red_BaselineNull_Unverifiable()
    {
        var r = Red(null, "A.B");
        Assert.Equal("기준 테스트를 실행하지 못해 지정한 테스트를 판정할 수 없습니다", r.Unverifiable);
        Assert.Equal("TestUnverifiable", r.FailureKind);
    }

    [Fact]
    public void Red_BuildErrors_Unverifiable()
    {
        var r = Red(Run([], errors: [new Diagnostic("f.cs", 1, 1, "error", "CS1", "x")]), "A.B");
        Assert.Equal("테스트 프로젝트가 원본 상태에서 빌드되지 않습니다 — 새 시그니처로 쓴 테스트는 pelly apply의 작업 그룹에서만 받습니다", r.Unverifiable);
        Assert.Equal("TestUnverifiable", r.FailureKind);
    }

    [Fact]
    public void Red_ExitCodeOnly_Unverifiable()
    {
        var r = Red(Run([], perTest: false), "A.B");
        Assert.Equal("테스트별 결과가 없어 지정한 테스트를 판정할 수 없습니다 (레거시는 test.command에 {trx}를 넣으세요)", r.Unverifiable);
        Assert.Equal("TestUnverifiable", r.FailureKind);
    }

    [Fact]
    public void Red_TwoFullNames_IsAmbiguous()
    {
        var r = Red(Run([Pass("N.X.T.Do"), Pass("N.Y.T.Do")]), "T.Do");
        Assert.Equal("TestAmbiguous", r.FailureKind);
        Assert.Equal("테스트 이름 T.Do이(가) 여러 테스트와 맞습니다: N.X.T.Do, N.Y.T.Do", r.Unverifiable);
    }

    [Fact]
    public void Red_Skipped_Unverifiable()
    {
        var r = Red(Run([], skipped: ["N.T.Do"]), "T.Do");
        Assert.Equal("TestUnverifiable", r.FailureKind);
        Assert.Equal("테스트 T.Do이(가) 건너뛰도록 표시되어 있습니다", r.Unverifiable);
    }

    [Fact]
    public void Red_PartlySkippedTheory_Unverifiable()
    {
        var r = Red(Run([Fail("N.T.Do(1)")], skipped: ["N.T.Do(2)"]), "T.Do");
        Assert.Equal("TestUnverifiable", r.FailureKind);
        Assert.Equal("테스트 T.Do이(가) 건너뛰도록 표시되어 있습니다", r.Unverifiable);
    }

    [Fact]
    public void Red_Missing_NotFound()
    {
        var r = Red(Run([Pass("N.T.Other")]), "T.Do");
        Assert.Equal("TestNotFound", r.FailureKind);
        Assert.Equal("테스트 T.Do을(를) 테스트 실행 결과에서 찾지 못했습니다 (test.projects 설정을 확인하세요)", r.Unverifiable);
    }

    [Fact]
    public void Red_Failing_IsRed()
    {
        var r = Red(Run([Fail("N.T.Do", "Assert.Equal() Failure: Values differ")]), "T.Do");
        Assert.Null(r.Unverifiable);
        var t = Assert.Single(r.Tests);
        Assert.Equal(RedState.Red, t.State);
        Assert.Equal("Assert.Equal() Failure: Values differ", t.FirstMessage);
        Assert.Equal("T.Do", t.Name);
        Assert.Equal("N.T.Do", t.FullName);
        Assert.Equal(0, t.ProjectIndex);
    }

    [Fact]
    public void Red_Passing_IsAlreadyPassed()
    {
        var r = Red(Run([Pass("N.T.Do")]), "T.Do");
        Assert.Equal(RedState.AlreadyPassed, Assert.Single(r.Tests).State);
    }

    [Fact]
    public void Red_TheoryRows_OneFails_IsRed()
    {
        var r = Red(Run([Pass("N.T.Do(1)"), Fail("N.T.Do(2)", "bad row")]), "T.Do");
        var t = Assert.Single(r.Tests);
        Assert.Equal(RedState.Red, t.State);
        Assert.Equal("bad row", t.FirstMessage);
        Assert.Equal("N.T.Do", t.FullName);
    }

    [Fact]
    public void Red_FoundInSecondProject_IndexIsSecond()
    {
        var r = TestFirstJudge.Red(["a", "b"], [Run([Pass("N.T.Other")]), Run([Fail("M.T.Do")])], ["T.Do"]);
        Assert.Equal(1, Assert.Single(r.Tests).ProjectIndex);
    }

    private static RedTest Red1(string full = "N.T.Do") => new("T.Do", full, 0, RedState.Red, "m");

    [Fact]
    public void Green_AllPass_Empty() =>
        Assert.Empty(TestFirstJudge.Green([Red1()], [Run([Pass("N.T.Do")])]));

    [Fact]
    public void Green_MissingFromRun_Fails()
    {
        var f = Assert.Single(TestFirstJudge.Green([Red1()], [Run([Pass("N.T.Other")])]));
        Assert.Equal("not run", f.Detail);
        Assert.Equal("N.T.Do", f.FullName);
    }

    [Fact]
    public void Green_Skipped_Fails()
    {
        var f = Assert.Single(TestFirstJudge.Green([Red1()], [Run([], skipped: ["N.T.Do"])]));
        Assert.Equal("not run", f.Detail);
    }

    [Fact]
    public void Green_OneTheoryRowFails_Fails()
    {
        var f = Assert.Single(TestFirstJudge.Green([Red1()], [Run([Pass("N.T.Do(1)"), Fail("N.T.Do(2)")])]));
        Assert.Equal("detail-N.T.Do(2)", f.Detail);
        Assert.Equal("stack-N.T.Do(2)", f.StackTrace);
    }

    [Fact]
    public void Green_PartlySkippedTheory_Fails()
    {
        var f = Assert.Single(TestFirstJudge.Green([Red1()], [Run([Pass("N.T.Do(1)")], skipped: ["N.T.Do(2)"])]));
        Assert.Equal("not run", f.Detail);
    }

    [Fact]
    public void Green_FailingRowWithoutDetail_FallsBackToMessage()
    {
        var f = Assert.Single(TestFirstJudge.Green([Red1()], [Run([new TestCaseResult("N.T.Do", false, "the message")])]));
        Assert.Equal("the message", f.Detail);
    }

    [Fact]
    public void Green_NullRun_Fails()
    {
        var f = Assert.Single(TestFirstJudge.Green([Red1()], [null]));
        Assert.Equal("not run", f.Detail);
    }

    [Fact]
    public void BreakWarnings_FailingOrMissing_Warns()
    {
        var w = TestFirstJudge.BreakWarnings([Run([Fail("N.T.Bad"), Pass("N.T.Good")])], ["T.Bad", "T.Gone", "T.Good"]);
        Assert.Equal(
            ["깨질 것으로 정한 테스트 T.Bad이(가) 원본 상태에서 통과하지 않습니다",
             "깨질 것으로 정한 테스트 T.Gone이(가) 원본 상태에서 통과하지 않습니다"], w);
    }

    private static TestRun FromTrx(string fixture)
    {
        var report = TrxParser.ParseFull(TrxParserTests.Fixture(fixture));
        return Run(report.Results, skipped: report.Skipped);
    }

    [Fact]
    public void Green_ShortNameDottedArgumentRowFails_IsNotGreen()
    {
        // Baseline: both rows pass; after the change the dotted row fails and must count.
        var baseline = Run([Fail("Shop.Tests.OrderTests.Refund (1)")]);
        var red = Red(baseline, "OrderTests.Refund");
        var test = Assert.Single(red.Tests);
        var failure = Assert.Single(TestFirstJudge.Green([test], [FromTrx("short-names-dotted-args.trx")]));
        Assert.Equal("Shop.Tests.OrderTests.Refund", failure.FullName);
        Assert.Equal("Expected 1.5", failure.Detail);
    }

    [Fact]
    public void Red_NestedClass_MatchesInnerName()
    {
        var r = Red(FromTrx("nested-class.trx"), "Inner.Cancel", "Outer.Inner.Refund");
        Assert.Null(r.Unverifiable);
        Assert.Equal(["Shop.Tests.Outer.Inner.Cancel", "Shop.Tests.Outer.Inner.Refund"], r.Tests.Select(t => t.FullName));
        Assert.Equal([RedState.Red, RedState.AlreadyPassed], r.Tests.Select(t => t.State));
        Assert.Empty(TestFirstJudge.Green([r.Tests[1]], [FromTrx("nested-class.trx")]));
    }
}
