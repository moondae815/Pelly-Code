namespace PellyCoder.Core.Asp;

public enum AspClientBlockKind { Script, Style }

/// <summary>
/// A client-side &lt;script&gt; or &lt;style&gt; block of a page. Lines are 1-based page lines.
/// <see cref="ContentStart"/>/<see cref="ContentEnd"/> are character positions in the LF text (end exclusive):
/// the content runs from the character after the opening tag's <c>&gt;</c> to the <c>&lt;</c> of the closing tag
/// (or to the end of the text when <see cref="Closed"/> is false).
/// <see cref="ContentStartLine"/>/<see cref="ContentEndLine"/> are the first and last lines that hold content:
/// a line break right after the opening tag, or right before the closing tag, does not count.
/// The range is empty (start after end) when the block has no content line.
/// </summary>
public sealed record AspClientBlock(
    AspClientBlockKind Kind, int OpenLine, int ContentStart, int ContentEnd,
    int ContentStartLine, int ContentEndLine, int CloseLine,
    string? Type, string? Language, string? Src, bool Closed);

internal static class AspClientBlocks
{
    public static IReadOnlyList<AspClientBlock> Find(string textLf, IReadOnlyList<AspSegment> segments)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < textLf.Length; i++)
            if (textLf[i] == '\n')
                lineStarts.Add(i + 1);

        int LineOf(int offset)
        {
            var idx = lineStarts.BinarySearch(offset);
            return idx >= 0 ? idx + 1 : ~idx;
        }

        int OffsetOf(int line, int column) =>
            Math.Min(textLf.Length, (line - 1 < lineStarts.Count ? lineStarts[line - 1] : textLf.Length) + column - 1);

        // Server-side ranges: nothing inside them is HTML.
        var server = new List<(int Start, int End)>();
        foreach (var s in segments)
        {
            if (s.Kind == AspSegmentKind.Html)
                continue;
            server.Add((OffsetOf(s.StartLine, s.StartColumn), OffsetOf(s.EndLine, s.EndColumn) + 1));
        }
        server.Sort();

        // Returns the end of the server range that holds pos, or -1.
        int ServerEnd(int pos)
        {
            foreach (var (start, end) in server)
            {
                if (start > pos) break;
                if (pos < end) return end;
            }
            return -1;
        }

        var blocks = new List<AspClientBlock>();
        var p = 0;
        while (p < textLf.Length)
        {
            var se = ServerEnd(p);
            if (se >= 0) { p = se; continue; }

            if (textLf[p] != '<') { p++; continue; }

            if (string.CompareOrdinal(textLf, p, "<!--", 0, 4) == 0)
            {
                var close = textLf.IndexOf("-->", p + 4, StringComparison.Ordinal);
                p = close < 0 ? textLf.Length : close + 3;
                continue;
            }

            var kind = TagAt(textLf, p, "script") ? AspClientBlockKind.Script
                : TagAt(textLf, p, "style") ? AspClientBlockKind.Style
                : (AspClientBlockKind?)null;
            if (kind is null) { p++; continue; }

            var name = kind == AspClientBlockKind.Script ? "script" : "style";
            var tagEnd = EndOfOpenTag(textLf, p + 1 + name.Length, ServerEnd);
            if (tagEnd < 0) break; // no '>' anywhere: the rest is a broken tag

            var contentStart = tagEnd + 1;
            var contentEnd = FindClose(textLf, contentStart, name, ServerEnd, out var closed);
            var attrs = AspPage.Attributes(textLf[(p + 1 + name.Length)..tagEnd]);
            attrs.TryGetValue("type", out var type);
            attrs.TryGetValue("language", out var language);
            attrs.TryGetValue("src", out var src);

            if (IsClient(kind.Value, attrs, type, language))
            {
                var (startLine, endLine) = ContentLines(textLf, contentStart, contentEnd, LineOf);
                var closeLine = LineOf(Math.Min(contentEnd, Math.Max(0, textLf.Length - 1)));
                blocks.Add(new AspClientBlock(kind.Value, LineOf(p), contentStart, contentEnd,
                    startLine, endLine, closeLine, type, language, src, closed));
            }

            p = closed ? contentEnd + 1 : textLf.Length;
        }
        return blocks;
    }

    private static bool TagAt(string text, int p, string name)
    {
        if (string.Compare(text, p + 1, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) != 0
            || p + 1 + name.Length > text.Length)
            return false;
        var after = p + 1 + name.Length;
        return after >= text.Length || char.IsWhiteSpace(text[after]) || text[after] is '>' or '/';
    }

    // Finds the '>' that ends an opening tag; quotes and server ranges can hold a '>'.
    private static int EndOfOpenTag(string text, int from, Func<int, int> serverEnd)
    {
        char quote = '\0';
        for (var i = from; i < text.Length; i++)
        {
            var se = serverEnd(i);
            if (se >= 0) { i = se - 1; continue; }
            var c = text[i];
            if (quote != '\0') { if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
            else if (c == '>') return i;
        }
        return -1;
    }

    private static int FindClose(string text, int from, string name, Func<int, int> serverEnd, out bool closed)
    {
        for (var i = from; i < text.Length; i++)
        {
            var se = serverEnd(i);
            if (se >= 0) { i = se - 1; continue; }
            if (text[i] == '<' && i + 1 < text.Length && text[i + 1] == '/'
                && string.Compare(text, i + 2, name, 0, name.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                var after = i + 2 + name.Length;
                if (after >= text.Length || char.IsWhiteSpace(text[after]) || text[after] == '>')
                {
                    closed = true;
                    return i;
                }
            }
        }
        closed = false;
        return text.Length;
    }

    private static bool IsClient(AspClientBlockKind kind, Dictionary<string, string> attrs, string? type, string? language)
    {
        if (attrs.TryGetValue("runat", out var runat) && runat.Trim().Equals("server", StringComparison.OrdinalIgnoreCase))
            return false;
        var t = type?.Trim().ToLowerInvariant();
        if (kind == AspClientBlockKind.Style)
            return string.IsNullOrEmpty(t) || t == "text/css";

        if (!string.IsNullOrEmpty(t) && t != "module" && !IsScriptMime(t))
            return false;
        var l = language?.Trim().ToLowerInvariant();
        return string.IsNullOrEmpty(l) || l.StartsWith("javascript", StringComparison.Ordinal)
            || l.StartsWith("jscript", StringComparison.Ordinal) || l.StartsWith("ecmascript", StringComparison.Ordinal);
    }

    private static bool IsScriptMime(string t) =>
        t.Contains("javascript", StringComparison.Ordinal) || t.Contains("jscript", StringComparison.Ordinal)
        || t.Contains("ecmascript", StringComparison.Ordinal);

    private static (int Start, int End) ContentLines(string text, int start, int end, Func<int, int> lineOf)
    {
        if (end <= start)
            return (lineOf(Math.Min(start, Math.Max(0, text.Length - 1))), lineOf(Math.Min(start, Math.Max(0, text.Length - 1))) - 1);

        var startLine = lineOf(start);
        var firstBreak = text.IndexOf('\n', start, end - start);
        if (firstBreak >= 0 && string.IsNullOrWhiteSpace(text[start..firstBreak]))
            startLine++;

        var lastBreak = text.LastIndexOf('\n', end - 1, end - start);
        var endLine = lastBreak >= 0 && string.IsNullOrWhiteSpace(text[(lastBreak + 1)..end])
            ? lineOf(lastBreak)
            : lineOf(end - 1);
        return (startLine, endLine);
    }
}
