using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace PellyCoder.Cli.Terminal;

/// <summary>Builds unified-diff lines (like <c>diff -u</c>) for the REVIEW preview.</summary>
public static class DiffRenderer
{
    /// <returns>Lines of kind ' ' (context), '+', '-' and '@' (hunk header).</returns>
    public static IReadOnlyList<(char Kind, string Text)> UnifiedLines(string before, string after, int context = 3)
    {
        var diff = InlineDiffBuilder.Diff(before, after, ignoreWhiteSpace: false);
        var lines = diff.Lines;
        var result = new List<(char, string)>();

        // Old/new line number (1-based) of each diff line's position.
        var oldNo = new int[lines.Count];
        var newNo = new int[lines.Count];
        int o = 1, n = 1;
        for (var i = 0; i < lines.Count; i++)
        {
            oldNo[i] = o;
            newNo[i] = n;
            switch (lines[i].Type)
            {
                case ChangeType.Deleted: o++; break;
                case ChangeType.Inserted: n++; break;
                default: o++; n++; break;
            }
        }

        var changed = Enumerable.Range(0, lines.Count).Where(i => lines[i].Type is ChangeType.Inserted or ChangeType.Deleted).ToList();
        var index = 0;
        while (index < changed.Count)
        {
            var start = Math.Max(0, changed[index] - context);
            var last = changed[index];
            index++;
            while (index < changed.Count && changed[index] - last <= 2 * context + 1)
                last = changed[index++];
            var end = Math.Min(lines.Count - 1, last + context);

            var oldCount = 0;
            var newCount = 0;
            for (var i = start; i <= end; i++)
            {
                if (lines[i].Type != ChangeType.Inserted) oldCount++;
                if (lines[i].Type != ChangeType.Deleted) newCount++;
            }
            var oldStart = oldCount == 0 ? oldNo[start] - 1 : oldNo[start];
            var newStart = newCount == 0 ? newNo[start] - 1 : newNo[start];
            result.Add(('@', $"@@ -{oldStart},{oldCount} +{newStart},{newCount} @@"));
            for (var i = start; i <= end; i++)
            {
                var kind = lines[i].Type switch { ChangeType.Inserted => '+', ChangeType.Deleted => '-', _ => ' ' };
                result.Add((kind, lines[i].Text ?? ""));
            }
        }
        return result;
    }
}
