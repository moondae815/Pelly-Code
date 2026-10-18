using System.Text.Json;
using PellyCoder.Core.Abstractions;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

public sealed class AcceptanceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pelly-accept-" + Guid.NewGuid().ToString("N"));

    public AcceptanceTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    // Baseline shaped like docs/m3b-baseline.md: legacy 18, modern 10/9/10, sql 20 → mean 47.67, median 1.90 s.
    private static RepeatSummaryData Baseline() => Make([18, 18, 18], [10, 9, 10], [20, 20, 20]);

    [Fact]
    public void Acceptance_PlusOneOrMore_NoDrop_Accepted()
    {
        // modern 11/10/11 → mean 48.67 (+1.0 exactly: 146/3 − 143/3 must not round below 1.0).
        var r = Acceptance.Compare(Baseline(), Make([18, 18, 18], [11, 10, 11], [20, 20, 20]));

        Assert.True(r.Accepted, string.Join("\n", r.Reasons));
    }

    [Fact]
    public void Acceptance_WithinNoise_IsRejected()
    {
        // modern 10/10/11 → +0.67.
        var r = Acceptance.Compare(Baseline(), Make([18, 18, 18], [10, 10, 11], [20, 20, 20]));

        Assert.False(r.Accepted);
        Assert.Contains(r.Reasons, x => x.StartsWith("✗ 규칙 1", StringComparison.Ordinal) && x.Contains("+0.67", StringComparison.Ordinal));
    }

    [Fact]
    public void Acceptance_CategoryDrop_IsRejected()
    {
        // modern 12/12/12 (+2.33), sql 19/20/19 (−0.67) → overall +1.67 but a category falls more than 0.5.
        var r = Acceptance.Compare(Baseline(), Make([18, 18, 18], [12, 12, 12], [19, 20, 19]));

        Assert.False(r.Accepted);
        Assert.Contains(r.Reasons, x => x.StartsWith("✓ 규칙 1", StringComparison.Ordinal));
        Assert.Contains(r.Reasons, x => x.StartsWith("✗ 규칙 2", StringComparison.Ordinal) && x.Contains("sql", StringComparison.Ordinal) && x.Contains("−0.67", StringComparison.Ordinal));
    }

    [Fact]
    public void Acceptance_CategoryDropOfExactlyHalf_IsAllowed()
    {
        // A drop of 0.5 is not "more than 0.5": sql 19.5 is impossible with 3 runs, so use two runs.
        var baseline = Make([18, 18], [10, 9], [20, 20]);
        var r = Acceptance.Compare(baseline, Make([18, 18], [12, 11], [20, 19]));

        Assert.True(r.Accepted, string.Join("\n", r.Reasons));
    }

    [Fact]
    public void Acceptance_InvariantViolation_IsRejected()
    {
        var r = Acceptance.Compare(Baseline(), Make([18, 18, 18], [12, 12, 12], [20, 20, 20], invariants: [0, 1, 0]));

        Assert.False(r.Accepted);
        Assert.Contains(r.Reasons, x => x.StartsWith("✗ 규칙 3", StringComparison.Ordinal) && x.Contains("회차 2", StringComparison.Ordinal));
    }

    [Fact]
    public void Acceptance_TooSlow_IsRejected()
    {
        // 1.5 × 1.90 = 2.85: 2.85 passes, 2.86 fails.
        var good = Make([18, 18, 18], [12, 12, 12], [20, 20, 20]);
        Assert.True(Acceptance.Compare(Baseline(), good with { MedianWallSeconds = 2.85 }).Accepted);

        var r = Acceptance.Compare(Baseline(), good with { MedianWallSeconds = 2.86 });
        Assert.False(r.Accepted);
        Assert.Contains(r.Reasons, x => x.StartsWith("✗ 규칙 4", StringComparison.Ordinal));
    }

    [Fact]
    public void Comparable_RefusesCancelledMissingAndDifferentRuns()
    {
        var b = Baseline();
        Assert.Empty(Acceptance.Comparability(b, Make([18, 18, 18], [12, 12, 12], [20, 20, 20])));

        Assert.Contains(Acceptance.Comparability(b, b with { Cancelled = true }), x => x.Contains("중단", StringComparison.Ordinal));
        Assert.Contains(Acceptance.Comparability(b, b with { Conditions = null }), x => x.Contains("conditions", StringComparison.Ordinal));
        Assert.Contains(Acceptance.Comparability(b with { Runs = [] }, b), x => x.Contains("runs", StringComparison.Ordinal));
        Assert.Contains(Acceptance.Comparability(b, b with { Groups = [] }), x => x.Contains("groups", StringComparison.Ordinal));
        Assert.Contains(Acceptance.Comparability(b, b with { Tasks = b.Tasks.Skip(1).ToList() }), x => x.Contains("cs-legacy-001", StringComparison.Ordinal));
        var fewerRan = b with { Runs = b.Runs.Select(r => r.Run == 2 ? r with { Ran = r.Ran - 1 } : r).ToList() };
        Assert.Contains(Acceptance.Comparability(b, fewerRan), x => x.Contains("회차 2", StringComparison.Ordinal));
        Assert.Contains(Acceptance.Comparability(b, Make([18, 18], [10, 10], [20, 20])), x => x.Contains("회차 수", StringComparison.Ordinal));
    }

    [Fact]
    public void Conditions_DifferencesAreListedSideBySide()
    {
        var b = Baseline();
        var c = b with { Conditions = b.Conditions! with { BudgetThink = 768 } };

        var diff = Acceptance.ConditionDifferences(b.Conditions!, c.Conditions!);

        Assert.Equal([("budgetThink", "512", "768")], diff);
    }

    [Fact]
    public void Json_RoundTripsAndRejectsOldOrIncompleteSummaries()
    {
        var b = Baseline();
        var json = RepeatSummary.ToJson(b);
        Assert.Contains("\"formatVersion\": 1", json, StringComparison.Ordinal);
        Assert.Equal(b.MeanSuccesses, RepeatSummary.FromJson(json).MeanSuccesses);

        // Task 5 summaries had no formatVersion.
        Assert.Throws<JsonException>(() => RepeatSummary.FromJson(json.Replace("\"formatVersion\": 1,", "", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => RepeatSummary.FromJson(json.Replace("\"formatVersion\": 1", "\"formatVersion\": 2", StringComparison.Ordinal)));
        // A required field missing (meanSuccesses) is an error, not 0.
        Assert.Throws<JsonException>(() => RepeatSummary.FromJson(Remove(json, "meanSuccesses")));
        Assert.Throws<JsonException>(() => RepeatSummary.FromJson(Remove(json, "groups")));
    }

    [Fact]
    public void Command_PrintsVerdictAndExitCodes()
    {
        var basePath = Write("base.json", Baseline());
        var good = Make([18, 18, 18], [11, 10, 11], [20, 20, 20]);
        var goodPath = Write("good.json", good with { Conditions = good.Conditions! with { BudgetThink = 768 } });
        var noisePath = Write("noise.json", Make([18, 18, 18], [10, 10, 11], [20, 20, 20]));
        var cancelledPath = Write("cancelled.json", Baseline() with { Cancelled = true });
        var oldPath = Path.Combine(_dir, "old.json");
        File.WriteAllText(oldPath, RepeatSummary.ToJson(Baseline()).Replace("\"formatVersion\": 1,", "", StringComparison.Ordinal));

        var (code, output) = Run(basePath, goodPath);
        Assert.Equal(0, code);
        Assert.Contains("채택", output, StringComparison.Ordinal);
        Assert.Contains("budgetThink", output, StringComparison.Ordinal);
        Assert.Contains("512", output, StringComparison.Ordinal);
        Assert.Contains("768", output, StringComparison.Ordinal);

        (code, output) = Run(basePath, noisePath);
        Assert.Equal(3, code);
        Assert.Contains("거절", output, StringComparison.Ordinal);

        (code, output) = Run(basePath, cancelledPath);
        Assert.Equal(4, code);
        Assert.Contains("판정하지 않습니다", output, StringComparison.Ordinal);
        Assert.DoesNotContain("채택", output, StringComparison.Ordinal);

        (code, output) = Run(basePath, oldPath);
        Assert.Equal(4, code);
        Assert.Contains("old.json", output, StringComparison.Ordinal);

        (code, _) = Run(basePath);
        Assert.Equal(4, code);
    }

    private (int Code, string Output) Run(params string[] paths)
    {
        var output = new StringWriter();
        var code = CompareCommand.Run(paths, output);
        return (code, output.ToString());
    }

    private string Write(string name, RepeatSummaryData data)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, RepeatSummary.ToJson(data));
        return path;
    }

    private static string Remove(string json, string property)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json)!.AsObject();
        Assert.True(node.Remove(property));
        return node.ToJsonString();
    }

    /// <summary>A summary with 18 legacy, 12 modern and 20 sql tasks; successes per run per group as given.</summary>
    private static RepeatSummaryData Make(int[] legacy, int[] modern, int[] sql, int[]? invariants = null, double median = 1.90)
    {
        var runCount = legacy.Length;
        var groups = new (string Name, int Tasks, int[] PerRun)[] { ("cs-legacy", 18, legacy), ("cs-modern", 12, modern), ("sql", 20, sql) };
        var tasks = new List<TaskRepeatSummary>();
        foreach (var (name, count, perRun) in groups)
        {
            for (var i = 1; i <= count; i++)
            {
                // Task i passes in run k when i ≤ perRun[k].
                var outcomes = perRun.Select(p => i <= p ? VerificationResult.Passed : "Failed(Judge)").ToList();
                tasks.Add(new TaskRepeatSummary($"{name}-{i:000}", name, outcomes.Count(o => o == VerificationResult.Passed), runCount, outcomes));
            }
        }
        var perRunTotal = Enumerable.Range(0, runCount).Select(k => legacy[k] + modern[k] + sql[k]).ToList();
        var runs = perRunTotal.Select((s, k) => new RunRepeatSummary(k + 1, 50, 50, s, invariants?[k] ?? 0, 50 - s, 0, median, $"run-{k + 1}")).ToList();
        var groupSummaries = groups.Select(g => new GroupRepeatSummary(g.Name, g.Tasks, g.PerRun.Average(), 0, g.PerRun.Min(), g.PerRun.Max(), g.PerRun)).ToList();
        var conditions = new RunConditions("qwen3.6:35b-a3b-coding-nvfp4", "cs-v2", "sql-v1", "Retry", 512,
            new SamplingOptions(0.6, 0.95, 20, 1.0, 0.0), new SamplingOptions(0.1, 0.9, 20, 1.05, 0.0), "0.1.0", null, null);
        return new RepeatSummaryData(runCount, 50, perRunTotal.Average(), 0, median, runs, groupSummaries, tasks, conditions);
    }
}
