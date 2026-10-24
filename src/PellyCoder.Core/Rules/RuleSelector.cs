using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Rules;

/// <summary><paramref name="Dropped"/> holds only rules whose tag matched but that did not fit the budget.</summary>
public sealed record RuleSelection(IReadOnlyList<ProjectRule> Included, IReadOnlyList<ProjectRule> Dropped, int Tokens);

public static class RuleSelector
{
    /// <summary>
    /// Matches rules against the slice (always-rules, or a tag found case-insensitively in an included part's text),
    /// keeps file order, and drops from the bottom once <paramref name="budget"/> tokens are exceeded.
    /// </summary>
    public static RuleSelection Select(IReadOnlyList<ProjectRule> rules, Slice slice, ITokenCounter tokens, int budget)
    {
        var included = new List<ProjectRule>();
        var dropped = new List<ProjectRule>();
        var used = 0;
        var full = false;
        foreach (var rule in rules)
        {
            if (!Matches(rule, slice)) continue;
            if (!full)
            {
                var cost = tokens.Count(Line(rule) + "\n");
                if (used + cost <= budget)
                {
                    included.Add(rule);
                    used += cost;
                    continue;
                }
                full = true;
            }
            dropped.Add(rule);
        }
        return new RuleSelection(included, dropped, used);
    }

    public static string Render(IEnumerable<ProjectRule> rules) => string.Join("\n", rules.Select(Line));

    private static string Line(ProjectRule rule) => "- " + rule.Text;

    private static bool Matches(ProjectRule rule, Slice slice) =>
        rule.IsAlways || rule.Tags.Any(tag => slice.Included.Any(p => p.Text.Contains(tag, StringComparison.OrdinalIgnoreCase)));
}
