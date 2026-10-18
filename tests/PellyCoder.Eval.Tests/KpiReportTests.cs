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
    public void Kpi_ParseSuccess_FromSummaries()
    {
        // Ten EDIT attempts, one of them a FormatError (summary.json: attempts 10 / formatErrors 1).
        (int Attempts, int FormatErrors)[] runs = [(4, 0), (6, 1)];

        Assert.Equal(0.9, KpiReport.ParseSuccessRate(runs)!.Value, 6);
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

        Assert.Contains("50.0% (위반 1건 / 독립 검증 2건, 독립 검증 생략 0건)", report, StringComparison.Ordinal);
        Assert.Contains("PELLY_SQL_CONNECTION이 없습니다", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Kpi_Invariant_SkippedIndependentChecksAreNotCounted()
    {
        var legacySkip = VerificationResult.Skip(VerificationResult.LegacyBuildSkipped);
        TaskResult[] onlySkipped =
        [
            Result("cs-legacy-001", "cs", "Passed", 0, 1, 10, independent: legacySkip),
            Result("cs-legacy-002", "cs", "Passed", 0, 1, 10, independent: legacySkip),
        ];
        var none = KpiReport.Render(onlySkipped, "테스트");
        Assert.Contains("| 검증 불변 조건 | 100% | 판정 불가 (종료 0 2건 모두 독립 검증 생략 2건) |", none, StringComparison.Ordinal);

        TaskResult[] mixed =
        [
            Result("cs-legacy-001", "cs", "Passed", 0, 1, 10, independent: legacySkip),
            Result("cs-modern-001", "cs", "Passed", 0, 1, 10),
            Result("cs-modern-002", "cs", "Failed", 0, 1, 10, failure: "InvariantViolated", invariant: true,
                independent: VerificationResult.Fail("CS0103")),
        ];
        var some = KpiReport.Render(mixed, "테스트");
        Assert.Contains("| 검증 불변 조건 | 100% | 50.0% (위반 1건 / 독립 검증 2건, 독립 검증 생략 1건) |", some, StringComparison.Ordinal);
    }

    [Fact]
    public void Kpi_Ttft_ExcludesRunsWithoutGeneration()
    {
        TaskResult[] results =
        [
            Result("cs-modern-001", "cs", "Passed", 0, 1.0, 10),
            Result("cs-modern-002", "cs", "Passed", 0, 3.0, 10),
            Result("cs-modern-003", "cs", "Failed", 4, 0, 1, failure: "EnvError"),
            Result("cs-modern-004", "cs", "Failed", 3, 1, 1, failure: "NotFound") with { TtftSeconds = null },
        ];

        var report = KpiReport.Render(results, "테스트");

        Assert.Contains("| 초기 응답 지연 (TTFT) | < 1.0초 | 중위값 1.00초 / P90 3.00초 (n=2) |", report, StringComparison.Ordinal);
    }

    [Fact]
    public void Kpi_Report_LabelsEnvironmentSkips()
    {
        var task = new BenchmarkTask { Id = "cs-modern-001", Kind = "cs", File = "corpus/Modern/x.cs" };
        TaskResult[] results = [TaskResult.EnvironmentSkipped(task, "복원 실패", "dotnet restore 종료 1")];

        var report = KpiReport.Render(results, "테스트");

        Assert.Contains("건너뜀 1건 (환경 오류 1건)", report, StringComparison.Ordinal);
        Assert.Contains("* cs-modern-001: 환경 오류(복원 실패): dotnet restore 종료 1", report, StringComparison.Ordinal);
    }

    [Fact]
    public void TaskResult_FactoriesAndLegacyFollowTheCorpusPath()
    {
        var legacy = new BenchmarkTask { Id = "cs-legacy-001", Kind = "cs", File = "corpus/Legacy/Shop.Web/Services/OrderService.cs" };
        var modern = new BenchmarkTask { Id = "cs-legacy-099", Kind = "cs", File = "corpus/Modern/Shop.Api/Services/OrderService.cs" };

        Assert.True(legacy.IsLegacy);
        Assert.False(modern.IsLegacy); // the corpus path decides, not the id
        var failed = TaskResult.Failed(legacy, "실행기 오류: x", "RunnerError");
        Assert.Equal((VerificationResult.Failed, "RunnerError", true, (string?)null), (failed.Status, failed.FailureType, failed.IsLegacy, failed.SkipReason));
        Assert.Equal(["실행기 오류: x"], failed.Reasons);
        Assert.False(TaskResult.Skipped(modern, "r").IsLegacy);
    }

    [Fact]
    public void Kpi_Report_ShowsJudgeTestDetailOfAPassedCheck()
    {
        var judged = new VerificationResult(VerificationResult.Passed, "판정 테스트 통과 1건, 수정 전 동작 테스트 실패 확인 1건");

        var report = KpiReport.Render([Result("cs-modern-001", "cs", "Passed", 0, ttft: 0.8, wall: 12, independent: judged)], "테스트");

        Assert.Contains("| Passed (판정 테스트 통과 1건, 수정 전 동작 테스트 실패 확인 1건) |", report, StringComparison.Ordinal);
    }

    private static TaskResult Result(string id, string kind, string status, int exit, double ttft, double wall,
        string? failure = null, bool invariant = false, VerificationResult? independent = null) =>
        new(id, kind, status, exit, 1, [], independent ?? VerificationResult.Pass, (independent ?? VerificationResult.Pass).Status,
            invariant, failure, wall, ttft, 100, false, ["Ok"], null, id.StartsWith("cs-legacy-", StringComparison.Ordinal));
}
