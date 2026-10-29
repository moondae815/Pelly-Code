using PellyCoder.Core.Fsm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Reporting;

namespace PellyCoder.Core.Tests.Reporting;

public sealed class RunReportTests
{
    private static RunRecord Rec(int i, int exit = 0, int attempts = 1, int formatErrors = 0, double ttft = 0.5, double queue = 0,
        string? failure = null, int think = 0, bool truncated = false, EditKind kind = EditKind.CSharp, double editSeconds = 2) =>
        new(RunHistoryTests.Summary($"20261001-10101{i}-aaaa", exit, attempts, formatErrors, ttft, queue, failure, think, truncated, kind, editSeconds),
            DateTimeOffset.UnixEpoch);

    private static RunRecord TfRec(int i, int exit, RedState red, GreenState green, int regressions)
    {
        var s = RunHistoryTests.Summary($"20261001-10101{i}-aaaa", exit) with { TestFirst = new TestFirstSummary(1, red, green, regressions, 0) };
        return new RunRecord(s, DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Build_TestFirstCounts()
    {
        var d = RunReport.Build(
        [
            TfRec(1, 0, RedState.Red, GreenState.Passed, 0),
            TfRec(2, 1, RedState.Red, GreenState.Failed, 0),
            TfRec(3, 1, RedState.Red, GreenState.NotChecked, 0),
            TfRec(4, 0, RedState.AlreadyPassed, GreenState.Passed, 0),
            TfRec(5, 1, RedState.Red, GreenState.Passed, 1),
        ]);

        Assert.Equal(new TestFirstCounts(Met: 1, Judged: 4, RedWarned: 1, RedChecked: 5), d.TestFirst);
    }

    [Fact]
    public void Build_TestFileChangedRun_NotMet()
    {
        var d = RunReport.Build([TfRec(1, 4, RedState.Red, GreenState.Passed, 0)]);

        Assert.Equal(new TestFirstCounts(0, 1, 0, 1), d.TestFirst);
    }

    [Fact]
    public void RunReport_SkipsInApplyRuns()
    {
        var inApply = TfRec(2, 0, RedState.AlreadyPassed, GreenState.Passed, 0);
        inApply = inApply with { Summary = inApply.Summary with { TestFirst = inApply.Summary.TestFirst! with { InApply = true } } };

        var d = RunReport.Build([TfRec(1, 0, RedState.Red, GreenState.Passed, 0), inApply]);

        Assert.Equal(new TestFirstCounts(Met: 1, Judged: 1, RedWarned: 0, RedChecked: 1), d.TestFirst);
        Assert.Null(RunReport.Build([inApply]).TestFirst);
    }

    [Fact]
    public void Build_NoTestFirstRuns_Null() => Assert.Null(RunReport.Build([Rec(1), Rec(2, exit: 1)]).TestFirst);

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

    [Fact]
    public void Build_BreaksExcludedSumsSingleRuns()
    {
        var a = RunHistoryTests.Summary("20261001-101010-aaaa", 0) with { TestFirst = new TestFirstSummary(1, RedState.Red, GreenState.Passed, 0, 2) };
        var b = RunHistoryTests.Summary("20261001-101011-bbbb", 0) with { TestFirst = new TestFirstSummary(1, RedState.Red, GreenState.Passed, 0, 1) };
        Assert.Equal(3, RunReport.Build([new RunRecord(a, DateTimeOffset.UnixEpoch), new RunRecord(b, DateTimeOffset.UnixEpoch)]).TestFirst!.BreaksExcluded);
    }

    private static RunRecord CtRec(int i, int exit, int attempts, string? failure = null, bool dryRun = false)
    {
        var s = RunHistoryTests.Summary($"20261001-10102{i}-aaaa", exit, attempts, failure: failure, kind: EditKind.CSharpTest) with
        {
            PromptVersion = "cs-test-v1",
            CharTest = new PellyCoder.Core.CharTests.CharTestSummary(3, 3, exit == 0 && !dryRun ? 3 : 0, 3, true, 3, null, "xUnit", false),
        };
        return new RunRecord(s, DateTimeOffset.UnixEpoch);
    }

    [Fact]
    public void Build_CharTestRuns_AreNotEditKpis()
    {
        var edit = new[] { Rec(1), Rec(2, exit: 1, failure: "BuildErrors") };
        var plain = RunReport.Build(edit);

        var mixed = RunReport.Build([.. edit, CtRec(1, 0, 1), CtRec(2, 1, 3, "CharTestBuild"), CtRec(3, 1, 2, "CharTestFlaky")]);

        Assert.Equal(plain.ExitCodes, mixed.ExitCodes);
        Assert.Equal(plain.RunsByKind, mixed.RunsByKind);
        Assert.Equal(plain.FailureKinds, mixed.FailureKinds);
        Assert.Equal(plain.AttemptDistribution, mixed.AttemptDistribution);
        Assert.Equal(plain.MeanPhaseSeconds, mixed.MeanPhaseSeconds);
        Assert.Equal(plain.Parse, mixed.Parse);
        Assert.Equal(plain.SuccessRate, mixed.SuccessRate);
        Assert.Equal(plain.TaskSeconds, mixed.TaskSeconds);
        Assert.Null(plain.CharTest);
        Assert.Equal(2, mixed.RunCount);
        Assert.Equal(1, mixed.SuccessCount);
        Assert.DoesNotContain(EditKind.CSharpTest, mixed.RunsByKind.Keys);
        Assert.DoesNotContain("CharTestBuild", mixed.FailureKinds.Keys);
    }

    [Fact]
    public void Build_CharTestCounts()
    {
        var d = RunReport.Build([CtRec(1, 0, 1), CtRec(2, 1, 3, "CharTestBuild"), CtRec(3, 1, 2, "CharTestFlaky"), CtRec(4, 0, 2)]);

        var ct = d.CharTest!;
        Assert.Equal(4, ct.Runs);
        Assert.Equal(2, ct.Kept);
        Assert.Equal(2.0, ct.MeanAttempts);
        Assert.Equal(new Dictionary<string, int> { ["CharTestBuild"] = 1, ["CharTestFlaky"] = 1 }, ct.FailureKinds);
        Assert.Equal("특성 테스트: 실행 4건, 남김 2건(성공률 50.0%), 평균 시도 2.0, 실패 유형 CharTestBuild 1, CharTestFlaky 1", ct.Line());
        Assert.Equal(0, d.RunCount);
    }

    [Fact]
    public void Build_CharTestDryRun_NotCountedAsKept()
    {
        var d = RunReport.Build([CtRec(1, 0, 1), CtRec(2, 0, 1, dryRun: true)]);

        Assert.Equal(2, d.CharTest!.Runs);
        Assert.Equal(1, d.CharTest.Kept);
        Assert.Contains("남김 1건(성공률 50.0%)", d.CharTest.Line());
    }

    [Fact]
    public void CharTestLine_NoFailures()
    {
        Assert.Equal("특성 테스트: 실행 1건, 남김 1건(성공률 100.0%), 평균 시도 1.0, 실패 유형 없음",
            RunReport.Build([CtRec(1, 0, 1)]).CharTest!.Line());
    }
}
