using System.Text.RegularExpressions;

namespace PellyCoder.Core.Rules;

public sealed record RuleViolation(ProjectRule Rule, string AddedLine);

/// <summary>A line the patched file has beyond the original: <paramref name="Text"/> as written, <paramref name="Code"/> with comments and literals masked.</summary>
public sealed record AddedLine(string Text, string Code);

/// <summary>
/// Checks the lines the patched file has beyond the original (a multiset comparison of the two files, so earlier attempts'
/// leftovers count) against every rule's <c>forbid</c> regex. The regex sees only code (<see cref="CodeMask"/>); the
/// violation reports the line as written.
/// </summary>
public static class ForbiddenPatternChecker
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Lines of the patched text left after removing the original's lines as a multiset (patched order kept, ordinal,
    /// untrimmed). <paramref name="patchedCode"/> is <see cref="CodeMask"/>'s view of the patched text; each added line takes
    /// its code from the same line there. Without it, or when its line count differs, the code is the text itself.
    /// </summary>
    public static IReadOnlyList<AddedLine> AddedLines(string originalText, string patchedText, string? patchedCode = null)
    {
        var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var line in Split(originalText))
            remaining[line] = remaining.GetValueOrDefault(line) + 1;
        var patched = Split(patchedText);
        var code = patchedCode is null ? null : Split(patchedCode);
        if (code is not null && code.Count != patched.Count) code = null;

        var added = new List<AddedLine>();
        for (var i = 0; i < patched.Count; i++)
        {
            var line = patched[i];
            if (remaining.TryGetValue(line, out var n) && n > 0) remaining[line] = n - 1;
            else added.Add(new AddedLine(line, code?[i] ?? line));
        }
        return added;
    }

    /// <summary>Every rule with a <c>forbid</c> against the code of every added line. Throws <see cref="RegexMatchTimeoutException"/> on a slow pattern.</summary>
    public static IReadOnlyList<RuleViolation> Check(IReadOnlyList<ProjectRule> rules, IReadOnlyList<AddedLine> addedLines)
    {
        var violations = new List<RuleViolation>();
        foreach (var rule in rules)
        {
            if (rule.Forbid is null) continue;
            var regex = new Regex(rule.Forbid, RegexOptions.CultureInvariant, MatchTimeout);
            foreach (var line in addedLines.DistinctBy(l => l.Text, StringComparer.Ordinal))
                if (regex.IsMatch(line.Code)) violations.Add(new RuleViolation(rule, line.Text));
        }
        return violations;
    }

    private static List<string> Split(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }
}
