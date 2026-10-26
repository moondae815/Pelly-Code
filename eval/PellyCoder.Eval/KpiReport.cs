using System.Globalization;
using System.Text;
using PellyCoder.Core.Reporting;

namespace PellyCoder.Eval;

/// <summary>KPI table of a benchmark run (PRD §2.2 rows, in that order) plus the distributions behind it.</summary>
public static class KpiReport
{
    /// <summary>Nearest-rank percentile (see <see cref="KpiMath.Percentile"/>); the report only asks for non-empty sets.</summary>
    public static double Percentile(IReadOnlyList<double> values, int percent) =>
        KpiMath.Percentile(values, percent / 100.0) ?? throw new ArgumentException("값이 없습니다.", nameof(values));

    /// <summary>EDIT attempts that were not format errors ÷ all EDIT attempts, from the run summaries; null when there are none.</summary>
    public static double? ParseSuccessRate(IEnumerable<(int Attempts, int FormatErrors)> runs)
    {
        var (ok, total) = KpiMath.ParseSuccess(runs);
        return total == 0 ? null : (double)ok / total;
    }

    public static string Render(IReadOnlyList<TaskResult> results, string header)
    {
        var ran = results.Where(r => r.Status != VerificationResult.Skipped).ToList();
        var exit0 = ran.Where(r => r.ExitCode == 0).ToList();
        // No generation recorded (failed before EDIT, or summary missing) leaves TTFT 0 or null: not a measurement.
        var ttft = ran.Where(r => r.TtftSeconds is > 0).Select(r => r.TtftSeconds!.Value).ToList();
        var wall = ran.Where(r => r.WallSeconds is not null).Select(r => r.WallSeconds!.Value).ToList();
        var parse = ParseSuccessRate(ran.Select(r => (r.Attempts, r.FormatErrors)));
        var edits = ran.Sum(r => r.Attempts);

        var sb = new StringBuilder();
        sb.AppendLine("# 벤치마크 결과").AppendLine().AppendLine(header).AppendLine();
        var environment = results.Count(r => r.SkipReason?.StartsWith(TaskResult.EnvironmentPrefix, StringComparison.Ordinal) == true);
        sb.AppendLine($"작업 {results.Count}건 — 실행 {ran.Count}건, 건너뜀 {results.Count - ran.Count}건"
            + (environment > 0 ? $" (환경 오류 {environment}건)" : "")).AppendLine();
        sb.AppendLine("## KPI (PRD §2.2)").AppendLine();
        sb.AppendLine("| 지표 항목 | 목표 | 측정값 |").AppendLine("| :--- | :--- | :--- |");
        sb.AppendLine($"| 검증 불변 조건 | 100% | {Invariant(exit0)} |");
        sb.AppendLine($"| 초기 응답 지연 (TTFT) | < 1.0초 | {MedianP90(ttft, "초")} |");
        sb.AppendLine($"| 추론 예산 제어 (Thinking) | ≤ 512 토큰 | 최대 {(ran.Count == 0 ? 0 : ran.Max(r => r.ThinkTokens))} 토큰, 잘림 {ran.Count(r => r.ThinkTruncated)}건 |");
        sb.AppendLine($"| 패치 포맷 파싱 성공률 | > 99.5% | {(parse is { } p ? $"{Pct(p)} (EDIT {edits}회)" : "— (EDIT 없음)")} |");
        var tooling = ran.Count(r => r.FailureType == "JudgeTooling");
        sb.AppendLine($"| 최종 작업 성공률 | ≥ 85% | {SuccessRates(ran)} (판정 도구 오류로 미판정 {tooling}건) |");
        sb.AppendLine($"| 단일 작업 완료 시간 | 중위값 < 15초 / P90 < 30초 | {MedianP90(wall, "초")} |");
        sb.AppendLine();
        if (ran.Any(r => r.TestFirst is not null)) TestFirstMetrics.AppendSection(sb, TestFirstMetrics.Build(ran));

        sb.AppendLine("## 실패 유형 분포").AppendLine();
        var failures = ran.Where(r => r.Status == VerificationResult.Failed)
            .GroupBy(r => r.FailureType ?? "(미상)").OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (failures.Count == 0) sb.AppendLine("실패 없음");
        else
        {
            sb.AppendLine("| 실패 유형 | 건수 | 작업 |").AppendLine("| :--- | ---: | :--- |");
            foreach (var g in failures) sb.AppendLine($"| {g.Key} | {g.Count()} | {string.Join(", ", g.Select(r => r.Id))} |");
        }
        sb.AppendLine();

        var legacySkipped = ran.Count(r => r.IsLegacy && r.Independent?.Detail == VerificationResult.LegacyBuildSkipped);
        sb.AppendLine($"Legacy 빌드 판정 생략: {legacySkipped}건 (설계 §10.6 — Windows가 아니면 패치 적용·모양 검사까지만 판정)").AppendLine();

        var skipped = results.Where(r => r.Status == VerificationResult.Skipped).ToList();
        if (skipped.Count > 0)
        {
            sb.AppendLine("## 건너뛴 작업").AppendLine();
            foreach (var r in skipped) sb.AppendLine($"* {r.Id}: {r.SkipReason}");
            sb.AppendLine();
        }

        sb.AppendLine("## 작업별 결과").AppendLine();
        sb.AppendLine("| id | 판정 | 종료 | 시도 | 독립 검증 | 시간(초) | TTFT(초) | 사유 |");
        sb.AppendLine("| :--- | :--- | ---: | ---: | :--- | ---: | ---: | :--- |");
        foreach (var r in results)
            sb.AppendLine($"| {r.Id} | {r.Status} | {r.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "—"} | {r.Attempts} | "
                + $"{IndependentCell(r.Independent)} | {Num(r.WallSeconds)} | {Num(r.TtftSeconds)} | {Cell(r.SkipReason ?? string.Join("; ", r.Reasons))} |");
        return sb.ToString();
    }

