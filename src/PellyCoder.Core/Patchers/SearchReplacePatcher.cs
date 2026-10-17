using PellyCoder.Core.Models;

namespace PellyCoder.Core.Patchers;

public static class SearchReplacePatcher
{
    private sealed record Range(int Start, int End); // 0-based, inclusive; End < Start means empty

    public static PatchOutcome Apply(string originalLf, IReadOnlyList<PatchBlock> blocks, IReadOnlyList<EditableRegion> regions)
    {
        var trailingNewline = originalLf.EndsWith('\n');
        var text = trailingNewline ? originalLf[..^1] : originalLf;
        var lines = originalLf.Length == 0 ? new List<string>() : text.Split('\n').ToList();
        var live = regions.Select(r => new Range(r.Span.StartLine - 1, r.Span.EndLine - 1)).ToList();
        var changed = new List<Range>();
        var stages = new List<MatchStage>();

        for (var b = 0; b < blocks.Count; b++)
        {
            var searchLines = blocks[b].Search.Split('\n');
            var replaceLines = blocks[b].Replace.Length == 0 ? [] : blocks[b].Replace.Split('\n');
            var n = searchLines.Length;

            int matchStart = -1;
            var stage = MatchStage.Exact;
            var anyOutside = false;

            foreach (var candidate in new[] { MatchStage.Exact, MatchStage.TrailingWhitespace, MatchStage.Indentation })
            {
                var inRegion = new List<int>();
                for (var i = 0; i + n <= lines.Count; i++)
                {
                    if (!Matches(lines, i, searchLines, candidate)) continue;
                    if (live.Any(r => i >= r.Start && i + n - 1 <= r.End)) inRegion.Add(i);
                    else anyOutside = true;
                }

                if (inRegion.Count == 1)
                {
                    matchStart = inRegion[0];
                    stage = candidate;
                    break;
                }

                if (inRegion.Count > 1)
                {
                    return Fail(stages, PatchFailureKinds.Ambiguous,
                        $"Block {b + 1}: SEARCH matched {inRegion.Count} locations in the editable region; add more context lines.", null);
                }
            }

            if (matchStart < 0)
            {
                if (anyOutside)
                {
                    return Fail(stages, PatchFailureKinds.OutsideRegion,
                        $"Block {b + 1}: SEARCH text matches only outside the editable regions.", null);
                }

                return Fail(stages, PatchFailureKinds.NotFound,
                    $"Block {b + 1}: SEARCH text not found in the editable regions.",
                    Nearest(lines, live, searchLines[0]));
            }

            var matchEnd = matchStart + n - 1;
            if (changed.Any(c => c.End >= c.Start && matchStart <= c.End && matchEnd >= c.Start))
            {
                return Fail(stages, PatchFailureKinds.Overlap,
                    $"Block {b + 1}: SEARCH overlaps lines changed by an earlier block.", null);
            }

            if (stage == MatchStage.Indentation) replaceLines = Reindent(searchLines, lines, matchStart, replaceLines);

            lines.RemoveRange(matchStart, n);
            lines.InsertRange(matchStart, replaceLines);
            var delta = replaceLines.Length - n;

            for (var i = 0; i < live.Count; i++)
            {
                var r = live[i];
                if (matchStart >= r.Start && matchEnd <= r.End) live[i] = r with { End = r.End + delta };
                else if (r.Start > matchEnd) live[i] = new Range(r.Start + delta, r.End + delta);
            }

            for (var i = 0; i < changed.Count; i++)
            {
                var c = changed[i];
                if (c.Start > matchEnd) changed[i] = new Range(c.Start + delta, c.End + delta);
            }

            changed.Add(new Range(matchStart, matchStart + replaceLines.Length - 1));
            stages.Add(stage);
        }

        var result = string.Join('\n', lines) + (trailingNewline ? "\n" : "");
        return new PatchOutcome(true, result, stages, null);
    }

    private static PatchOutcome Fail(List<MatchStage> stages, string kind, string detail, string? nearest) =>
        new(false, null, stages, new PatchFailure(kind, detail, nearest));

    private static bool Matches(List<string> lines, int at, string[] search, MatchStage stage)
    {
        for (var k = 0; k < search.Length; k++)
        {
            var a = lines[at + k];
            var s = search[k];
            var equal = stage switch
            {
                MatchStage.Exact => a == s,
                MatchStage.TrailingWhitespace => a.TrimEnd() == s.TrimEnd(),
                _ => a.Trim() == s.Trim(),
            };
            if (!equal) return false;
        }

        return true;
    }

    private static string[] Reindent(string[] search, List<string> lines, int matchStart, string[] replace)
    {
        var first = Array.FindIndex(search, l => l.Trim().Length > 0);
        if (first < 0) return replace;

        var searchIndent = LeadingWhitespace(search[first]);
        var origIndent = LeadingWhitespace(lines[matchStart + first]);
        if (searchIndent == origIndent) return replace;

        // Fast path: indents are prefix-related with a single-kind difference and the line carries the
        // SEARCH indent as its own prefix: swap that prefix for the original's indent.
        var related = origIndent.StartsWith(searchIndent, StringComparison.Ordinal)
            ? IsUniform(origIndent[searchIndent.Length..])
            : searchIndent.StartsWith(origIndent, StringComparison.Ordinal) && IsUniform(searchIndent[origIndent.Length..]);
        var fast = related;

        // General path: convert by columns (tab width 4) and emit in the matched original's style.
        var searchCols = Columns(searchIndent);
        var origCols = Columns(origIndent);
        var useTabs = origIndent.Contains('\t');
        var origKind = useTabs ? '\t' : ' ';
        return replace.Select(l =>
        {
            if (l.Trim().Length == 0) return l;
            var indent = LeadingWhitespace(l);
            if (fast && indent.StartsWith(searchIndent, StringComparison.Ordinal)
                && indent[searchIndent.Length..].All(c => c == origKind))
            {
                return origIndent + l[searchIndent.Length..];
            }

            var target = Math.Max(0, origCols + Columns(indent) - searchCols);
            var emitted = useTabs
                ? new string('\t', target / TabWidth) + new string(' ', target % TabWidth)
                : new string(' ', target);
            return emitted + l[indent.Length..];
        }).ToArray();
    }

    private const int TabWidth = 4;

    private static bool IsUniform(string s) => s.All(c => c == s[0]);

    private static int Columns(string indent)
    {
        var col = 0;
        foreach (var c in indent) col = c == '\t' ? (col / TabWidth + 1) * TabWidth : col + 1;
        return col;
    }

    private static string LeadingWhitespace(string line)
    {
        var i = 0;
        while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
        return line[..i];
    }

    private static string? Nearest(List<string> lines, List<Range> regions, string firstSearchLine)
    {
        var target = firstSearchLine.Trim();
        var best = -1;
        var bestDistance = int.MaxValue;
        for (var i = 0; i < lines.Count; i++)
        {
            if (!regions.Any(r => i >= r.Start && i <= r.End)) continue;
            var d = Levenshtein(target, lines[i].Trim());
            if (d < bestDistance) { bestDistance = d; best = i; }
        }

        if (best < 0) return null;
        var from = Math.Max(0, best - 3);
        var to = Math.Min(lines.Count - 1, best + 3);
        return string.Join('\n', lines.Skip(from).Take(to - from + 1));
    }

    private static int Levenshtein(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var cur = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            cur[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                cur[j] = Math.Min(Math.Min(cur[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }

            (prev, cur) = (cur, prev);
        }

        return prev[b.Length];
    }
}
