using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PellyCoder.Eval;

/// <summary>The R4 verdict: <paramref name="Reasons"/> has one line per rule, prefixed <c>✓</c> or <c>✗</c>.</summary>
public sealed record AcceptanceResult(bool Accepted, IReadOnlyList<string> Reasons);

/// <summary>
/// The M3b tuning acceptance rule (R4). A candidate is adopted only when all four hold against the baseline:
/// 1. mean passed tasks over the runs is at least 1.0 higher;
/// 2. no id-prefix group (cs-legacy, cs-modern, sql) falls by more than 0.5 on average;
/// 3. every run of the candidate has 0 verification invariant violations;
/// 4. the candidate's median task time is at most 1.5 × the baseline's.
/// Whether two summaries may be compared at all is <see cref="Comparability"/>; <see cref="Compare"/> assumes they may.
/// </summary>
public static class Acceptance
{
    public const double MinMeanGain = 1.0;
    public const double MaxGroupDrop = 0.5;
    public const double MaxTimeRatio = 1.5;

    // Means of integers over 3 runs are thirds; 146/3 − 143/3 is 0.99999… in doubles and must still count as +1.0.
    private const double Epsilon = 1e-9;

    public static AcceptanceResult Compare(RepeatSummaryData baseline, RepeatSummaryData candidate)
    {
        var reasons = new List<string>();
        var ok = true;
        void Rule(bool pass, string text)
        {
            ok &= pass;
            reasons.Add((pass ? "✓ " : "✗ ") + text);
        }

        var gain = candidate.MeanSuccesses - baseline.MeanSuccesses;
        Rule(gain >= MinMeanGain - Epsilon,
            $"규칙 1 평균 성공 {F(baseline.MeanSuccesses)} → {F(candidate.MeanSuccesses)} ({Signed(gain)}, 기준 +{F(MinMeanGain)} 이상)");

        var drops = new List<string>();
        foreach (var g in baseline.Groups)
        {
            var c = candidate.Groups.FirstOrDefault(x => x.Group == g.Group);
            if (c is null)
            {
                drops.Add($"{g.Group} 없음");
                continue;
            }
            var delta = c.MeanSuccesses - g.MeanSuccesses;
            if (delta < -MaxGroupDrop - Epsilon) drops.Add($"{g.Group} {F(g.MeanSuccesses)} → {F(c.MeanSuccesses)} ({Signed(delta)})");
        }
        var groupText = string.Join(" · ", baseline.Groups.Select(g =>
            $"{g.Group} {Signed((candidate.Groups.FirstOrDefault(x => x.Group == g.Group)?.MeanSuccesses ?? double.NaN) - g.MeanSuccesses)}"));
        Rule(drops.Count == 0, drops.Count == 0
            ? $"규칙 2 범주 평균 감소 {F(MaxGroupDrop)} 이하 ({groupText})"
            : $"규칙 2 범주 평균이 {F(MaxGroupDrop)}건 넘게 줄었습니다: {string.Join(", ", drops)}");

        var violated = candidate.Runs.Where(r => r.InvariantViolations > 0).ToList();
        Rule(violated.Count == 0, violated.Count == 0
            ? $"규칙 3 검증 불변 조건 위반 0건 ({candidate.Runs.Count}회 모두)"
            : $"규칙 3 검증 불변 조건 위반: {string.Join(", ", violated.Select(r => $"회차 {r.Run} {r.InvariantViolations}건"))}");

        var limit = baseline.MedianWallSeconds * MaxTimeRatio;
        var time = candidate.MedianWallSeconds;
        Rule(limit is not null && time is not null && time <= limit + Epsilon,
            $"규칙 4 작업 시간 중위 {S(baseline.MedianWallSeconds)} → {S(time)} (한도 {S(limit)}, 기준선의 {F(MaxTimeRatio)}배)");

        return new AcceptanceResult(ok, reasons);
    }