    /// <summary>The top-level <c>summary.md</c> of <c>--repeat N</c> (the same data as <c>summary.json</c>).</summary>
    public static string RenderRepeat(RepeatSummaryData s, string header)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 반복 벤치마크 요약").AppendLine().AppendLine(header);
        if (s.Conditions is { } c)
            sb.AppendLine($"* 조건: 모델 `{c.Model}` · 프롬프트 `{c.CsPromptVersion}`·`{c.SqlPromptVersion}` · thinkMode {c.ThinkMode} · "
                + $"budget.think {c.BudgetThink} · sampling think {Sampling(c.SamplingThink)} / code {Sampling(c.SamplingCode)}");
        if (s.Cancelled) sb.AppendLine("* **중단됨** — 끝난 회차만 집계했습니다");
        sb.AppendLine();

        sb.AppendLine($"작업 {s.TaskCount}건 × {s.RunCount}회").AppendLine();
        sb.AppendLine("## 전체").AppendLine();
        sb.AppendLine($"* 평균 성공 작업 수: {Num(s.MeanSuccesses)} (표본 표준편차 {Num(s.StdDevSuccesses)}; 회차별 {string.Join(", ", s.Runs.Select(r => r.Successes))})");
        sb.AppendLine($"* 작업 시간 중위값(전 회차): {Num(s.MedianWallSeconds)}초");
        sb.AppendLine($"* 검증 불변 조건 위반: 회차별 {string.Join(", ", s.Runs.Select(r => r.InvariantViolations))}건").AppendLine();

        if (s.TestFirst is { Ran: > 0 } tf) TestFirstMetrics.AppendSection(sb, tf);

