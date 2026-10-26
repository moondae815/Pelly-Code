using System.Globalization;
using System.Text;
using PellyCoder.Core.Reporting;

namespace PellyCoder.Eval;

/// <summary>
/// Test-first counts over rows that carry a <see cref="TestFirstResult"/> (a repeated run sums its runs).
/// </summary>
/// <param name="Ran">Rows with test-first data.</param>
/// <param name="Met">Judged rows with exit 0, GREEN passed and no regressions (the <c>pelly report</c> rule).</param>
/// <param name="Judged">Rows whose RED was confirmed and whose run did not end with exit 2.</param>
/// <param name="RedWarned">Rows whose test already passed before the change.</param>
/// <param name="RedChecked">Rows whose RED check ran (<c>Red</c> or <c>AlreadyPassed</c>).</param>
/// <param name="BlockedWrongPatches">Rows whose first attempt failed GREEN: a patch v1 would have accepted.</param>
/// <param name="FixedByFeedback">Of those, rows that still passed (exit 0 and the independent verdict passed).</param>
public sealed record TestFirstMetricsData(
    int Ran, int Met, int Judged, int RedWarned, int RedChecked, int BlockedWrongPatches, int FixedByFeedback, int BreaksExcluded)
{
    public const string Notice = "판정 테스트가 GREEN 판정과 독립 판정에 함께 쓰입니다. 차이에는 GREEN 피드백과 회귀 확인 효과가 섞여 있습니다.";

    public static TestFirstMetricsData operator +(TestFirstMetricsData a, TestFirstMetricsData b) => new(
        a.Ran + b.Ran, a.Met + b.Met, a.Judged + b.Judged, a.RedWarned + b.RedWarned, a.RedChecked + b.RedChecked,
        a.BlockedWrongPatches + b.BlockedWrongPatches, a.FixedByFeedback + b.FixedByFeedback, a.BreaksExcluded + b.BreaksExcluded);
}

public static class TestFirstMetrics
{
    public static TestFirstMetricsData Build(IReadOnlyList<TaskResult> results)
    {
        int ran = 0, met = 0, judged = 0, warned = 0, redChecked = 0, blocked = 0, fixedBy = 0, breaks = 0;
        foreach (var r in results)
        {
            if (r.TestFirst is not { } t) continue;
            ran++;
            breaks += t.BreaksExcluded;
            if (t.Red is "Red" or "AlreadyPassed") redChecked++;
            if (t.Red == "AlreadyPassed") warned++;
            if (t.Red == "Red" && r.ExitCode != 2)
            {
                judged++;
                if (r.ExitCode == 0 && t.Green == "Passed" && t.Regressions == 0) met++;
            }
            if (t.FirstAttemptGreen == "Failed")
            {
                blocked++;
                if (r.Status == VerificationResult.Passed) fixedBy++;
            }
        }
        return new TestFirstMetricsData(ran, met, judged, warned, redChecked, blocked, fixedBy, breaks);
    }

    /// <summary>The "## 테스트 우선 지표" section shared by report.md and summary.md.</summary>
    public static void AppendSection(StringBuilder sb, TestFirstMetricsData m)
    {
        sb.AppendLine("## 테스트 우선 지표").AppendLine();
        sb.AppendLine("| 지표 항목 | 측정값 |").AppendLine("| :--- | :--- |");
        sb.AppendLine($"| 수용 기준 충족률 | {Rate(m.Met, m.Judged)} |");
        sb.AppendLine($"| RED 확인 경고율 | {Rate(m.RedWarned, m.RedChecked)} |");
        sb.AppendLine($"| 막은 잘못된 패치 | {m.BlockedWrongPatches}건 |");
        sb.AppendLine($"| 피드백으로 고친 비율 | {m.FixedByFeedback}/{m.BlockedWrongPatches} |");
        sb.AppendLine().AppendLine(TestFirstMetricsData.Notice).AppendLine();
    }

    /// <summary>
    /// The "## 사람 표본 검토 대상" list: test-first rows that passed (task id, and the runs when repeated), from which a person
    /// samples diffs to review (design V2-2c).
    /// </summary>
    public static void AppendSampleReview(StringBuilder sb, IEnumerable<(string Id, IReadOnlyList<int>? Runs)> passed)
    {
        sb.AppendLine("## 사람 표본 검토 대상").AppendLine();
        sb.AppendLine("테스트 우선으로 통과한 작업입니다. 사람이 여기서 표본을 골라 변경을 검토합니다.").AppendLine();
        var any = false;
        foreach (var (id, runs) in passed)
        {
            any = true;
            sb.AppendLine(runs is null ? $"* {id}" : $"* {id}: 회차 {string.Join(", ", runs)}");
        }
        if (!any) sb.AppendLine("없음");
        sb.AppendLine();
    }

    public static string Rate(int part, int whole) => whole == 0
        ? "— (대상 없음)"
        : $"{(100.0 * part / whole).ToString("0.0", CultureInfo.InvariantCulture)}% ({part}/{whole})";
}
