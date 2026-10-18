using System.Text.RegularExpressions;

namespace PellyCoder.Eval;

/// <summary>
/// <c>--tasks</c> and <c>--kind</c> selection. A pattern is a comma-separated list; each item is an id prefix, or a glob
/// (<c>*</c>, <c>?</c>) matched against the whole id.
/// </summary>
public static class TaskFilter
{
    public static bool Matches(BenchmarkTask task, string? pattern, string? kind)
    {
        if (!string.IsNullOrWhiteSpace(kind) && !string.Equals(task.Kind, kind.Trim(), StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.IsNullOrWhiteSpace(pattern))
            return true;
        return pattern.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(p => MatchesOne(task.Id, p));
    }

    private static bool MatchesOne(string id, string item)
    {
        if (item.IndexOfAny(['*', '?']) < 0)
            return id.StartsWith(item, StringComparison.OrdinalIgnoreCase);
        var regex = "^" + Regex.Escape(item).Replace(@"\*", ".*", StringComparison.Ordinal).Replace(@"\?", ".", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(id, regex, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
}