    /// <summary>
    /// Why two summaries cannot be judged by R4 (empty when they can): a cancelled series, missing conditions/runs/groups,
    /// a different run count, task set or per-run number of tasks that ran, or a different model digest / Ollama version when both
    /// summaries record one (a side without it, e.g. a v1 file, is not refused). Differing <see cref="RunConditions"/> are
    /// expected (the candidate's change) and are listed by <see cref="ConditionDifferences"/>, not refused.
    /// </summary>
    public static IReadOnlyList<string> Comparability(RepeatSummaryData baseline, RepeatSummaryData candidate)
    {
        var problems = new List<string>();
        foreach (var (name, s) in new[] { ("기준선", baseline), ("후보", candidate) })
        {
            if (s.Cancelled) problems.Add($"{name}: 중단된 반복 실행입니다(cancelled).");
            if (s.Conditions is null) problems.Add($"{name}: 실행 조건(conditions)이 없습니다.");
            if (s.Runs.Count == 0) problems.Add($"{name}: 회차(runs)가 없습니다.");
            else if (s.Runs.Count != s.RunCount) problems.Add($"{name}: runCount {s.RunCount}와 runs {s.Runs.Count}개가 다릅니다.");
            if (s.Groups.Count == 0) problems.Add($"{name}: 범주(groups)가 없습니다.");
        }

        problems.AddRange(ModelIdentityProblems(baseline, candidate));

        if (baseline.RunCount != candidate.RunCount)
            problems.Add($"회차 수가 다릅니다: 기준선 {baseline.RunCount} · 후보 {candidate.RunCount}.");

        var b = baseline.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var c = candidate.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var onlyB = b.Except(c).Order(StringComparer.Ordinal).ToList();
        var onlyC = c.Except(b).Order(StringComparer.Ordinal).ToList();
        if (onlyB.Count > 0 || onlyC.Count > 0)
            problems.Add($"작업 집합이 다릅니다: 기준선에만 [{string.Join(", ", onlyB)}] · 후보에만 [{string.Join(", ", onlyC)}].");

        foreach (var run in baseline.Runs)
        {
            var other = candidate.Runs.FirstOrDefault(r => r.Run == run.Run);
            if (other is not null && other.Ran != run.Ran)
                problems.Add($"회차 {run.Run}에서 실행된 작업 수가 다릅니다: 기준선 {run.Ran} · 후보 {other.Ran}.");
        }
        return problems;
    }

    private static IEnumerable<string> ModelIdentityProblems(RepeatSummaryData baseline, RepeatSummaryData candidate)
    {
        if (baseline.Conditions is not { } bc || candidate.Conditions is not { } cc) yield break;
        if (bc.ModelDigest is { } bd && cc.ModelDigest is { } cd && bd != cd)
            yield return $"모델 digest가 다릅니다 ({Head(bd)} ≠ {Head(cd)})";
        if (bc.OllamaVersion is { } bo && cc.OllamaVersion is { } co && bo != co)
            yield return $"Ollama 버전이 다릅니다 ({bo} ≠ {co})";
    }

    /// <summary>
    /// Why a v1 baseline and a <c>run --test-first</c> candidate cannot be reported on together (empty when they can): the same
    /// model identity rules as <see cref="Comparability"/> plus the model name (spec §4.2: same model), a different run count
    /// (same <c>--repeat N</c>), a cancelled or empty summary, and the test-first flag (candidate must be a test-first run,
    /// baseline must not). The task set may differ: the candidate covers only tasks with judge tests.
    /// </summary>
    public static IReadOnlyList<string> TestFirstComparability(RepeatSummaryData baseline, RepeatSummaryData candidate)
    {
        var problems = new List<string>();
        foreach (var (name, s) in new[] { ("기준선", baseline), ("후보", candidate) })
        {
            if (s.Cancelled) problems.Add($"{name}: 중단된 반복 실행입니다(cancelled).");
            if (s.Conditions is null) problems.Add($"{name}: 실행 조건(conditions)이 없습니다.");
            if (s.Runs.Count == 0) problems.Add($"{name}: 회차(runs)가 없습니다.");
        }
        problems.AddRange(ModelIdentityProblems(baseline, candidate));
        if (baseline.Conditions is { } bm && candidate.Conditions is { } cm && bm.Model != cm.Model)
            problems.Add($"모델이 다릅니다 ({bm.Model} ≠ {cm.Model})");
        if (baseline.RunCount != candidate.RunCount)
            problems.Add($"회차 수가 다릅니다: 기준선 {baseline.RunCount} · 후보 {candidate.RunCount}.");
        if (candidate.Conditions is { } cc && cc.TestFirst != true) problems.Add("후보가 테스트 우선 실행(run --test-first)이 아닙니다.");
        if (baseline.Conditions is { TestFirst: true }) problems.Add("기준선이 테스트 우선 실행입니다. v1 실행 요약이어야 합니다.");
        return problems;
    }

