using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Models;
using PellyCoder.Core.Testing;

namespace PellyCoder.Core.CharTests;

/// <summary>Retry feedback for characterization-test failures, every text fitted to <c>budget</c> tokens.</summary>
public sealed class CharTestFeedback(FeedbackBuilder builder, ITokenCounter tokens, int budget)
{
    private const string Ellipsis = "…";
    public const string TruthRequest = "The current behavior is the truth. Change the assertion to the actual value.";
    public const string SurvivorRequest = "These tests pass even when the target throws. Assert the target's return value or effect.";
    public const string RegressionRequest = "Do not change existing tests or production code. Only add new tests.";
    public const string FlakyRequest = "Remove time, randomness, ordering and shared state from these tests.";

    public Feedback ForRejections(IReadOnlyList<CharTestRejection> rejections) =>
        Fit("TEST REJECTED (" + rejections.Count + ")",
            rejections.Select(r => r.TestName is null ? $"{r.Kind}: {r.Reason}" : $"{r.TestName}: {r.Reason}").ToList(), null);

    public Feedback ForParse(string error) => Fit("FORMAT ERROR: " + error, [], "Output only the test methods.");

    public Feedback ForBuild(IReadOnlyList<Diagnostic> newErrors, string testRelativePath, string testTextLf) =>
        builder.ForBuild(newErrors, testRelativePath, testTextLf);

    public Feedback ForFailsOnCurrent(IReadOnlyList<TestCaseResult> failures) =>
        Fit($"TESTS FAIL ON CURRENT CODE ({failures.Count})",
            failures.Select(f => string.Join("\n", new[] { f.FullName }.Concat(FirstLines(f.Message, 3)))).ToList(), TruthRequest);

    public Feedback ForRegression(IReadOnlyList<TestCaseResult> failures) =>
        Fit($"EXISTING TESTS NEWLY FAIL ({failures.Count})",
            failures.Count == 0
                ? ["existing tests failed (exit code only, no names)"]
                : failures.Select(f => string.Join("\n", new[] { f.FullName }.Concat(FirstLines(f.Message, 1)))).ToList(), RegressionRequest);

    public Feedback ForFlaky(IReadOnlyList<string> names) => Fit($"FLAKY TESTS ({names.Count})", names, FlakyRequest);

    public Feedback ForSurvivors(IReadOnlyList<string> names) => Fit($"TESTS SURVIVE MUTATION ({names.Count})", names, SurvivorRequest);

    private static IEnumerable<string> FirstLines(string? message, int n) =>
        (message ?? "").Replace("\r\n", "\n").Trim().Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0).Take(n);

    /// <summary>head, entries (dropped from the back when over budget), tail; the tail stays unless nothing else fits.</summary>
    private Feedback Fit(string head, IReadOnlyList<string> entries, string? tail)
    {
        string Compose(int count, string? firstOverride = null)
        {
            var parts = new List<string> { head };
            for (var i = 0; i < count; i++) parts.Add(i == 0 && firstOverride is not null ? firstOverride : entries[i]);
            if (tail is not null) parts.Add(tail);
            return string.Join("\n", parts);
        }
        for (var count = entries.Count; count >= 1; count--)
        {
            var text = Compose(count);
            if (tokens.Count(text) <= budget) return new(text, tokens.Count(text));
        }
        if (entries.Count > 0)
        {
            var cut = Truncate(entries[0], s => Compose(1, s));
            var text = Compose(1, cut);
            if (tokens.Count(text) <= budget) return new(text, tokens.Count(text));
        }
        return Done(Compose(0));
    }

    private string Truncate(string part, Func<string, string> compose)
    {
        if (tokens.Count(compose(part)) <= budget) return part;
        int lo = 0, hi = part.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (tokens.Count(compose(Cut(part, mid).TrimEnd() + Ellipsis)) <= budget) lo = mid; else hi = mid - 1;
        }
        return Cut(part, lo).TrimEnd() + Ellipsis;
    }

    private static string Cut(string text, int length)
    {
        if (length >= text.Length) return text;
        if (length > 0 && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length])) length--;
        return text[..length];
    }

    private Feedback Done(string text)
    {
        var count = tokens.Count(text);
        if (count <= budget) return new(text, count);
        var cut = Truncate(text, s => s);
        count = tokens.Count(cut);
        if (count <= budget) return new(cut, count);
        count = tokens.Count(Ellipsis);
        return count <= budget ? new(Ellipsis, count) : new("", 0);
    }
}
