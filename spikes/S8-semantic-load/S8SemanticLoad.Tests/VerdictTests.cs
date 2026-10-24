using S8SemanticLoad;

public class VerdictTests
{
    private static bool? P(IReadOnlyList<Judgement> j, string item) => j.Single(x => x.Item == item).Pass;

    [Fact] public void Verdict_passes_clean_small_solution()
    {
        var j = Verdict.Judge([ReportFx.Clean(open: 8, compile: 4), ReportFx.Clean(open: 5, compile: 3)]);
        Assert.All(j, x => Assert.True(x.Pass, x.Item));
        Assert.DoesNotContain(j, x => x.Item == Verdict.CorpusItem);
    }

    [Fact] public void Verdict_fails_on_low_call_rate() =>
        Assert.False(P(Verdict.Judge([ReportFx.Clean(rate: 0.98)]), Verdict.TypeInfoItem));

    [Fact] public void Verdict_ignores_failures_from_non_csharp_projects() =>
        Assert.True(P(Verdict.Judge([ReportFx.Clean(issues: [new IssueCount(WorkspaceIssueKind.UnsupportedProject, false, true, 1)])]), Verdict.LoadItem));

    [Fact] public void Verdict_fails_load_on_csharp_failure() =>
        Assert.False(P(Verdict.Judge([ReportFx.Clean(issues: [new IssueCount(WorkspaceIssueKind.Other, true, true, 1)])]), Verdict.LoadItem));

    // 최종 리뷰 I2: 경고는 실패가 아니다(설계 §7은 작업 공간 진단 「실패」 0).
    [Fact] public void Verdict_ignores_workspace_warnings() =>
        Assert.True(P(Verdict.Judge([ReportFx.Clean(issues: [new IssueCount(WorkspaceIssueKind.Other, true, false, 2)])]), Verdict.LoadItem));

    // 최종 리뷰 C1: 잰 것이 없으면 통과가 아니라 판정 불가다(설계 §3의 조용한 거짓 통과).
    [Fact] public void Verdict_type_info_not_judged_when_nothing_measured() =>
        Assert.Null(P(Verdict.Judge([ReportFx.Clean(callsTotal: 0, measured: 0, errorsExcluded: 804)]), Verdict.TypeInfoItem));

    [Fact] public void Verdict_probe_not_judged_with_zero_methods() =>
        Assert.Null(P(Verdict.Judge([ReportFx.Clean(probeMethods: 0)]), Verdict.ProbeItem));

    // 최종 리뷰 I3: 솔루션 밖 참조 프로젝트가 더 불러와져도 실패가 아니다.
    [Fact] public void Verdict_extra_loaded_projects_do_not_fail() =>
        Assert.True(P(Verdict.Judge([ReportFx.Clean(csharp: 1, extra: 1)]), Verdict.LoadItem));

    [Fact] public void Verdict_excludes_errors_of_unrestored_projects() =>
        Assert.True(P(Verdict.Judge([ReportFx.Clean(errorsCounted: 0, errorsExcluded: 120)]), Verdict.TypeInfoItem));

    [Fact] public void Verdict_time_not_judged_above_four_projects()
    {
        var j = Verdict.Judge([ReportFx.Clean(csharp: 6), ReportFx.Clean(csharp: 6)]);
        Assert.Null(P(j, Verdict.ColdItem));
        Assert.Null(P(j, Verdict.WarmItem));
    }

    [Fact] public void Verdict_cold_over_15_seconds_fails() =>
        Assert.False(P(Verdict.Judge([ReportFx.Clean(open: 12, compile: 4)]), Verdict.ColdItem));

    [Fact] public void Verdict_warm_is_null_with_one_run() =>
        Assert.Null(P(Verdict.Judge([ReportFx.Clean()]), Verdict.WarmItem));

    [Fact] public void Verdict_corpus_item_follows_a3() =>
        Assert.False(P(Verdict.Judge([ReportFx.Clean(a3: new A3Result(["x"], [], []))]), Verdict.CorpusItem));
}