    /// <summary>The <see cref="RunConditions"/> fields (camelCase JSON names) whose values differ, with both values as JSON text.</summary>
    public static IReadOnlyList<(string Field, string Baseline, string Candidate)> ConditionDifferences(RunConditions baseline, RunConditions candidate)
    {
        var b = ToObject(baseline);
        var c = ToObject(candidate);
        var diff = new List<(string, string, string)>();
        foreach (var name in b.Select(p => p.Key).Union(c.Select(p => p.Key)))
        {
            var bv = b[name]?.ToJsonString() ?? "null";
            var cv = c[name]?.ToJsonString() ?? "null";
            if (bv != cv) diff.Add((name, bv, cv));
        }
        return diff;
    }

    private static JsonObject ToObject(RunConditions conditions) =>
        JsonSerializer.SerializeToNode(conditions, RepeatSummary.Json)!.AsObject();

    private static string Head(string digest) => digest.Length <= 12 ? digest : digest[..12];

    internal static string S(double? v) => v is null ? "없음" : F(v.Value) + "초";

    internal static string Sg(double v) => Signed(v);

    internal static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Signed(double v) => double.IsNaN(v) ? "없음" : v < 0 ? "−" + F(-v) : "+" + F(v);
}

/// <summary><c>compare &lt;baseline summary.json&gt; &lt;candidate summary.json&gt;</c>: prints the R4 verdict.
/// Exit 0 adopted, 3 rejected, 4 not judged (bad arguments, unreadable or incomparable summaries).</summary>
public static class CompareCommand
{
    public const string Usage = "사용법: dotnet run --project eval/PellyCoder.Eval -- compare <기준선 summary.json> <후보 summary.json>\n"
        + "        dotnet run --project eval/PellyCoder.Eval -- compare --test-first <v1 summary.json> <테스트 우선 summary.json>\n"
        + "  종료 코드: 0 채택 · 3 거절 · 4 판정 불가(인자·파일 오류, 비교할 수 없는 요약)";

    private static RepeatSummaryData[]? Load(IReadOnlyList<string> paths, TextWriter output)
    {
        var loaded = new RepeatSummaryData[2];
        for (var i = 0; i < 2; i++)
        {
            try
            {
                loaded[i] = RepeatSummary.FromJson(File.ReadAllText(paths[i]));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                output.WriteLine($"판정하지 않습니다 — {paths[i]}을 읽을 수 없습니다: {ex.Message}");
                return null;
            }
        }
        return loaded;
    }

    /// <summary><c>compare --test-first</c>: a report, not a verdict. Exit 0 reported, 4 not judged.</summary>
    public static int RunTestFirst(IReadOnlyList<string> paths, TextWriter output)
    {
        if (paths.Count != 2)
        {
            output.WriteLine(Usage);
            return 4;
        }
        if (Load(paths, output) is not { } loaded) return 4;
        var (baseline, candidate) = (loaded[0], loaded[1]);

        output.WriteLine($"기준선: {paths[0]}");
        output.WriteLine($"후보:   {paths[1]}");
        var problems = Acceptance.TestFirstComparability(baseline, candidate);
        if (problems.Count > 0)
        {
            output.WriteLine("판정하지 않습니다 — 두 요약을 비교할 수 없습니다:");
            foreach (var p in problems) output.WriteLine("  * " + p);
            return 4;
        }

        // Test-first target tasks = the candidate's tasks that ran in some run. The candidate summary records no per-task
        // judge-test flag, but `run --test-first` skips every task without judge tests, so a task that never left Skipped
        // (or "—") is not a target; the baseline average counts only these ids.
        var targets = candidate.Tasks.Where(t => t.Outcomes.Any(o => o != VerificationResult.Skipped && o != "—"))
            .Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        if (targets.Count == 0)
        {
            output.WriteLine("판정하지 않습니다 — 테스트 우선 대상 작업이 없습니다.");
            return 4;
        }
        var inBaseline = baseline.Tasks.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var missing = targets.Where(id => !inBaseline.Contains(id)).Order(StringComparer.Ordinal).ToList();
        if (missing.Count > 0)
        {
            output.WriteLine($"판정하지 않습니다 — 기준선에 없는 작업: {string.Join(", ", missing)}");
            return 4;
        }
        double Mean(RepeatSummaryData s) =>
            s.RunCount == 0 ? 0 : (double)s.Tasks.Where(t => targets.Contains(t.Id)).Sum(t => t.Successes) / s.RunCount;
        var (bMean, cMean) = (Mean(baseline), Mean(candidate));

        // The test-first flag is the point of the comparison, not a condition difference.
        WriteConditionDifferences(Acceptance.ConditionDifferences(baseline.Conditions!, candidate.Conditions!)
            .Where(d => d.Field != "testFirst").ToList(), output);
        output.WriteLine();
        output.WriteLine("보고 (판정이 아닙니다):");
        output.WriteLine($"* 대상 작업: 테스트 우선 {targets.Count}건 (기준선은 이 작업만 셉니다)");
        output.WriteLine($"* 회차 수: 기준선 {baseline.RunCount} · 테스트 우선 {candidate.RunCount}");
        output.WriteLine($"* 평균 성공 수: 기준선 {Acceptance.F(bMean)} · 테스트 우선 {Acceptance.F(cMean)}");
        output.WriteLine($"* 차이: {Acceptance.Sg(cMean - bMean)}");
        var ratio = baseline.MedianWallSeconds is > 0 && candidate.MedianWallSeconds is not null
            ? Acceptance.F(candidate.MedianWallSeconds.Value / baseline.MedianWallSeconds.Value) + "배" : "없음";
        output.WriteLine($"* 시간 중위값 비율(전체 작업 기준 — 요약에 작업별 시간이 없음): {ratio} (기준선 {Acceptance.S(baseline.MedianWallSeconds)} · 테스트 우선 {Acceptance.S(candidate.MedianWallSeconds)})");
        output.WriteLine($"* 검증 불변 조건 위반(전체 작업 기준 — 요약에 작업별 위반이 없음): 기준선 {baseline.Runs.Sum(r => r.InvariantViolations)}건 · 테스트 우선 {candidate.Runs.Sum(r => r.InvariantViolations)}건");
        output.WriteLine();
        if (candidate.TestFirst is { } tf)
        {
            var sb = new System.Text.StringBuilder();
            TestFirstMetrics.AppendSection(sb, tf);
            output.Write(sb.ToString());
        }
        else
        {
            output.WriteLine("테스트 우선 지표: 후보 요약에 없습니다.");
            output.WriteLine(TestFirstMetricsData.Notice);
        }
        return 0;
    }

