using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

public class KpiReportTests
{
    [Fact]
    public void Kpi_Percentiles_NearestRank()
    {
        double[] values = [10, 3, 7, 1, 9, 2, 8, 4, 6, 5]; // 1..10, unsorted

        Assert.Equal(5, KpiReport.Percentile(values, 50));
        Assert.Equal(9, KpiReport.Percentile(values, 90));
        Assert.Equal(10, KpiReport.Percentile(values, 100));
        Assert.Equal(7, KpiReport.Percentile([7], 90));
    }

    [Fact]
    public void Kpi_ParseSuccess_FromEvents()
    {
        var lines = new List<string>();
        for (var i = 1; i <= 9; i++)
        {
            lines.Add($$"""{"ts":"2026-10-18T00:00:00+00:00","phase":"Edit","outcome":"Ok","attempt":{{i}},"elapsedMs":10}""");
            lines.Add($$"""{"ts":"2026-10-18T00:00:00+00:00","phase":"Apply","outcome":"MatchFailed","attempt":{{i}},"elapsedMs":1}""");
        }
        lines.Add("""{"ts":"2026-10-18T00:00:00+00:00","phase":"Edit","outcome":"FormatError","attempt":10,"elapsedMs":10}""");
        lines.Add("");

        var outcomes = RunArtifacts.ParseEditOutcomes(lines);

        Assert.Equal(10, outcomes.Count);
        Assert.Equal(0.9, KpiReport.ParseSuccessRate(outcomes)!.Value, 6);
        Assert.Null(KpiReport.ParseSuccessRate([]));
    }

    [Fact]
    public void Kpi_Report_ListsAllPrdRows()
    {
        TaskResult[] results =
        [
            Result("cs-modern-001", "cs", "Passed", 0, ttft: 0.8, wall: 12),
            Result("cs-legacy-001", "cs", "Failed", 1, ttft: 1.2, wall: 40, failure: "NewErrors"),
            Result("sql-001", "sql", "Passed", 0, ttft: 0.5, wall: 8),
        ];

        var report = KpiReport.Render(results, "테스트");

        string[] rows = ["검증 불변 조건", "초기 응답 지연 (TTFT)", "추론 예산 제어 (Thinking)", "패치 포맷 파싱 성공률", "최종 작업 성공률", "단일 작업 완료 시간"];
        var positions = rows.Select(r => report.IndexOf("| " + r, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0, report));
        Assert.Equal(positions.Order().ToList(), positions); // PRD §2.2 order
        Assert.Contains("NewErrors", report, StringComparison.Ordinal);
        Assert.Contains("Legacy 빌드 판정 생략", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Kpi_Report_CountsInvariantViolationsAndSkips()
    {
        TaskResult[] results =
        [
            Result("cs-modern-001", "cs", "Failed", 0, 1, 10, failure: "Judge", invariant: true),
            Result("cs-modern-002", "cs", "Passed", 0, 1, 10),
            TaskResult.Skipped(new BenchmarkTask { Id = "sql-001", Kind = "sql" }, "PELLY_SQL_CONNECTION이 없습니다"),
        ];

        var report = KpiReport.Render(results, "테스트");

        Assert.Contains("50.0% (위반 1건 / 종료 0 2건)", report, StringComparison.Ordinal);
        Assert.Contains("PELLY_SQL_CONNECTION이 없습니다", report, StringComparison.Ordinal);
    }

    private static TaskResult Result(string id, string kind, string status, int exit, double ttft, double wall,
        string? failure = null, bool invariant = false) =>
        new(id, kind, status, exit, 1, [], VerificationResult.Pass, VerificationResult.Passed, invariant, failure,
            wall, ttft, 100, false, ["Ok"], null);
}
