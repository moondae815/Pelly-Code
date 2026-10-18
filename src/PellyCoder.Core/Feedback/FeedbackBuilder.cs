using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Models;
using PellyCoder.Core.Testing;
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

        string Compose(int count, int radius, Shrink shrink = default)
        {
            var entries = new List<string>();
            for (var i = 0; i < count; i++)
                entries.Add(Entry(unique[i], lines, i == 0 ? radius : MaxRadius, i == 0 ? shrink : default));
            return head + "\n" + string.Join("\n\n", entries);
        }

        for (var count = unique.Count; count >= 1; count--)
        {
            var text = Compose(count, MaxRadius);
            if (Fits(text)) return Done(text);
        }
        for (var radius = MaxRadius - 1; radius >= 0; radius--)
        {
            var text = Compose(1, radius);
            if (Fits(text)) return Done(text);
        }
        return Done(FitFirstEntry(unique[0].Message, s => Compose(1, 0, s)));
    }

    /// <summary>
    /// <c>BUILD FAILED (n new errors)</c>: errors in the edited file first, each with ±3 lines of context, then errors in
    /// other files one line each. Over budget: drop entries from the back, then shrink the first entry's context, then
    /// cut its message.
    /// </summary>
    public Feedback ForBuild(IReadOnlyList<Diagnostic> newErrors, string targetRelativePath, string targetTextLf)
    {
        var pathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        bool IsTarget(Diagnostic e) => e.File.Length > 0 && string.Equals(e.File, targetRelativePath, pathComparison);

        var unique = newErrors.DistinctBy(e => (e.File, e.Line, e.Code, e.Message)).ToList();
        var ordered = unique.Where(IsTarget).Concat(unique.Where(e => !IsTarget(e))).ToList();
        var head = $"BUILD FAILED ({ordered.Count} new {(ordered.Count == 1 ? "error" : "errors")})";
        if (ordered.Count == 0) return Done(head);

        var lines = targetTextLf.Split('\n').ToList();
        if (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        string Compose(int count, int radius, Shrink shrink = default)
        {
            var sb = new StringBuilder(head);
            for (var i = 0; i < count; i++)
            {
                var e = ordered[i];
                var s = i == 0 ? shrink : default;
                if (IsTarget(e))
                    sb.Append(i == 0 ? "\n" : "\n\n").Append(Entry(e, lines, i == 0 ? radius : MaxRadius, s));
                else
                    sb.Append(i > 0 && IsTarget(ordered[i - 1]) ? "\n\n" : "\n").Append(OneLine(e, s));
            }
            return sb.ToString();
        }

        for (var count = ordered.Count; count >= 1; count--)
        {
            var text = Compose(count, MaxRadius);
            if (Fits(text)) return Done(text);
        }
        for (var radius = MaxRadius - 1; radius >= 0; radius--)
        {
            var text = Compose(1, radius);
            if (Fits(text)) return Done(text);
        }
        return Done(FitFirstEntry(ordered[0].Message, s => Compose(1, 0, s)));
    }

    /// <summary>How the first (only remaining) entry is cut once dropping entries and context lines was not enough.</summary>
    private readonly record struct Shrink(string? Message = null, int? LineCap = null, bool ShortPath = false);

    private static string OneLine(Diagnostic e, Shrink s)
    {
        var file = s.ShortPath ? FileNameOf(e.File) : e.File;
        var message = s.Message ?? e.Message;
        return file.Length == 0 ? $"{e.Code} {message}" : $"{file}({e.Line}): {e.Code} {message}";
    }

    private static string FileNameOf(string file)
    {
        var cut = file.LastIndexOfAny(['/', '\\']);
        return cut < 0 ? file : file[(cut + 1)..];
    }

    /// <summary>
    /// Last-resort shrink of a single entry, in order: cut the context lines (error line included) from their end keeping the
    /// full message, then use the bare file name, then cut the message. A message cut alone is tried first because it keeps the file path and context. The caller's <see cref="Done"/> still guarantees the budget.
    /// </summary>
    private string FitFirstEntry(string message, Func<Shrink, string> compose)
    {
        var cutMessage = Truncate(message, m => compose(new Shrink(m)));
        var byMessage = compose(new Shrink(cutMessage));
        if (Fits(byMessage)) return byMessage;
        foreach (var shortPath in new[] { false, true })
        {
            if (!Fits(compose(new Shrink(null, 0, shortPath)))) continue;
            int lo = 0, hi = 1 << 20;
            while (lo < hi)
            {
                var mid = lo + (hi - lo + 1) / 2;
                if (Fits(compose(new Shrink(null, mid, shortPath)))) lo = mid; else hi = mid - 1;
            }
            return compose(new Shrink(null, lo, shortPath));
        }
        var cut = Truncate(message, m => compose(new Shrink(m, 0, true)));
        return compose(new Shrink(cut, 0, true));
    }

    public Feedback ForPatchFailure(PatchFailure failure)
    {
        switch (failure.Kind)
        {
            case PatchFailureKinds.NotFound:
                const string Prefix = "SEARCH NOT FOUND: the SEARCH block does not match the [EDITABLE] text exactly.";
                if (failure.NearestOriginal is null) return Done(Prefix);
                var near = Truncate(failure.NearestOriginal, n => Prefix + " Nearest original lines:\n" + n);
                return Done(Prefix + " Nearest original lines:\n" + near);
            case PatchFailureKinds.Ambiguous:
                return Done("SEARCH AMBIGUOUS: the SEARCH block matches more than one place. Include more surrounding lines.");
            case PatchFailureKinds.OutsideRegion:
                return Done("SEARCH OUTSIDE EDITABLE REGION: copy SEARCH lines only from [EDITABLE: target], [EDITABLE: usings] or [EDITABLE: helper].");
            case PatchFailureKinds.Overlap:
                return Done("BLOCKS OVERLAP: each SEARCH must refer to original lines not changed by an earlier block.");
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

    /// <summary><c>SHAPE REJECTED: </c> and the guard's reason, cut to the budget.</summary>
    public Feedback ForShapeRejection(string reason)
    {
        const string Prefix = "SHAPE REJECTED: ";
        return Done(Prefix + Truncate(reason, r => Prefix + r));
    }

    /// <summary><c>SQL SAFETY REJECTED: </c> and the guard's reason, cut to the budget.</summary>
    public Feedback ForSqlSafety(string reason)
    {
        const string Prefix = "SQL SAFETY REJECTED: ";
        return Done(Prefix + Truncate(reason, r => Prefix + r));
    }

    /// <summary>
    /// <c>TESTS FAILED (n new failures)</c>, then one line per test: <c>&lt;FullName&gt;: &lt;first line of its message&gt;</c>.
    /// Over budget: drop entries from the back, then cut the first entry's message.
    /// </summary>
    public Feedback ForTests(IReadOnlyList<TestCaseResult> failures)
    {
        var unique = failures.DistinctBy(f => f.FullName).ToList();
        var head = $"TESTS FAILED ({unique.Count} new {(unique.Count == 1 ? "failure" : "failures")})";
        if (unique.Count == 0) return Done(head);

        static string FirstLine(string? message)
        {
            var text = (message ?? "").Replace("\r\n", "\n").Trim();
            var cut = text.IndexOf('\n');
            return (cut < 0 ? text : text[..cut]).TrimEnd();
        }
        static string TestLine(TestCaseResult f, string first) => first.Length == 0 ? f.FullName : $"{f.FullName}: {first}";

        var entries = unique.Select(f => TestLine(f, FirstLine(f.Message))).ToList();
        for (var count = entries.Count; count >= 1; count--)
        {
            var text = head + "\n" + string.Join("\n", entries.Take(count));
            if (Fits(text)) return Done(text);
        }
        var only = unique[0];
        var message = FirstLine(only.Message);
        if (message.Length == 0) return Done(head + "\n" + only.FullName);
        return Done(head + "\n" + TestLine(only, Truncate(message, m => head + "\n" + TestLine(only, m))));
    }

    /// <summary><c>TESTS FAILED (exit n)</c>: the test command reports only its exit code (design R7).</summary>
    public Feedback ForTestExitCode(int exitCode) => Done($"TESTS FAILED (exit {exitCode})");

    private static string Entry(Diagnostic e, List<string> lines, int radius, Shrink shrink)
    {
        var sb = new StringBuilder();
        sb.Append(shrink.ShortPath ? FileNameOf(e.File) : e.File).Append('(').Append(e.Line).Append("): ").Append(e.Code).Append(' ').Append(shrink.Message ?? e.Message);
        var first = Math.Max(1, e.Line - radius);
        var last = Math.Min(lines.Count, e.Line + radius);
        if (first > last) return sb.ToString();
        var width = last.ToString().Length;
        for (var n = first; n <= last; n++)
            sb.Append('\n').Append(n == e.Line ? "> " : "  ").Append(n.ToString().PadLeft(width)).Append(" | ").Append(CapLine(lines[n - 1], shrink.LineCap));
        return sb.ToString();
    }

    private static string CapLine(string line, int? cap)
    {
        if (cap is null || line.Length <= cap) return line;
        return CutPrefix(line, cap.Value).TrimEnd() + Ellipsis;
    }

    /// <summary>First <paramref name="length"/> chars, never ending between the halves of a surrogate pair.</summary>
    private static string CutPrefix(string text, int length)
    {
        if (length >= text.Length) return text;
        if (length > 0 && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length])) length--;
        return text[..length];
    }

    private bool Fits(string text) => tokens.Count(text) <= budget;

    /// <summary>Longest prefix of <paramref name="part"/> (plus an ellipsis when cut) whose composed text fits the budget.</summary>
    private string Truncate(string part, Func<string, string> compose)
    {
        if (Fits(compose(part))) return part;
        int lo = 0, hi = part.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) / 2;
            if (Fits(compose(CutPrefix(part, mid).TrimEnd() + Ellipsis))) lo = mid; else hi = mid - 1;
        }
        return CutPrefix(part, lo).TrimEnd() + Ellipsis;
    }

    /// <summary>
    /// The one place every public method ends in, so <c>Tokens &lt;= budget</c> holds for any input: text that still does
    /// not fit is cut as a whole and ends with an ellipsis (empty only when even the ellipsis exceeds the budget).
    /// </summary>
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
