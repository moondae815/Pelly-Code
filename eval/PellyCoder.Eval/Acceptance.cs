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

        if (baseline.Conditions is { } bc && candidate.Conditions is { } cc)
        {
            if (bc.ModelDigest is { } bd && cc.ModelDigest is { } cd && bd != cd)
                problems.Add($"모델 digest가 다릅니다 ({Head(bd)} ≠ {Head(cd)})");
            if (bc.OllamaVersion is { } bo && cc.OllamaVersion is { } co && bo != co)
                problems.Add($"Ollama 버전이 다릅니다 ({bo} ≠ {co})");
        }

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

    internal static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    private static string S(double? v) => v is null ? "없음" : F(v.Value) + "초";

    private static string Signed(double v) => double.IsNaN(v) ? "없음" : v < 0 ? "−" + F(-v) : "+" + F(v);
}

/// <summary><c>compare &lt;baseline summary.json&gt; &lt;candidate summary.json&gt;</c>: prints the R4 verdict.
/// Exit 0 adopted, 3 rejected, 4 not judged (bad arguments, unreadable or incomparable summaries).</summary>
public static class CompareCommand
{
    public const string Usage = "사용법: dotnet run --project eval/PellyCoder.Eval -- compare <기준선 summary.json> <후보 summary.json>\n"
        + "  종료 코드: 0 채택 · 3 거절 · 4 판정 불가(인자·파일 오류, 비교할 수 없는 요약)";

    public static int Run(IReadOnlyList<string> paths, TextWriter output)
    {
        if (paths.Count != 2)
        {
            output.WriteLine(Usage);
            return 4;
        }

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
                return 4;
            }
        }
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

        var diff = Acceptance.ConditionDifferences(baseline.Conditions!, candidate.Conditions!);
        output.WriteLine();
        output.WriteLine(diff.Count == 0 ? "조건 차이: 없음" : "조건 차이:");
        if (diff.Count > 0)
        {
            output.WriteLine("| 조건 | 기준선 | 후보 |");
            output.WriteLine("| :--- | :--- | :--- |");
            foreach (var (field, b, c) in diff) output.WriteLine($"| {field} | {b} | {c} |");
        }

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
