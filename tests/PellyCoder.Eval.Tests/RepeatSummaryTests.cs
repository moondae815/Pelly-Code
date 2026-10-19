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
