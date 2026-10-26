using PellyCoder.Core.Reporting;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

public sealed class TestFirstMetricsTests
{
    [Fact]
    public void Metrics_Counts()
    {
        var m = TestFirstMetrics.Build([
            Row("a", VerificationResult.Passed, 0, new("Red", "Passed", "Failed", 0, 1)),
            Row("b", VerificationResult.Failed, 1, new("Red", "Failed", "Failed", 0, 0)),
            Row("c", VerificationResult.Passed, 0, new("AlreadyPassed", "Passed", "Passed", 0, 0)),
            Row("d", VerificationResult.Failed, 1, new("Red", "NotChecked", "NotChecked", 0, 0)),
            Row("e", VerificationResult.Passed, 0, null),
        ]);
        Assert.Equal(new TestFirstMetricsData(Ran: 4, Met: 1, Judged: 3, RedWarned: 1, RedChecked: 4, BlockedWrongPatches: 2, FixedByFeedback: 1, BreaksExcluded: 1), m);
    }

    [Fact]
    public void Metrics_ExitTwoIsNotJudged_RegressionIsNotMet()
    {
        var m = TestFirstMetrics.Build([
            Row("a", VerificationResult.Failed, 2, new("Red", "Passed", "Passed", 0, 0)),
            Row("b", VerificationResult.Passed, 0, new("Red", "Passed", "Passed", 1, 0)),
        ]);
        Assert.Equal(1, m.Judged);
        Assert.Equal(0, m.Met);
    }

    internal static TaskResult Row(string id, string status, int exit, TestFirstResult? tf) =>
        new(id, "cs", status, exit, 1, [], VerificationResult.Pass, VerificationResult.Passed, false,
            status == VerificationResult.Failed ? "Judge" : null, 1.0, 0.3, 100, false, ["Ok"], null, false, TestFirst: tf);
}
