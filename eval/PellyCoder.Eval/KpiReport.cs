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
        sb.AppendLine($"| 최종 작업 성공률 | ≥ 85% | {SuccessRates(ran)} |");
        sb.AppendLine($"| 단일 작업 완료 시간 | 중위값 < 15초 / P90 < 30초 | {MedianP90(wall, "초")} |");
        sb.AppendLine();

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
                + $"{r.Independent?.Status ?? "—"} | {Num(r.WallSeconds)} | {Num(r.TtftSeconds)} | {Cell(r.SkipReason ?? string.Join("; ", r.Reasons))} |");
        return sb.ToString();
    }

    /// <summary>
    /// Only exit-0 runs the independent check actually judged (Passed or Failed) count; a skipped check (Legacy off
    /// Windows, no SQL server) proves nothing and is shown separately.
    /// </summary>
    private static string Invariant(IReadOnlyList<TaskResult> exit0)
    {
        if (exit0.Count == 0) return "— (종료 0 작업 없음)";
        var judged = exit0.Where(r => r.Independent?.Status is VerificationResult.Passed or VerificationResult.Failed).ToList();
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

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal);
}
