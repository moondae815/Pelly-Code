using PellyCoder.Core.CharTests;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Tests.CharTests;

public sealed class CharTestRunJudgeTests
{
    private const string TestFile = "/ws/tests/CalcCharTests.cs";
    private static readonly string[] New = ["Ns.CalcCharTests.A", "Ns.CalcCharTests.B"];

    private static TestCaseResult R(string name, bool passed, string? msg = null) => new(name, passed, msg);

    private static TestRun Run(IReadOnlyList<TestCaseResult> results, IReadOnlyList<Diagnostic>? errors = null,
        bool reportMissing = false, bool aborted = false, bool timedOut = false, string? error = null) =>
        new(results, errors ?? [], results.All(r => r.Passed) ? 0 : 1, timedOut, true, error, reportMissing, aborted);

    private static Diagnostic Err(string file) => new(file, 3, 1, "error", "CS0103", "bad");

    private static readonly TestRun Baseline = Run([R("Ns.OldTests.Old1", true), R("Ns.OldTests.Old2", false, "x")]);

    [Fact]
    public void OnCurrent_AllNewPass_Ok()
    {
        var after = Run([R("Ns.OldTests.Old1", true), R("Ns.OldTests.Old2", false), R("Ns.CalcCharTests.A", true), R("Ns.CalcCharTests.B(x: 1)", true)]);
        var v = CharTestRunJudge.OnCurrent(Baseline, after, New, TestFile);
        Assert.Null(v.FailureKind);
    }

    [Fact]
    public void OnCurrent_NewFails_FailsOnCurrentWithMessage()
    {
        var after = Run([R("Ns.OldTests.Old1", true), R("Ns.OldTests.Old2", false), R("Ns.CalcCharTests.A", false, "Expected 5 Actual 4"), R("Ns.CalcCharTests.B", true)]);
        var v = CharTestRunJudge.OnCurrent(Baseline, after, New, TestFile);
        Assert.Equal(FailureKinds.CharTestFailsOnCurrent, v.FailureKind);
        Assert.Equal(["Ns.CalcCharTests.A"], v.FailingTests);
        Assert.Contains("Actual 4", v.Details.Single().Message);
    }

    [Fact]
    public void OnCurrent_NewMissingFromResults_Fails()
    {
        var after = Run([R("Ns.OldTests.Old1", true), R("Ns.OldTests.Old2", false), R("Ns.CalcCharTests.A", true)]);
        var v = CharTestRunJudge.OnCurrent(Baseline, after, New, TestFile);
        Assert.Equal(FailureKinds.CharTestFailsOnCurrent, v.FailureKind);
        Assert.Equal(["Ns.CalcCharTests.B"], v.FailingTests);
    }

    [Fact]
    public void OnCurrent_BuildErrorInTestFile_CharTestBuild()
    {
        var v = CharTestRunJudge.OnCurrent(Baseline, Run([], [Err(TestFile)]), New, TestFile);
        Assert.Equal(FailureKinds.CharTestBuild, v.FailureKind);
    }

    [Fact]
    public void OnCurrent_BuildErrorElsewhere_Unverifiable()
    {
        var v = CharTestRunJudge.OnCurrent(Baseline, Run([], [Err("/ws/src/Other.cs")]), New, TestFile);
        Assert.Equal(FailureKinds.BuildUnverifiable, v.FailureKind);
    }

    [Fact]
    public void BaselineFailures_AreNotRegressions()
    {
        var after = Run([R("Ns.OldTests.Old1", true), R("Ns.OldTests.Old2", false, "x"), R("Ns.CalcCharTests.A", true), R("Ns.CalcCharTests.B", true)]);
        Assert.Null(CharTestRunJudge.OnCurrent(Baseline, after, New, TestFile).FailureKind);
    }

    [Fact]
    public void OnCurrent_ExistingNewlyFails_Regression()
    {
        var after = Run([R("Ns.OldTests.Old1", false, "boom"), R("Ns.OldTests.Old2", false), R("Ns.CalcCharTests.A", true), R("Ns.CalcCharTests.B", true)]);
        var v = CharTestRunJudge.OnCurrent(Baseline, after, New, TestFile);
        Assert.Equal(FailureKinds.CharTestRegression, v.FailureKind);
        Assert.Equal(["Ns.OldTests.Old1"], v.FailingTests);
    }

    [Fact]
    public void Stable_Differs_Flaky()
    {
        var first = Run([R("Ns.CalcCharTests.A", true), R("Ns.CalcCharTests.B", true)]);
        var second = Run([R("Ns.CalcCharTests.A", true), R("Ns.CalcCharTests.B", false, "m")]);
        var v = CharTestRunJudge.Stable(first, second, New);
        Assert.Equal(FailureKinds.CharTestFlaky, v.FailureKind);
        Assert.Equal(["Ns.CalcCharTests.B"], v.FailingTests);
        Assert.Null(CharTestRunJudge.Stable(first, first, New).FailureKind);
    }

