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
        Assert.Contains("\"formatVersion\": 2", json, StringComparison.Ordinal);
        Assert.Equal(b.MeanSuccesses, RepeatSummary.FromJson(json).MeanSuccesses);

        // Task 5 summaries had no formatVersion.
        Assert.Throws<JsonException>(() => RepeatSummary.FromJson(json.Replace("\"formatVersion\": 2,", "", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => RepeatSummary.FromJson(json.Replace("\"formatVersion\": 2", "\"formatVersion\": 3", StringComparison.Ordinal)));
        Assert.Throws<JsonException>(() => RepeatSummary.FromJson(json.Replace("\"formatVersion\": 2", "\"formatVersion\": 0", StringComparison.Ordinal)));
        Assert.Equal(1, RepeatSummary.FromJson(json.Replace("\"formatVersion\": 2", "\"formatVersion\": 1", StringComparison.Ordinal)).FormatVersion);
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
        File.WriteAllText(oldPath, RepeatSummary.ToJson(Baseline()).Replace("\"formatVersion\": 2,", "", StringComparison.Ordinal));

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

    private static RepeatSummaryData WithIdentity(RepeatSummaryData d, string? ollama, string? digest) =>
        d with { Conditions = d.Conditions! with { OllamaVersion = ollama, ModelDigest = digest } };

    [Fact]
    public void Comparability_DifferentModelDigest_IsRefused()
    {
        var b = WithIdentity(Baseline(), "0.40.0", "cd2692a833e6" + new string('0', 52));
        var c = WithIdentity(Baseline(), "0.40.0", "ffffffffffff" + new string('1', 52));

        var problems = Acceptance.Comparability(b, c);

        Assert.Contains("모델 digest가 다릅니다 (cd2692a833e6 ≠ ffffffffffff)", problems);
        var (code, _) = Run(Write("b.json", b), Write("c.json", c));
        Assert.Equal(4, code);
    }

    [Fact]
    public void Comparability_DifferentOllamaVersion_IsRefused()
    {
        var d = new string('a', 64);
        var problems = Acceptance.Comparability(WithIdentity(Baseline(), "0.40.0", d), WithIdentity(Baseline(), "0.41.2", d));

        Assert.Contains(problems, p => p.Contains("Ollama 버전이 다릅니다 (0.40.0 ≠ 0.41.2)", StringComparison.Ordinal));
        var (code, _) = Run(Write("b.json", WithIdentity(Baseline(), "0.40.0", d)), Write("c.json", WithIdentity(Baseline(), "0.41.2", d)));
        Assert.Equal(4, code);
    }

    [Fact]
    public void Comparability_OneSideNullIdentity_IsNotRefused_ButShownInConditionDifferences()
    {
        var b = WithIdentity(Baseline(), null, null);
        var c = WithIdentity(Baseline(), "0.40.0", new string('a', 64));

        Assert.Empty(Acceptance.Comparability(b, c));
        var fields = Acceptance.ConditionDifferences(b.Conditions!, c.Conditions!).Select(d => d.Field).ToList();
        Assert.Contains("ollamaVersion", fields);
        Assert.Contains("modelDigest", fields);
        var (code, output) = Run(Write("b.json", b), Write("c.json", c));
        Assert.Equal(3, code); // identical results: rule 1 (+1.0 mean gain) fails, so a verdict is reached
        Assert.Contains("modelDigest", output, StringComparison.Ordinal);
    }

    private static RunConditions TfConditions(bool? testFirst, string? digest = null) =>
        Baseline().Conditions! with { TestFirst = testFirst, ModelDigest = digest };

    /// <summary>Baseline: 50 tasks as in Make. Candidate: cs-modern-001..004 only, all passing in both runs.</summary>
    private static (RepeatSummaryData B, RepeatSummaryData C) TfPair()
    {
        var b = Make([18, 18], [4, 4], [20, 20]) with { Conditions = TfConditions(null) };
        var tasks = Enumerable.Range(1, 4).Select(i => new TaskRepeatSummary($"cs-modern-{i:000}", "cs-modern", 2, 2, [VerificationResult.Passed, VerificationResult.Passed])).ToList();
        tasks.Add(new TaskRepeatSummary("cs-modern-009", "cs-modern", 0, 2, [VerificationResult.Skipped, VerificationResult.Skipped]));
        var c = new RepeatSummaryData(2, 5, 4, 0, 3.8, [new(1, 5, 4, 4, 0, 0, 0, 3.8, "run-1"), new(2, 5, 4, 4, 1, 0, 0, 3.8, "run-2")],
            [new("cs-modern", 5, 4, 0, 4, 4, [4, 4])], tasks, TfConditions(true), false, new TestFirstMetricsData(8, 6, 7, 1, 8, 3, 2, 0));
        return (b, c);
    }

    [Fact]
    public void CompareTestFirst_Reports()
    {
        var (b, c) = TfPair();
        var (code, output) = RunTf(Write("b.json", b), Write("c.json", c));
        Assert.Equal(0, code);
        Assert.Contains("대상 작업: 테스트 우선 4건", output, StringComparison.Ordinal);
        Assert.Contains("평균 성공 수: 기준선 4.00 · 테스트 우선 4.00", output, StringComparison.Ordinal);
        Assert.Contains("차이: +0.00", output, StringComparison.Ordinal);
        Assert.Contains("시간 중위값 비율(전체 작업 기준 — 요약에 작업별 시간이 없음): 2.00배", output, StringComparison.Ordinal);
        Assert.Contains("검증 불변 조건 위반(전체 작업 기준 — 요약에 작업별 위반이 없음): 기준선 0건 · 테스트 우선 1건", output, StringComparison.Ordinal);
        Assert.Contains("| 수용 기준 충족률 | 85.7% (6/7) |", output, StringComparison.Ordinal);
        Assert.DoesNotContain("판정:", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareTestFirst_BaselineCountsOnlyTestFirstTasks()
    {
        var (b, c) = TfPair();
        // Baseline has 50 tasks (Legacy/SQL included) that pass; only the four target ids may enter its average. The skipped
        // candidate task cs-modern-009 is not a target either.
        var (_, output) = RunTf(Write("b.json", b), Write("c.json", c));
        Assert.Contains("기준선 4.00", output, StringComparison.Ordinal);
        Assert.DoesNotContain("42.00", output, StringComparison.Ordinal); // the whole-baseline mean (18 + 4 + 20)
    }

    [Fact]
    public void CompareTestFirst_CandidateNotTestFirst_NotJudged()
    {
        var (b, c) = TfPair();
        var (code, output) = RunTf(Write("b.json", b), Write("c.json", c with { Conditions = TfConditions(null) }));
        Assert.Equal(4, code);
        Assert.Contains("판정하지 않습니다", output, StringComparison.Ordinal);
        var (code2, _) = RunTf(Write("b2.json", b with { Conditions = TfConditions(true) }), Write("c2.json", c));
        Assert.Equal(4, code2);
    }

    [Fact]
    public void CompareTestFirst_DifferentDigest_NotJudged()
    {
        var (b, c) = TfPair();
        var (code, output) = RunTf(Write("b.json", b with { Conditions = TfConditions(null, "aaaa") }), Write("c.json", c with { Conditions = TfConditions(true, "bbbb") }));
        Assert.Equal(4, code);
        Assert.Contains("모델 digest가 다릅니다", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Compare_WithoutTestFirst_StillRejectsTestFirstSubset()
    {
        var (b, c) = TfPair();
        Assert.Equal(4, Run(Write("b.json", b), Write("c.json", c)).Code); // task set differs: unchanged R4 behaviour
    }

    [Fact]
    public void CompareTestFirst_TargetMissingFromBaseline_NotJudged()
    {
        var (b, c) = TfPair();
        var bb = b with { Tasks = b.Tasks.Where(t => t.Id != "cs-modern-003").ToList() };
        var (code, output) = RunTf(Write("b.json", bb), Write("c.json", c));
        Assert.Equal(4, code);
        Assert.Contains("기준선에 없는 작업: cs-modern-003", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareTestFirst_NoTargets_NotJudged()
    {
        var (b, c) = TfPair();
        var cc = c with { Tasks = [new TaskRepeatSummary("cs-modern-009", "cs-modern", 0, 2, [VerificationResult.Skipped, VerificationResult.Skipped])] };
        var (code, output) = RunTf(Write("b.json", b), Write("c.json", cc));
        Assert.Equal(4, code);
        Assert.Contains("테스트 우선 대상 작업이 없습니다", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareTestFirst_DifferentOllamaVersion_NotJudged()
    {
        var (b, c) = TfPair();
        var (code, output) = RunTf(Write("b.json", b with { Conditions = TfConditions(null) with { OllamaVersion = "0.9.0" } }),
            Write("c.json", c with { Conditions = TfConditions(true) with { OllamaVersion = "0.9.1" } }));
        Assert.Equal(4, code);
        Assert.Contains("Ollama 버전이 다릅니다", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareTestFirst_CancelledSummary_NotJudged()
    {
        var (b, c) = TfPair();
        var (code, output) = RunTf(Write("b.json", b), Write("c.json", c with { Cancelled = true }));
        Assert.Equal(4, code);
        Assert.Contains("중단된 반복 실행", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareTestFirst_DifferentModelName_NotJudged()
    {
        var (b, c) = TfPair();
        var (code, output) = RunTf(Write("b.json", b), Write("c.json", c with { Conditions = TfConditions(true) with { Model = "other-model" } }));
        Assert.Equal(4, code);
        Assert.Contains("모델이 다릅니다", output, StringComparison.Ordinal);
        Assert.Contains("other-model", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareTestFirst_DifferentRunCount_NotJudged()
    {
        var (b, c) = TfPair();
        var cc = c with
        {
            RunCount = 3,
            Runs = [.. c.Runs, new(3, 5, 4, 4, 0, 0, 0, 3.8, "run-3")],
        };
        var (code, output) = RunTf(Write("b.json", b), Write("c.json", cc));
        Assert.Equal(4, code);
        Assert.Contains("회차 수가 다릅니다: 기준선 2 · 후보 3", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareTestFirst_PrintsRunCountsAndConditionDifferences_WithoutTestFirstFlag()
    {
        var (b, c) = TfPair();
        var (_, same) = RunTf(Write("b.json", b), Write("c.json", c));
        Assert.Contains("조건 차이: 없음", same, StringComparison.Ordinal);
        Assert.Contains("회차 수: 기준선 2 · 테스트 우선 2", same, StringComparison.Ordinal);

        var (code, output) = RunTf(Write("b2.json", b), Write("c2.json", c with { Conditions = TfConditions(true) with { ThinkMode = "off" } }));
        Assert.Equal(0, code);
        Assert.Contains("조건 차이:", output, StringComparison.Ordinal);
        Assert.Contains("| thinkMode |", output, StringComparison.Ordinal);
        Assert.DoesNotContain("testFirst", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareTestFirst_WrongPathCount_Usage()
    {
        var (code, output) = RunTf("only-one.json");
        Assert.Equal(4, code);
        Assert.Contains("사용법", output, StringComparison.Ordinal);
    }

    [Fact]
    public void CompareUsage_MentionsTestFirst() =>
        Assert.Contains("compare --test-first <v1 summary.json> <테스트 우선 summary.json>", CompareCommand.Usage, StringComparison.Ordinal);

    private (int Code, string Output) RunTf(params string[] paths)
    {
        var output = new StringWriter();
        var code = CompareCommand.RunTestFirst(paths, output);
        return (code, output.ToString());
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
