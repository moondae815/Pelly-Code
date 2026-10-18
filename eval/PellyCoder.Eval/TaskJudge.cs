using System.Text.RegularExpressions;

namespace PellyCoder.Eval;

/// <summary>Judges one finished task by the common rules of eval/benchmark/README.md.</summary>
public static partial class TaskJudge
{
    public static JudgeOutcome Judge(BenchmarkTask task, string originalTarget, string finalTarget, int exitCode, VerificationResult independent)
    {
        var judge = task.Judge ?? new BenchmarkJudge();
        var reasons = new List<string>();

        if (exitCode != 0)
            reasons.Add($"종료 코드 {exitCode}");
        if (string.Equals(Normalize(originalTarget), Normalize(finalTarget), StringComparison.Ordinal))
            reasons.Add("대상이 바뀌지 않음");

        foreach (var s in judge.MustContain ?? [])
            if (!finalTarget.Contains(s, StringComparison.Ordinal))
                reasons.Add($"mustContain '{s}'이(가) 없음");

        foreach (var s in judge.MustAdd ?? [])
        {
            int before = Count(originalTarget, s), after = Count(finalTarget, s);
            if (after <= before)
                reasons.Add($"mustAdd '{s}'이(가) 늘지 않음 ({before} → {after})");
        }

        foreach (var s in judge.MustNotContain ?? [])
            if (finalTarget.Contains(s, StringComparison.Ordinal))
                reasons.Add($"mustNotContain '{s}'이(가) 남아 있음");

        if (independent.Status == VerificationResult.Failed)
            reasons.Add($"독립 검증 실패: {independent.Detail}");
        // A behaviour judge that did not run (judge tooling failure) proved nothing: no success, but no invariant violation either.
        else if (independent.Status == VerificationResult.Skipped && (judge.Tests?.Count ?? 0) + (judge.FailingTests?.Count ?? 0) > 0)
            reasons.Add($"동작 판정 생략: {independent.Detail}");

        var checkRequired = judge.Build == true || judge.SqlCompile == true;
        var buildCheck = checkRequired ? independent.Status : VerificationResult.NotRequired;
        var invariant = exitCode == 0 && independent.Status == VerificationResult.Failed;
        return new JudgeOutcome(reasons.Count == 0, reasons, buildCheck, invariant);
    }

    /// <summary>Whitespace runs (line breaks and indentation included) collapsed to one space: a reformat is no change.</summary>
    internal static string Normalize(string text) => WhitespaceRun().Replace(text, " ").Trim();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    internal static int Count(string text, string needle)
    {
        if (needle.Length == 0) return 0;
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0; i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
