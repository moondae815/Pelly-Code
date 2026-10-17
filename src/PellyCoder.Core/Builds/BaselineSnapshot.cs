using PellyCoder.Core.Models;

namespace PellyCoder.Core.Builds;

/// <summary>One finished build: parsed errors and warnings plus how the process ended.</summary>
public sealed record BuildRun(
    IReadOnlyList<Diagnostic> Errors,
    IReadOnlyList<Diagnostic> Warnings,
    int ExitCode,
    bool TimedOut,
    TimeSpan Duration);

/// <summary>The build of the original, unedited project; its errors are never counted against an edit.</summary>
public sealed record BaselineSnapshot(BuildRun Run);

/// <summary>"New errors only": multiset difference between the current errors and the baseline errors.</summary>
public static class BaselineDiff
{
    /// <summary>
    /// Key is (File case-insensitive, Code, Message); line and column are ignored because edits shift them.
    /// Each current diagnostic consumes one matching baseline entry if available, otherwise it is new.
    /// Result keeps the order of <paramref name="current"/>.
    /// </summary>
    public static IReadOnlyList<Diagnostic> New(IReadOnlyList<Diagnostic> baseline, IReadOnlyList<Diagnostic> current)
    {
        var remaining = new Dictionary<(string, string, string), int>(KeyComparer.Instance);
        foreach (var d in baseline)
        {
            var key = (d.File, d.Code, d.Message);
            remaining[key] = remaining.GetValueOrDefault(key) + 1;
        }

        var result = new List<Diagnostic>();
        foreach (var d in current)
        {
            var key = (d.File, d.Code, d.Message);
            if (remaining.TryGetValue(key, out var n) && n > 0) remaining[key] = n - 1;
            else result.Add(d);
        }

        return result;
    }

    private sealed class KeyComparer : IEqualityComparer<(string File, string Code, string Message)>
    {
        public static readonly KeyComparer Instance = new();

        public bool Equals((string File, string Code, string Message) x, (string File, string Code, string Message) y) =>
            string.Equals(x.File, y.File, StringComparison.OrdinalIgnoreCase)
            && string.Equals(x.Code, y.Code, StringComparison.Ordinal)
            && string.Equals(x.Message, y.Message, StringComparison.Ordinal);

        public int GetHashCode((string File, string Code, string Message) k) =>
            HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(k.File), k.Code, k.Message);
    }
}