    [Fact]
    public void UnderMutation_Survivor_Named()
    {
        var m = Run([R("Ns.CalcCharTests.A", true), R("Ns.CalcCharTests.B", false, "threw")]);
        var v = CharTestRunJudge.UnderMutation(m, New);
        Assert.Equal(FailureKinds.CharTestSurvivesMutation, v.FailureKind);
        Assert.Equal(["Ns.CalcCharTests.A"], v.FailingTests);
    }

    [Fact]
    public void UnderMutation_AllFail_Ok() =>
        Assert.Null(CharTestRunJudge.UnderMutation(Run([R("Ns.CalcCharTests.A", false), R("Ns.CalcCharTests.B", false)]), New).FailureKind);

    [Fact]
    public void UnderMutation_BuildError_Unbuildable()
    {
        var v = CharTestRunJudge.UnderMutation(Run([], [Err("/ws/src/Calc.cs")]), New);
        Assert.Equal(FailureKinds.CharTestMutationUnbuildable, v.FailureKind);
    }

    [Fact]
    public void UnderMutation_NoResults_Unbuildable() =>
        Assert.Equal(FailureKinds.CharTestMutationUnbuildable, CharTestRunJudge.UnderMutation(Run([]), New).FailureKind);

    [Fact]
    public void Baseline_ReportMissing_Unusable() =>
        Assert.NotNull(CharTestRunJudge.BaselineUsable(Run([], reportMissing: true)));

    [Fact]
    public void Baseline_OnlyTestFailures_Usable() =>
        Assert.Null(CharTestRunJudge.BaselineUsable(Baseline));

    [Fact]
    public void OnCurrent_RunnerError_Unverifiable()
    {
        var v = CharTestRunJudge.OnCurrent(Baseline, Run([], error: "launch failed"), New, TestFile);
        Assert.Equal(FailureKinds.BuildUnverifiable, v.FailureKind);
        Assert.Equal("launch failed", v.Detail);
    }

    [Fact]
    public void OnCurrent_TimedOutNoResults_Unverifiable() =>
        Assert.Equal(FailureKinds.BuildUnverifiable, CharTestRunJudge.OnCurrent(Baseline, Run([], timedOut: true), New, TestFile).FailureKind);

    [Fact]
    public void Stable_SecondRunErrors_Unverifiable()
    {
        var first = Run([R("Ns.CalcCharTests.A", true), R("Ns.CalcCharTests.B", true)]);
        Assert.Equal(FailureKinds.BuildUnverifiable, CharTestRunJudge.Stable(first, Run([], error: "x"), New).FailureKind);
    }

    [Fact]
    public void Stable_MissingInSecond_Flaky()
    {
        var first = Run([R("Ns.CalcCharTests.A", true), R("Ns.CalcCharTests.B", true)]);
        var second = Run([R("Ns.CalcCharTests.A", true)]);
        Assert.Equal(FailureKinds.CharTestFlaky, CharTestRunJudge.Stable(first, second, New).FailureKind);
    }

    [Fact]
    public void UnderMutation_TheoryRowSurvives_Survivor()
    {
        var m = Run([R("Ns.CalcCharTests.A(x: 1)", false), R("Ns.CalcCharTests.A(x: 2)", true), R("Ns.CalcCharTests.B", false)]);
        Assert.Equal(["Ns.CalcCharTests.A"], CharTestRunJudge.UnderMutation(m, New).FailingTests);
    }

    [Fact]
    public void OnCurrent_OneTheoryRowFails_FailsOnCurrent()
    {
        var after = Run([R("Ns.OldTests.Old1", true), R("Ns.CalcCharTests.A(x: 1)", true), R("Ns.CalcCharTests.A(x: 2)", false, "m"), R("Ns.CalcCharTests.B", true)]);
        Assert.Equal(FailureKinds.CharTestFailsOnCurrent, CharTestRunJudge.OnCurrent(Baseline, after, New, TestFile).FailureKind);
    }

    [Fact]
    public void Baseline_Unusable_Reasons()
    {
        Assert.NotNull(CharTestRunJudge.BaselineUsable(Run([], [Err("/a.cs")])));
        Assert.NotNull(CharTestRunJudge.BaselineUsable(Run([], aborted: true)));
        Assert.NotNull(CharTestRunJudge.BaselineUsable(Run([], timedOut: true)));
        Assert.Contains("boom", CharTestRunJudge.BaselineUsable(Run([], error: "boom")));
    }
}