    private static void WriteConditionDifferences(IReadOnlyList<(string Field, string Baseline, string Candidate)> diff, TextWriter output)
    {
        output.WriteLine();
        output.WriteLine(diff.Count == 0 ? "조건 차이: 없음" : "조건 차이:");
        if (diff.Count > 0)
        {
            output.WriteLine("| 조건 | 기준선 | 후보 |");
            output.WriteLine("| :--- | :--- | :--- |");
            foreach (var (field, b, c) in diff) output.WriteLine($"| {field} | {b} | {c} |");
        }
    }

    public static int Run(IReadOnlyList<string> paths, TextWriter output)
    {
        if (paths.Count != 2)
        {
            output.WriteLine(Usage);
            return 4;
        }

        if (Load(paths, output) is not { } loaded) return 4;
        var (baseline, candidate) = (loaded[0], loaded[1]);

        output.WriteLine($"기준선: {paths[0]}");
        output.WriteLine($"후보:   {paths[1]}");
        var problems = Acceptance.Comparability(baseline, candidate);
        if (problems.Count > 0)
        {
            output.WriteLine("판정하지 않습니다 — 두 요약을 비교할 수 없습니다:");
            foreach (var p in problems) output.WriteLine("  * " + p);
            return 4;
        }

        WriteConditionDifferences(Acceptance.ConditionDifferences(baseline.Conditions!, candidate.Conditions!), output);

        output.WriteLine();
        output.WriteLine("| 범주 | 기준선 평균 | 후보 평균 |");
        output.WriteLine("| :--- | ---: | ---: |");
        foreach (var g in baseline.Groups)
        {
            var c = candidate.Groups.FirstOrDefault(x => x.Group == g.Group);
            output.WriteLine($"| {g.Group} | {Acceptance.F(g.MeanSuccesses)} | {(c is null ? "없음" : Acceptance.F(c.MeanSuccesses))} |");
        }
        output.WriteLine($"| 전체 | {Acceptance.F(baseline.MeanSuccesses)} | {Acceptance.F(candidate.MeanSuccesses)} |");

        var result = Acceptance.Compare(baseline, candidate);
        output.WriteLine();
        foreach (var r in result.Reasons) output.WriteLine(r);
        output.WriteLine();
        output.WriteLine(result.Accepted ? "판정: 채택 (R4 네 규칙 모두 만족)" : "판정: 거절 (R4 규칙 중 ✗ 항목)");
        return result.Accepted ? 0 : 3;
    }
}