        sb.AppendLine("## 회차별").AppendLine();
        sb.AppendLine("| 회차 | 폴더 | 작업 | 실행 | 성공 | 불변 조건 위반 | 판정 실패(Judge) | 판정 도구 오류(JudgeTooling) | 시간 중위값(초) |");
        sb.AppendLine("| ---: | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var r in s.Runs)
            sb.AppendLine($"| {r.Run} | {r.Folder ?? "—"} | {r.Tasks} | {r.Ran} | {r.Successes} | {r.InvariantViolations} | {r.JudgeFailures} | "
                + $"{r.JudgeTooling} | {Num(r.MedianWallSeconds)} |");
        sb.AppendLine();

        sb.AppendLine("## 범주별 성공 작업 수").AppendLine();
        sb.AppendLine("| 범주 | 작업 | 평균 | 표준편차 | 최소 | 최대 | 회차별 |").AppendLine("| :--- | ---: | ---: | ---: | ---: | ---: | :--- |");
        foreach (var g in s.Groups)
            sb.AppendLine($"| {g.Group} | {g.Tasks} | {Num(g.MeanSuccesses)} | {Num(g.StdDevSuccesses)} | {g.MinSuccesses} | {g.MaxSuccesses} | "
                + $"{string.Join(", ", g.SuccessesPerRun)} |");
        sb.AppendLine();

        var unstable = s.Tasks.Where(t => t.Successes > 0 && t.Successes < t.Runs).ToList();
        var never = s.Tasks.Where(t => t.Successes == 0).ToList();
        sb.AppendLine("## 불안정·실패 작업").AppendLine();
        sb.AppendLine($"* 불안정({unstable.Count}건): {(unstable.Count == 0 ? "없음" : string.Join(", ", unstable.Select(t => $"{t.Id} {t.Ratio}")))}");
        sb.AppendLine($"* 모두 실패({never.Count}건): {(never.Count == 0 ? "없음" : string.Join(", ", never.Select(t => t.Id)))}").AppendLine();

        sb.AppendLine("## 실패 유형 분포 (전 회차 합계)").AppendLine();
        var failures = s.Tasks.SelectMany(t => t.Outcomes.Select(o => (t.Id, Outcome: o)))
            .Where(x => x.Outcome.StartsWith(VerificationResult.Failed, StringComparison.Ordinal))
            .GroupBy(x => x.Outcome, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal).ToList();
        if (failures.Count == 0) sb.AppendLine("실패 없음");
        else
        {
            sb.AppendLine("| 실패 유형 | 건수 | 작업 |").AppendLine("| :--- | ---: | :--- |");
            foreach (var g in failures)
                sb.AppendLine($"| {g.Key} | {g.Count()} | {string.Join(", ", g.Select(x => x.Id).Distinct(StringComparer.Ordinal))} |");
        }
        sb.AppendLine();

        sb.AppendLine("## 작업별").AppendLine();
        sb.AppendLine("| id | 범주 | 성공 | " + string.Join(" | ", s.Runs.Select(r => $"회차 {r.Run}")) + " |");
        sb.AppendLine("| :--- | :--- | :--- | " + string.Join(" | ", s.Runs.Select(_ => ":---")) + " |");
        foreach (var t in s.Tasks)
            sb.AppendLine($"| {t.Id} | {t.Group} | {t.Ratio} | {string.Join(" | ", t.Outcomes)} |");
        return sb.ToString();
    }

    private static string Sampling(PellyCoder.Core.Abstractions.SamplingOptions o) => FormattableString.Invariant(
        $"(T {o.Temperature}, topP {o.TopP}, topK {o.TopK}, repeat {o.RepeatPenalty}, presence {o.PresencePenalty})");

    /// <summary>
    /// Only exit-0 runs whose independent build/compile was judged (Passed or Failed) count; a skipped check (Legacy off
    /// Windows, no SQL server) proves nothing and is shown separately. Behaviour judges do not enter the invariant.
    /// </summary>
    private static string Invariant(IReadOnlyList<TaskResult> exit0)
    {
        if (exit0.Count == 0) return "— (종료 0 작업 없음)";
        var judged = exit0.Where(r => r.Independent?.BuildStatus is VerificationResult.Passed or VerificationResult.Failed).ToList();
        var skipped = exit0.Count - judged.Count;
        if (judged.Count == 0) return $"판정 불가 (종료 0 {exit0.Count}건 모두 독립 검증 생략 {skipped}건)";
        var violations = judged.Count(r => r.InvariantViolated);
        return $"{Pct((double)(judged.Count - violations) / judged.Count)} (위반 {violations}건 / 독립 검증 {judged.Count}건, 독립 검증 생략 {skipped}건)";
    }

    private static string SuccessRates(IReadOnlyList<TaskResult> ran)
    {
        string Rate(string label, IEnumerable<TaskResult> set)
        {
            var list = set.ToList();
            var passed = list.Count(r => r.Status == VerificationResult.Passed);
            return list.Count == 0 ? $"{label} —" : $"{label} {Pct((double)passed / list.Count)} ({passed}/{list.Count})";
        }
        return string.Join(", ", Rate("전체", ran), Rate("cs", ran.Where(r => r.Kind == "cs")),
            Rate("sql", ran.Where(r => r.Kind == "sql")), Rate("legacy", ran.Where(r => r.IsLegacy)));
    }

    private static string MedianP90(IReadOnlyList<double> values, string unit) => values.Count == 0
        ? "— (측정값 없음)"
        : $"중위값 {Num(Percentile(values, 50))}{unit} / P90 {Num(Percentile(values, 90))}{unit} (n={values.Count})";

    private static string Pct(double ratio) => (ratio * 100).ToString("0.0", CultureInfo.InvariantCulture) + "%";

    private static string Num(double? value) => value is { } v ? v.ToString("0.00", CultureInfo.InvariantCulture) : "—";

    /// <summary>The check's status; a passed check with a detail (the judge tests that ran) shows it, a failed one is already in the reasons.</summary>
    private static string IndependentCell(VerificationResult? v) =>
        v is null ? "—" : v.Status == VerificationResult.Passed && v.Detail is { Length: > 0 } d ? $"{v.Status} ({Cell(d)})" : v.Status;

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
