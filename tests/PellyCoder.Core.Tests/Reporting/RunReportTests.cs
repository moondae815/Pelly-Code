using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;

namespace PellyCoder.Core.Tests.Reporting;

public sealed class RunReportTests
{
    private static RunRecord Rec(int i, int exit = 0, int attempts = 1, int formatErrors = 0, double ttft = 0.5, double queue = 0,
        string? failure = null, int think = 0, bool truncated = false, EditKind kind = EditKind.CSharp, double editSeconds = 2) =>
        new(RunHistoryTests.Summary($"20261001-10101{i}-aaaa", exit, attempts, formatErrors, ttft, queue, failure, think, truncated, kind, editSeconds),
            DateTimeOffset.UnixEpoch);

    [Fact]
    public void Build_RatesAndPercentiles()
    {
        var d = RunReport.Build(
        [
            Rec(1, ttft: 1, queue: 0.1, attempts: 2, formatErrors: 1, think: 300, truncated: true),
            Rec(2, ttft: 2, queue: 0.2, attempts: 1),
            Rec(3, ttft: 0, attempts: 1), // no TTFT measured: excluded from TTFT/queue
            Rec(4, exit: 1, ttft: 3, queue: 0.3, attempts: 3, formatErrors: 0, failure: "BuildErrors", think: 100, kind: EditKind.Sql, editSeconds: 10),
        ]);

        Assert.Equal(4, d.RunCount);
        Assert.Equal(3, d.SuccessCount);
        Assert.Equal(0.75, d.SuccessRate);
        Assert.Equal(1, d.ExitCodes[1]);
        Assert.Equal(3, d.RunsByKind[EditKind.CSharp]);
        Assert.Equal(1, d.RunsByKind[EditKind.Sql]);
        Assert.Equal(3, d.TtftSamples);
        Assert.Equal(2, d.Ttft!.Median);
        Assert.Equal(3, d.Ttft.P90);
        Assert.Equal(0.2, d.Queue!.Median);
        Assert.Equal((6, 7), (d.Parse.Ok, d.Parse.Total));
        Assert.Equal(6.0 / 7, d.Parse.Rate);
        Assert.Equal(300, d.ThinkMax);
        Assert.Equal(1, d.ThinkTruncatedCount);
        Assert.Equal(3, d.TaskSeconds!.Median); // phase sums: 3,3,3,11
        Assert.Equal(11, d.TaskSeconds.P90);
        Assert.Equal(4.0, d.MeanPhaseSeconds[RunPhase.Edit]);
        Assert.Equal(2, d.AttemptDistribution[1]);
        Assert.Equal(1, d.AttemptDistribution[3]);
    }

    [Fact]
    public void Build_FailureKindDistribution_UnknownAsOther()
    {
        var d = RunReport.Build(
        [
            Rec(1, exit: 1, failure: "BuildErrors"),
            Rec(2, exit: 1, failure: "BuildErrors"),
            Rec(3, exit: 1, failure: "C:\\secret\\path.cs"),
            Rec(4, exit: 1, failure: "something odd"),
            Rec(5),
        ]);

        Assert.Equal(2, d.FailureKinds["BuildErrors"]);
        Assert.Equal(2, d.FailureKinds["Other"]);
        Assert.DoesNotContain("C:\\secret\\path.cs", d.FailureKinds.Keys);
        Assert.Equal(2, d.FailureKinds.Count);
    }

    [Fact]
    public void Build_Empty_HasNoSpreads()
    {
        var d = RunReport.Build([]);
        Assert.Equal(0, d.RunCount);
        Assert.Null(d.Ttft);
        Assert.Null(d.Parse.Rate);
    }
}
