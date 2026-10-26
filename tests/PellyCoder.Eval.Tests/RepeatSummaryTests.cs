using PellyCoder.Core.Abstractions;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

public sealed class RepeatSummaryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pelly-repeat-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void Summary_PerTaskCountsAndCategoryMeans()
    {
        // Three runs over four tasks: cs-legacy-001 3/3, cs-modern-001 2/3, cs-modern-002 0/3 (skipped once), sql-001 1/3.
        IReadOnlyList<TaskResult>[] runs =
        [
            [Pass("cs-legacy-001"), Pass("cs-modern-001"), Fail("cs-modern-002", "Judge"), Pass("sql-001")],
            [Pass("cs-legacy-001"), Pass("cs-modern-001"), Fail("cs-modern-002", "Judge"), Fail("sql-001", "NewErrors")],
            [Pass("cs-legacy-001"), Fail("cs-modern-001", "JudgeTooling"), Skip("cs-modern-002"), Fail("sql-001", "NewErrors")],
        ];

        var s = RepeatSummary.Build(runs);

        Assert.Equal((3, 4), (s.RunCount, s.TaskCount));
        Assert.Equal(["cs-legacy-001", "cs-modern-001", "cs-modern-002", "sql-001"], s.Tasks.Select(t => t.Id));
        Assert.Equal(["3/3", "2/3", "0/3", "1/3"], s.Tasks.Select(t => t.Ratio));
        Assert.Equal(["cs-legacy", "cs-modern", "cs-modern", "sql"], s.Tasks.Select(t => t.Group));
        Assert.Equal(["Passed", "Passed", "Failed(JudgeTooling)"], s.Tasks[1].Outcomes);
        Assert.Equal("Skipped", s.Tasks[2].Outcomes[2]);

        Assert.Equal(["cs-legacy", "cs-modern", "sql"], s.Groups.Select(g => g.Group));
        var modern = s.Groups[1];
        Assert.Equal((2, 1, 0), (modern.Tasks, modern.MaxSuccesses, modern.MinSuccesses));
        Assert.Equal([1, 1, 0], modern.SuccessesPerRun);
        Assert.Equal(2.0 / 3, modern.MeanSuccesses, 6);
        var sql = s.Groups[2];
        Assert.Equal(1.0 / 3, sql.MeanSuccesses, 6);
        Assert.Equal((0, 1), (sql.MinSuccesses, sql.MaxSuccesses));
        Assert.Equal(1.0, s.Groups[0].MeanSuccesses, 6);
        Assert.Equal(0.0, s.Groups[0].StdDevSuccesses, 6);
    }

    [Fact]
    public void Summary_StdDevAndInvariantPerRun()
    {
        IReadOnlyList<TaskResult>[] runs =
        [
            [Pass("cs-modern-001", wall: 1), Pass("cs-modern-002", wall: 3), Pass("sql-001", wall: 5)],
            [Pass("cs-modern-001", wall: 2), Fail("cs-modern-002", "InvariantViolated", invariant: true, wall: 4), Fail("sql-001", "Judge", wall: 6)],
            [Pass("cs-modern-001", wall: 7), Fail("cs-modern-002", "JudgeTooling", wall: 8), Skip("sql-001")],
        ];

        var s = RepeatSummary.Build(runs);

        Assert.Equal([3, 1, 1], s.Runs.Select(r => r.Successes));
        Assert.Equal(5.0 / 3, s.MeanSuccesses, 6);
        // Sample standard deviation (n − 1) of 3, 1, 1: sqrt(((4/3)² + 2·(2/3)²) / 2) = sqrt(4/3).
        Assert.Equal(Math.Sqrt(4.0 / 3), s.StdDevSuccesses, 6);
        Assert.Equal([0, 1, 0], s.Runs.Select(r => r.InvariantViolations));
        Assert.Equal([0, 1, 0], s.Runs.Select(r => r.JudgeFailures));
        Assert.Equal([0, 0, 1], s.Runs.Select(r => r.JudgeTooling));
        Assert.Equal([3, 3, 2], s.Runs.Select(r => r.Ran));
        Assert.Equal([1, 2, 3], s.Runs.Select(r => r.Run));
        // Nearest-rank medians: per run {1,3,5} → 3, {2,4,6} → 4, {7,8} → 7; all eight values → 4.
        Assert.Equal([3.0, 4.0, 7.0], s.Runs.Select(r => r.MedianWallSeconds!.Value));
        Assert.Equal(4.0, s.MedianWallSeconds!.Value, 6);
        Assert.Equal(0.0, RepeatSummary.Build([runs[0]]).StdDevSuccesses);
    }

    [Fact]
    public void Summary_V1File_ReadsWithNullModelIdentity()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "docs", "m3b-baseline.summary.json"))) dir = dir.Parent;
        Assert.NotNull(dir);

        var s = RepeatSummary.FromJson(File.ReadAllText(Path.Combine(dir.FullName, "docs", "m3b-baseline.summary.json")));

        Assert.Equal(1, s.FormatVersion);
        Assert.Null(s.Conditions!.OllamaVersion);
        Assert.Null(s.Conditions.ModelDigest);
    }

    [Fact]
    public void Summary_WritesFormatVersion2_WithModelIdentity()
    {
        var conditions = new RunConditions("qwen", "cs-v2", "sql-v1", "Retry", 512,
            new SamplingOptions(0.6, 0.95, 20, 1.0, 0.0), new SamplingOptions(0.1, 0.9, 20, 1.05, 0.0), "0.9.0", null, null,
            "0.40.0", new string('c', 64));
        var data = RepeatSummary.Build([[Pass("cs-modern-001")]]) with { Conditions = conditions };

        var json = RepeatSummary.ToJson(data);

        Assert.Equal(2, RepeatSummaryData.CurrentFormatVersion);
        Assert.Contains("\"formatVersion\": 2", json, StringComparison.Ordinal);
        var back = RepeatSummary.FromJson(json);
        Assert.Equal("0.40.0", back.Conditions!.OllamaVersion);
        Assert.Equal(new string('c', 64), back.Conditions.ModelDigest);
    }

    [Fact]
    public async Task Repeat_WritesRunFolders()
    {
        var conditions = new RunConditions("qwen", "cs-v2", "sql-v1", "Retry", 512,
            new SamplingOptions(0.6, 0.95, 20, 1.0, 0.0), new SamplingOptions(0.1, 0.9, 20, 1.05, 0.0), "0.9.0", null, null);
        var seen = new List<string>();
        Task<RepeatedRun.RunOnce> RunOnce(string dir, int run)
        {
            seen.Add(dir);
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "results.json"), "[]");
            IReadOnlyList<TaskResult> results = [run == 2 ? Fail("cs-modern-001", "Judge") : Pass("cs-modern-001")];
            return Task.FromResult(new RepeatedRun.RunOnce(results, Cancelled: false));
        }

        var outcome = await RepeatedRun.ExecuteAsync(3, _dir, RunOnce, conditions, "헤더");

        Assert.Equal(["run-1", "run-2", "run-3"], seen.Select(d => Path.GetRelativePath(_dir, d)));
        Assert.All(seen, d => Assert.True(File.Exists(Path.Combine(d, "results.json"))));
        Assert.False(outcome.Cancelled);
        var summary = RepeatSummary.FromJson(File.ReadAllText(Path.Combine(_dir, "summary.json")));
        Assert.Equal(["run-1", "run-2", "run-3"], summary.Runs.Select(r => r.Folder));
        Assert.Equal("2/3", summary.Tasks.Single().Ratio);
        Assert.Equal(conditions, summary.Conditions);
        var md = File.ReadAllText(Path.Combine(_dir, "summary.md"));
        Assert.Contains("| cs-modern-001 | cs-modern | 2/3 |", md, StringComparison.Ordinal);
        Assert.Contains("헤더", md, StringComparison.Ordinal);

        // --repeat 1 keeps the single-run layout: results straight in the output folder, no summary files.
        var single = Path.Combine(_dir, "single");
        seen.Clear();
        await RepeatedRun.ExecuteAsync(1, single, RunOnce, conditions, "헤더");
        Assert.Equal([single], seen);
        Assert.False(File.Exists(Path.Combine(single, "summary.json")));
        Assert.False(File.Exists(Path.Combine(single, "summary.md")));
    }

    [Fact]
    public async Task Repeat_StopsAfterACancelledRun()
    {
        var calls = 0;
        Task<RepeatedRun.RunOnce> RunOnce(string dir, int run)
        {
            calls++;
            return Task.FromResult(new RepeatedRun.RunOnce([Pass("cs-modern-001")], Cancelled: run == 2));
        }

        var outcome = await RepeatedRun.ExecuteAsync(3, _dir, RunOnce, null, "헤더");

        Assert.Equal(2, calls);
        Assert.True(outcome.Cancelled);
        var summary = RepeatSummary.FromJson(File.ReadAllText(Path.Combine(_dir, "summary.json")));
        Assert.True(summary.Cancelled);
        Assert.Equal(1, summary.RunCount); // only completed runs are summarised
    }

    [Fact]
    public void Arguments_Repeat_DefaultsToOneAndRejectsNonPositive()
    {
        Assert.Equal(1, EvalArguments.Parse([]).Repeat);
        Assert.Equal(3, EvalArguments.Parse(["--repeat", "3", "--tasks", "sql"]).Repeat);
        Assert.NotNull(EvalArguments.Parse(["--repeat", "0"]).Error);
        Assert.NotNull(EvalArguments.Parse(["--repeat", "x"]).Error);
        Assert.NotNull(EvalArguments.Parse(["--repeat"]).Error);
    }

    [Fact]
    public void Summary_TestFirstAggregated_AcrossRuns()
    {
        TaskResult Tf(string id, string first) => TestFirstMetricsTests.Row(id, VerificationResult.Passed, 0, new("Red", "Passed", first, 0, 0));
        var data = RepeatSummary.Build([[Tf("cs-modern-001", "Failed")], [Tf("cs-modern-001", "Passed")]]);
        Assert.Equal(new TestFirstMetricsData(2, 2, 2, 0, 2, 1, 1, 0), data.TestFirst);
        var md = KpiReport.RenderRepeat(data, "h");
        Assert.Contains("| 수용 기준 충족률 | 100.0% (2/2) |", md, StringComparison.Ordinal);
        Assert.Equal(data.TestFirst, RepeatSummary.FromJson(RepeatSummary.ToJson(data)).TestFirst);
    }

    [Fact]
    public void Summary_TestFirst_ListsPassedRowsForHumanSampleReview()
    {
        TaskResult Tf(string id, string status) => TestFirstMetricsTests.Row(id, status, status == VerificationResult.Passed ? 0 : 1, new("Red", "Passed", "Passed", 0, 0));
        var data = RepeatSummary.Build([
            [Tf("cs-modern-001", VerificationResult.Passed), Tf("cs-modern-002", VerificationResult.Failed)],
            [Tf("cs-modern-001", VerificationResult.Passed), Tf("cs-modern-002", VerificationResult.Passed)],
        ]);
        var md = KpiReport.RenderRepeat(data, "h");
        Assert.Contains("## 사람 표본 검토 대상", md, StringComparison.Ordinal);
        var lines = md.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        Assert.Contains("* cs-modern-001: 회차 1, 2", lines);
        Assert.Contains("* cs-modern-002: 회차 2", lines);

        var plain = RepeatSummary.Build([[Pass("cs-modern-001")]]);
        Assert.DoesNotContain("사람 표본 검토 대상", KpiReport.RenderRepeat(plain, "h"), StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_OldJsonWithoutTestFirst_Loads()
    {
        var data = RepeatSummary.Build([[Pass("cs-modern-001")]]);
        var json = RepeatSummary.ToJson(data);
        Assert.DoesNotContain("testFirst", json, StringComparison.Ordinal);
        Assert.Null(RepeatSummary.FromJson(json).TestFirst);
        Assert.DoesNotContain("수용 기준 충족률", KpiReport.RenderRepeat(data, "h"), StringComparison.Ordinal);
    }

    private static TaskResult Pass(string id, double wall = 1) => Result(id, VerificationResult.Passed, null, false, wall);

    private static TaskResult Fail(string id, string failure, bool invariant = false, double wall = 1) =>
        Result(id, VerificationResult.Failed, failure, invariant, wall);

    private static TaskResult Skip(string id) =>
        TaskResult.Skipped(new BenchmarkTask { Id = id, Kind = id.StartsWith("sql", StringComparison.Ordinal) ? "sql" : "cs" }, false, "건너뜀");

    private static TaskResult Result(string id, string status, string? failure, bool invariant, double wall) =>
        new(id, id.StartsWith("sql", StringComparison.Ordinal) ? "sql" : "cs", status, 0, 1, [], VerificationResult.Pass,
            VerificationResult.Passed, invariant, failure, wall, 0.3, 100, false, ["Ok"], null,
            id.StartsWith("cs-legacy-", StringComparison.Ordinal));
}
