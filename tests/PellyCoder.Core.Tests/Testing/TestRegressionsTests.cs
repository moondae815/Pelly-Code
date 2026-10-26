using PellyCoder.Core.Models;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Verification;

namespace PellyCoder.Core.Tests.Testing;

public class TestRegressionsTests
{
    private static TestRun Run(int exit, params TestCaseResult[] results) =>
        new(results.ToList(), [], exit, false, true, null);

    private static TestCaseResult Pass(string n) => new(n, true, null);
    private static TestCaseResult Fail(string n) => new(n, false, "boom");

    [Fact]
    public void Find_NewFailureCounts_PreexistingDoesNot()
    {
        var before = Run(1, Pass("N.C.A"), Fail("N.C.B"));
        var after = Run(1, Fail("N.C.A"), Fail("N.C.B"));

        var (failures, exitOnly) = TestRegressions.Find(before, after, []);

        Assert.Equal("N.C.A", Assert.Single(failures).FullName);
        Assert.False(exitOnly);
    }

    [Fact]
    public void Find_ExcludedByMatch()
    {
        var before = Run(0, Pass("N.C.A"), Pass("N.C.B(x: 1)"));
        var after = Run(1, Fail("N.C.A"), Fail("N.C.B(x: 1)"));

        var (failures, exitOnly) = TestRegressions.Find(before, after, ["C.B"]);

        Assert.Equal("N.C.A", Assert.Single(failures).FullName);
        Assert.False(exitOnly);
    }

    [Fact]
    public void Find_AllNewFailuresExcluded_IsNotExitOnlyFailure()
    {
        var before = Run(0, Pass("N.C.A"));
        var after = Run(1, Fail("N.C.A"));

        var (failures, exitOnly) = TestRegressions.Find(before, after, ["N.C.A"]);

        Assert.Empty(failures);
        Assert.False(exitOnly);
    }

    [Fact]
    public void Find_HostCrash_LostPassingTests()
    {
        // Partial report after a pass: a test that passed before and is missing now did not pass (the cut-short rule).
        var before = Run(0, Pass("N.C.A"), Pass("N.C.B"));
        var after = Run(1, Pass("N.C.A"));

        var (failures, exitOnly) = TestRegressions.Find(before, after, []);

        var lost = Assert.Single(failures);
        Assert.Equal("N.C.B", lost.FullName);
        Assert.Equal(TestStep.NotRunFeedback, lost.Message);
        Assert.False(exitOnly);
    }

    [Fact]
    public void Find_HostCrash_ReportMissing_EveryPassingTestLost()
    {
        var before = Run(0, Pass("N.C.A"), Fail("N.C.B"));
        var after = new TestRun([], [], 1, false, false, "no report", ReportMissing: true);

        var (failures, exitOnly) = TestRegressions.Find(before, after, []);

        Assert.Equal(TestStep.NotRunFeedback, Assert.Single(failures).Message);
        Assert.False(exitOnly);
        // Every lost test excluded: the project still failed, with no nameable test.
        Assert.True(TestRegressions.Find(before, after, ["N.C.A"]).ExitOnlyFailure);
    }

    [Fact]
    public void Find_BaselineFailedAndNamesDiffer_NotCutShort()
    {
        var before = Run(1, Pass("N.C.A"), Fail("N.C.B"));
        var after = Run(1, Pass("N.C.A2"), Fail("N.C.B"));

        Assert.Empty(TestRegressions.Find(before, after, []).Failures);
    }

    [Fact]
    public void Find_ExitCodeOnly_BrokenExitIsExitOnlyFailure()
    {
        var before = new TestRun([], [], 0, false, false, null);
        var after = new TestRun([], [], 1, false, false, null);

        var (failures, exitOnly) = TestRegressions.Find(before, after, []);

        Assert.Empty(failures);
        Assert.True(exitOnly);
        Assert.False(TestRegressions.Find(after, after, []).ExitOnlyFailure);
    }

    [Fact]
    public void Find_BrokenExitWithoutNamedTest_IsExitOnlyFailure()
    {
        var before = Run(0, Pass("N.C.A"));
        var after = Run(1, Pass("N.C.A"));

        var (failures, exitOnly) = TestRegressions.Find(before, after, []);

        Assert.Empty(failures);
        Assert.True(exitOnly);
    }

    [Fact]
    public void Strict_AnyFailureButExcluded()
    {
        var after = Run(1, Pass("N.C.A"), Fail("N.C.B"), Fail("N.C.Red(x: 1)"));

        var failures = TestRegressions.Strict(after, ["C.Red"]);

        Assert.Equal("N.C.B", Assert.Single(failures).FullName);
    }
}
