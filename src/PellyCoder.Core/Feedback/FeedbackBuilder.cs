using System.Text;
using System.Text.RegularExpressions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Models;
using PellyCoder.Core.Rules;
using PellyCoder.Core.Testing;
using Diagnostic = PellyCoder.Core.Models.Diagnostic;

namespace PellyCoder.Core.Feedbacks;

/// <summary>Builds the retry feedback text the local model reads, fitted to a token budget.</summary>
public sealed partial class FeedbackBuilder(ITokenCounter tokens, int budget)
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

    /// <summary>Per violation <c>RULE VIOLATION: &lt;rule text&gt;\n  added: &lt;trimmed line&gt;</c>; over budget the entries are dropped from the back.</summary>
    public Feedback ForRuleViolations(IReadOnlyList<RuleViolation> violations)
    {
        var entries = violations.Select(v => $"RULE VIOLATION: {v.Rule.Text}\n  added: {v.AddedLine.Trim()}").Distinct().ToList();
        if (entries.Count == 0) return Done("RULE VIOLATION");
        violations = violations.DistinctBy(v => (v.Rule.Text, v.AddedLine.Trim())).ToList();
        for (var count = entries.Count; count > 1; count--)
        {
            var text = string.Join("\n", entries.Take(count));
            if (Fits(text)) return Done(text);
        }
        var only = violations[0];
        const string Prefix = "RULE VIOLATION: ";
        var ruleText = only.Rule.Text;
        var line = only.AddedLine.Trim();
        string Compose(string l) => $"{Prefix}{ruleText}\n  added: {l}";
        return Done(Compose(Truncate(line, Compose)));
    }

    /// <summary>
    /// <c>TESTS FAILED (n new failures)</c>, then one line per test: <c>&lt;FullName&gt;: &lt;first line of its message&gt;</c>, then
    /// <see cref="KeepRequest"/> (M3a T14: the model undid the requested change to satisfy a test that pins the old behavior).
    /// Over budget: drop entries from the back, then cut the first entry's message; the last line is kept.
    /// </summary>
    public Feedback ForTests(IReadOnlyList<TestCaseResult> failures)
    {
        var unique = failures.DistinctBy(f => f.FullName).ToList();
        var head = $"TESTS FAILED ({unique.Count} new {(unique.Count == 1 ? "failure" : "failures")})";
        static string WithKeep(string body) => body + "\n" + KeepRequest;
        if (unique.Count == 0) return Done(WithKeep(head));

        var entries = unique.Select(f => TestLine(f, FirstLine(f.Message))).ToList();
        for (var count = entries.Count; count >= 1; count--)
        {
            var text = WithKeep(head + "\n" + string.Join("\n", entries.Take(count)));
            if (Fits(text)) return Done(text);
        }
        var only = unique[0];
        var message = FirstLine(only.Message);
        if (message.Length == 0) return Done(WithKeep(head + "\n" + only.FullName));
        return Done(WithKeep(head + "\n" + TestLine(only, Truncate(message, m => WithKeep(head + "\n" + TestLine(only, m))))));
    }

    /// <summary>
    /// Test-first feedback (design §5.5): per named test that did not pass <c>TEST NOT PASSING: &lt;FullName&gt;</c>, its whole
    /// message and up to three stack frames inside the target file, then the regressions in the <see cref="ForTests"/> format,
    /// then <see cref="GreenRequest"/>. Over budget, in order: drop regressions from the back, keep only the first named test,
    /// cut its message lines from the end, drop its frames, cut its first message line; the head line and the last line stay.
    /// </summary>
    public Feedback ForGreen(IReadOnlyList<GreenFailure> green, IReadOnlyList<TestCaseResult> regressions, string targetRelativePath)
    {
        var failures = green.DistinctBy(g => g.FullName).ToList();
        if (failures.Count == 0) return Done(GreenRequest);
        var regressed = regressions.DistinctBy(r => r.FullName).ToList();
        var regressionHead = $"TESTS FAILED ({regressed.Count} new {(regressed.Count == 1 ? "failure" : "failures")})";
        var regressionLines = regressed.Select(r => TestLine(r, FirstLine(r.Message))).ToList();
        var details = failures.Select(f => DetailLines(f.Detail)).ToList();
        var frames = failures.Select(f => TargetFrames(f.StackTrace, targetRelativePath)).ToList();

        string Entry(int i, int detailCount, int frameCount, string? firstLine = null)
        {
            var lines = new List<string> { GreenHead + failures[i].FullName };
            var detail = details[i].Take(detailCount).ToList();
            if (firstLine is not null && detail.Count > 0) detail[0] = firstLine;
            lines.AddRange(detail);
            lines.AddRange(frames[i].Take(frameCount));
            return string.Join("\n", lines);
        }
        string Compose(IEnumerable<string> entries, int regressionCount)
        {
            var parts = entries.ToList();
            if (regressionCount > 0) parts.Add(regressionHead + "\n" + string.Join("\n", regressionLines.Take(regressionCount)));
            parts.Add(GreenRequest);
            return string.Join("\n", parts);
        }

        var all = Enumerable.Range(0, failures.Count).Select(i => Entry(i, details[i].Count, MaxFrames)).ToList();
        for (var count = regressionLines.Count; count >= 0; count--)
        {
            var text = Compose(all, count);
            if (Fits(text)) return Done(text);
        }
        for (var lines = details[0].Count; lines >= 1; lines--)
        {
            var text = Compose([Entry(0, lines, MaxFrames)], 0);
            if (Fits(text)) return Done(text);
        }
        for (var count = MaxFrames - 1; count >= 0; count--)
        {
            var text = Compose([Entry(0, 1, count)], 0);
            if (Fits(text)) return Done(text);
        }
        if (details[0].Count > 0)
        {
            var first = details[0][0];
            var cut = Truncate(first, l => Compose([Entry(0, 1, 0, l)], 0));
            var text = Compose([Entry(0, 1, 0, cut)], 0);
            if (Fits(text)) return Done(text);
        }
        // Even the bare head does not fit: cut the test name, never the last line.
        var name = Truncate(failures[0].FullName, n => GreenHead + n + "\n" + GreenRequest);
        return Done(GreenHead + name + "\n" + GreenRequest);
    }

    /// <summary>Last line of <see cref="ForGreen"/>: the model may not change the test (design §5.6).</summary>
    public const string GreenRequest = "The test cannot be changed. Make it pass by implementing the request.";

    private const string GreenHead = "TEST NOT PASSING: ";
    private const int MaxFrames = 3;

    // English "at M in F:line N" and Korean .NET Framework "위치: M 파일 F:줄 N" share group names, so one
    // selection path serves both. The Korean form is Windows-unverified (fixture-based only).
    [GeneratedRegex(@"^\s*(?:at (?<m>.+?) in (?<f>.+?):line (?<l>\d+)|위치:\s*(?<m>.+?)\s+파일\s+(?<f>.+?):줄\s*(?<l>\d+))\s*$")]
    private static partial Regex StackFrame();

    /// <summary>An async state machine frame: <c>Ns.Type.&lt;Method&gt;d__3.MoveNext</c>.</summary>
    [GeneratedRegex(@"^(?<type>.+)\.<(?<method>[^>]+)>d__\d+\.MoveNext$")]
    private static partial Regex AsyncFrame();

    private static List<string> DetailLines(string? detail)
    {
        var text = (detail ?? "").Replace("\r\n", "\n").Trim();
        return text.Length == 0 ? [] : text.Split('\n').Select(l => l.TrimEnd()).ToList();
    }

    /// <summary>Frames whose file is the target, as <c>  at Type.Method (target:line)</c>.</summary>
    private static List<string> TargetFrames(string? stackTrace, string targetRelativePath)
    {
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var target = targetRelativePath.Replace('\\', '/');
        var frames = new List<string>();
        foreach (var line in (stackTrace ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            if (StackFrame().Match(line) is not { Success: true } match) continue;
            var file = match.Groups["f"].Value.Replace('\\', '/');
            // Ends with the target path at a path-segment boundary (OtherDemo/Calc.cs is not Demo/Calc.cs).
            var isTarget = file.Equals(target, comparison)
                || (file.EndsWith(target, comparison) && file[file.Length - target.Length - 1] == '/');
            if (!isTarget) continue;
            frames.Add($"  at {ShortMethod(match.Groups["m"].Value)} ({targetRelativePath}:{match.Groups["l"].Value})");
            if (frames.Count == MaxFrames) break;
        }
        return frames;
    }

    /// <summary><c>Ns.Type.Method(args)</c> → <c>Type.Method</c>; an async state machine reads as its method.</summary>
    private static string ShortMethod(string method)
    {
        var open = method.IndexOf('(');
        var name = (open < 0 ? method : method[..open]).Trim();
        if (AsyncFrame().Match(name) is { Success: true } async) name = async.Groups["type"].Value + "." + async.Groups["method"].Value;
        var parts = name.Split('.');
        return parts.Length <= 2 ? name : parts[^2] + "." + parts[^1];
    }

    private static string FirstLine(string? message)
    {
        var text = (message ?? "").Replace("\r\n", "\n").Trim();
        var cut = text.IndexOf('\n');
        return (cut < 0 ? text : text[..cut]).TrimEnd();
    }

    private static string TestLine(TestCaseResult f, string first) => first.Length == 0 ? f.FullName : $"{f.FullName}: {first}";

    /// <summary>Last line of <see cref="ForTests"/>.</summary>
    public const string KeepRequest = "Keep the requested change; do not undo it to make tests pass.";

    /// <summary>The patched target reads the same as the original target (whitespace-insensitive, M3a T14).</summary>
    public Feedback ForNoChange() => Done("NO CHANGE: the edit leaves the target identical to the original. Make the requested change.");

    /// <summary>
    /// <c>TESTS FAILED (exit n)</c>: the test command reports only its exit code (design R7), then <see cref="KeepRequest"/>
    /// (the same undo evasion applies without per-test results).
    /// </summary>
    public Feedback ForTestExitCode(int exitCode) => Done($"TESTS FAILED (exit {exitCode})\n{KeepRequest}");

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
