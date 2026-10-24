using System.Text.RegularExpressions;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Rules;

public sealed record RuleViolation(ProjectRule Rule, string AddedLine);

/// <summary>Checks the lines the model added (REPLACE minus SEARCH) against every rule's <c>forbid</c> regex.</summary>
public static class ForbiddenPatternChecker
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    /// <summary>Per block, the REPLACE lines left after removing the SEARCH lines as a multiset (order kept, ordinal, untrimmed).</summary>
    public static IReadOnlyList<string> AddedLines(IReadOnlyList<PatchBlock> blocks)
    {
        var added = new List<string>();
        foreach (var block in blocks)
        {
            var remaining = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var line in Split(block.Search))
                remaining[line] = remaining.GetValueOrDefault(line) + 1;
            foreach (var line in Split(block.Replace))
            {
                if (remaining.TryGetValue(line, out var n) && n > 0) remaining[line] = n - 1;
                else added.Add(line);
            }
        }
        return added;
    }

    /// <summary>Every rule with a <c>forbid</c> against every added line. Throws <see cref="RegexMatchTimeoutException"/> on a slow pattern.</summary>
    public static IReadOnlyList<RuleViolation> Check(IReadOnlyList<ProjectRule> rules, IReadOnlyList<string> addedLines)
    {
        var violations = new List<RuleViolation>();
        foreach (var rule in rules)
        {
            if (rule.Forbid is null) continue;
            var regex = new Regex(rule.Forbid, RegexOptions.CultureInvariant, MatchTimeout);
            foreach (var line in addedLines)
                if (regex.IsMatch(line)) violations.Add(new RuleViolation(rule, line));
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
