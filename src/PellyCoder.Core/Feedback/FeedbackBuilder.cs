using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Models;
using Diagnostic = PellyCoder.Core.Models.Diagnostic;

namespace PellyCoder.Core.Feedbacks;

/// <summary>Builds the retry feedback text the local model reads, fitted to a token budget.</summary>
public sealed class FeedbackBuilder(ITokenCounter tokens, int budget)
{
    private const int MaxRadius = 3;
    private const string Ellipsis = "…";

    public Feedback ForDiagnostics(string header, IReadOnlyList<Diagnostic> errors, string fileTextLf)
    {
        var unique = errors.DistinctBy(e => (e.Line, e.Code, e.Message)).ToList();
        if (unique.Count == 0) return Done(header + " (0 errors)");

        var lines = fileTextLf.Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        var head = $"{header} ({unique.Count} {(unique.Count == 1 ? "error" : "errors")})";

        string Compose(int count, int radius, string? firstMessage)
        {
            var entries = new List<string>();
            for (var i = 0; i < count; i++)
                entries.Add(Entry(unique[i], lines, i == 0 ? radius : MaxRadius, i == 0 ? firstMessage : null));
            return head + "\n" + string.Join("\n\n", entries);
        }

        for (var count = unique.Count; count >= 1; count--)
        {
            var text = Compose(count, MaxRadius, null);
            if (Fits(text)) return Done(text);
        }
        for (var radius = MaxRadius - 1; radius >= 0; radius--)
        {
            var text = Compose(1, radius, null);
            if (Fits(text)) return Done(text);
        }
        var msg = unique[0].Message;
        var cut = Truncate(msg, m => Compose(1, 0, m));
        return Done(Compose(1, 0, cut));
    }

    public Feedback ForPatchFailure(PatchFailure failure)
    {
        switch (failure.Kind)
        {
            case PatchFailureKinds.NotFound:
                const string Prefix = "SEARCH NOT FOUND: the SEARCH block does not match the [EDITABLE] text exactly.";
                if (failure.NearestOriginal is null) return Fit(Prefix);
                var near = Truncate(failure.NearestOriginal, n => Prefix + " Nearest original lines:\n" + n);
                return Done(Prefix + " Nearest original lines:\n" + near);
            case PatchFailureKinds.Ambiguous:
                return Fit("SEARCH AMBIGUOUS: the SEARCH block matches more than one place. Include more surrounding lines.");
            case PatchFailureKinds.OutsideRegion:
                return Fit("SEARCH OUTSIDE EDITABLE REGION: copy SEARCH lines only from [EDITABLE: target] or [EDITABLE: usings].");
            case PatchFailureKinds.Overlap:
                return Fit("BLOCKS OVERLAP: each SEARCH must refer to original lines not changed by an earlier block.");
            case PatchFailureKinds.Parse:
                return Done("FORMAT ERROR: " + Truncate(failure.Detail, d => $"FORMAT ERROR: {d}. Output only SEARCH/REPLACE blocks.")
                    + ". Output only SEARCH/REPLACE blocks.");
            case PatchFailureKinds.Unencodable:
                return Done("ENCODING ERROR: " + Truncate(failure.Detail, d => $"ENCODING ERROR: {d}. The file encoding cannot store this character; use only characters it supports.")
                    + ". The file encoding cannot store this character; use only characters it supports.");
            default:
                throw new ArgumentOutOfRangeException(nameof(failure), failure.Kind, "Unknown patch failure kind.");
        }
    }

    private static string Entry(Diagnostic e, List<string> lines, int radius, string? messageOverride)
    {
        var sb = new StringBuilder();
        sb.Append(e.File).Append('(').Append(e.Line).Append("): ").Append(e.Code).Append(' ').Append(messageOverride ?? e.Message);
        var first = Math.Max(1, e.Line - radius);
        var last = Math.Min(lines.Count, e.Line + radius);
        if (first > last) return sb.ToString();
        var width = last.ToString().Length;
        for (var n = first; n <= last; n++)
            sb.Append('\n').Append(n == e.Line ? "> " : "  ").Append(n.ToString().PadLeft(width)).Append(" | ").Append(lines[n - 1]);
        return sb.ToString();
    }

    private bool Fits(string text) => tokens.Count(text) <= budget;

    private Feedback Fit(string text) => Done(Fits(text) ? text : TruncateWhole(text));

    private string TruncateWhole(string text) => Truncate(text, s => s);

    /// <summary>Longest prefix of <paramref name="part"/> (plus an ellipsis when cut) whose composed text fits the budget.</summary>
    private string Truncate(string part, Func<string, string> compose)
    {
        if (Fits(compose(part))) return part;
        int lo = 0, hi = part.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Fits(compose(part[..mid].TrimEnd() + Ellipsis))) lo = mid; else hi = mid - 1;
        }
        return part[..lo].TrimEnd() + Ellipsis;
    }

    private Feedback Done(string text) => new(text, tokens.Count(text));
}
