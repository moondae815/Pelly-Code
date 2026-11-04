namespace PellyCoder.Core.Asp;

/// <summary>
/// A compile script and where each of its lines came from: <see cref="PageLines"/>[n - 1] is the page line of script
/// line n. <see cref="LastPageLine"/> is the page's last line, where an error reported past the script's end belongs.
/// </summary>
public sealed record VbsScript(string Text, IReadOnlyList<int> PageLines, int LastPageLine)
{
    /// <summary>The page line of a script line reported by cscript; past the end clamps to <see cref="LastPageLine"/>.</summary>
    public int PageLine(int scriptLine) =>
        scriptLine > PageLines.Count || PageLines.Count == 0 ? LastPageLine
        : scriptLine < 1 ? PageLines[0]
        : PageLines[scriptLine - 1];
}

/// <summary>
/// The VBScript text <c>cscript</c> compiles for a page (design §4.5.7). Every code fragment (one line of a code block,
/// server script or <c>&lt;%= e %&gt;</c>, which becomes <c>Response.Write (e)</c>) is its own script line, in page
/// order; HTML, directives, includes and metadata produce no line. Fragments are never joined with <c>:</c>, so block
/// statements (<c>Class</c>, <c>Sub</c>, <c>If … Then</c>) compile exactly as on the page. <c>WScript.Quit 0</c> on its
/// own line stops the script before its first statement runs: right after the <c>Option Explicit</c> statement (which
/// must stay first) when the page has one, otherwise as the first line. VBScript compiles the whole script first, so
/// compile errors are still reported; <see cref="VbsScript.PageLines"/> maps them back to page lines.
/// </summary>
public static class VbsCompileScript
{
    private const string Quit = "WScript.Quit 0";

    public static VbsScript Build(AspPage page, string textLf)
    {
        var lineStarts = LineStarts(textLf);
        var anchor = OptionExplicitEnd(page);
        var fragments = new List<(int Line, int Column, string Text, bool Anchor)>();
        foreach (var segment in page.Segments)
        {
            var pieces = segment.Kind switch
            {
                AspSegmentKind.Code => Fragments(segment.Text),
                AspSegmentKind.Output => OutputFragments(segment.Text),
                AspSegmentKind.ServerScript when IsVbScript(segment, textLf, lineStarts) => Fragments(segment.Text),
                _ => null,
            };
            if (pieces is null)
                continue;
            var firstLine = ContentLine(segment, textLf, lineStarts);
            var holdsAnchor = anchor is not null && Contains(segment, anchor);
            for (var k = 0; k < pieces.Count; k++)
            {
                var line = firstLine + k;
                if (line > page.LineCount || pieces[k].Length == 0)
                    continue;
                var column = k == 0 ? segment.StartColumn : 1;
                if (holdsAnchor && line == anchor!.Line)
                {
                    // Quit must come right after the Option Explicit statement, before anything sharing its fragment.
                    var (head, rest) = SplitAfter(page, segment, k, pieces[k], anchor);
                    fragments.Add((line, column, head, true));
                    if (rest.Length > 0)
                        fragments.Add((line, column, rest, false));
                    continue;
                }
                fragments.Add((line, column, pieces[k], false));
            }
        }

        var text = new List<string>();
        var pageLines = new List<int>();
        var quitAdded = false;
        if (anchor is null)
        {
            text.Add(Quit);
            pageLines.Add(1);
            quitAdded = true;
        }
        foreach (var fragment in fragments.OrderBy(f => f.Line).ThenBy(f => f.Column))
        {
            text.Add(fragment.Text);
            pageLines.Add(fragment.Line);
            if (fragment.Anchor && !quitAdded)
            {
                text.Add(Quit);
                pageLines.Add(fragment.Line);
                quitAdded = true;
            }
        }
        if (!quitAdded)
        {
            // Defensive: the Option Explicit statement's fragment was not found; never run the page.
            text.Insert(0, Quit);
            pageLines.Insert(0, 1);
        }
        return new VbsScript(string.Join('\n', text), pageLines, Math.Max(1, page.LineCount));
    }

    /// <summary>
    /// The last token (<c>Explicit</c>) of the first <c>Option Explicit</c> statement (comments and strings are not
    /// statements).
    /// </summary>
    private static VbToken? OptionExplicitEnd(AspPage page) =>
        page.Statements
            .Select(s => s.Tokens)
            .FirstOrDefault(t => t.Count >= 2 && IsWord(t[0], "Option") && IsWord(t[1], "Explicit"))?[1];

    /// <summary>
    /// Splits the fragment holding the <c>Option Explicit</c> statement right after its last token: the head ends with
    /// that statement, the rest (leading <c>:</c> dropped) runs only after <c>WScript.Quit 0</c>. Positions come from the
    /// parsed statement; the fragment is located in the page line, so strings and comments cannot mislead the split.
    /// </summary>
    private static (string Head, string Tail) SplitAfter(AspPage page, AspSegment segment, int k, string fragment, VbToken last)
    {
        var lineText = page.LineText(last.Line);
        var raw = segment.Text.Split('\n')[k];
        var searchFrom = k == 0 ? Math.Max(0, segment.StartColumn - 1) : 0;
        var rawIndex = raw.Length == 0 ? -1 : lineText.IndexOf(raw, Math.Min(searchFrom, lineText.Length), StringComparison.Ordinal);
        if (rawIndex < 0)
            return (fragment, "");
        var fragmentStart = rawIndex + (raw.Length - raw.TrimStart().Length); // 0-based page column of the fragment
        var split = last.Column - 1 + last.Text.Length - fragmentStart;
        if (split <= 0 || split >= fragment.Length)
            return (fragment, "");
        var rest = fragment[split..].TrimStart();
        if (rest.StartsWith(':'))
            rest = rest[1..].TrimStart();
        return (fragment[..split].TrimEnd(), rest);
    }

    private static bool IsWord(VbToken token, string text) =>
        token.Kind is VbTokenKind.Identifier or VbTokenKind.Keyword && token.Text.Equals(text, StringComparison.OrdinalIgnoreCase);

    private static bool Contains(AspSegment segment, VbToken token) =>
        (token.Line, token.Column).CompareTo((segment.StartLine, segment.StartColumn)) >= 0
        && (token.Line, token.Column).CompareTo((segment.EndLine, segment.EndColumn)) <= 0;

    /// <summary>One trimmed, comment-free fragment per line of <paramref name="code"/>.</summary>
    private static List<string> Fragments(string code) =>
        [.. code.Split('\n').Select(f => StripComment(f).Trim())];

    private static List<string> OutputFragments(string expression)
    {
        var fragments = Fragments(expression);
        var first = fragments.FindIndex(f => f.Length > 0);
        if (first < 0)
        {
            fragments[0] = "Response.Write ()";
            return fragments;
        }
        var last = fragments.FindLastIndex(f => f.Length > 0);
        fragments[first] = "Response.Write (" + fragments[first];
        fragments[last] += ")";
        return fragments;
    }

    /// <summary>
    /// Drops a <c>'</c> comment (outside string literals) so the closing parenthesis added to an output expression is
    /// not commented out. Strings never span lines in VBScript, so a line is enough context.
    /// </summary>
    private static string StripComment(string line)
    {
        var inString = false;
        for (var i = 0; i < line.Length; i++)
        {
            if (line[i] == '"')
                inString = !inString;
            else if (line[i] == '\'' && !inString)
                return line[..i];
        }
        return line;
    }

    /// <summary>Line of the first content character: the server script's content starts after its opening tag.</summary>
    private static int ContentLine(AspSegment segment, string textLf, List<int> lineStarts)
    {
        if (segment.Kind != AspSegmentKind.ServerScript)
            return segment.StartLine;
        var start = lineStarts[segment.StartLine - 1] + segment.StartColumn - 1;
        var tagEnd = textLf.IndexOf('>', start);
        return tagEnd < 0 ? segment.StartLine : LineOf(lineStarts, tagEnd + 1);
    }

    private static bool IsVbScript(AspSegment segment, string textLf, List<int> lineStarts)
    {
        var start = lineStarts[segment.StartLine - 1] + segment.StartColumn - 1;
        var tagEnd = textLf.IndexOf('>', start);
        if (tagEnd < 0)
            return true;
        var language = AspPage.Attributes(textLf[start..tagEnd]).GetValueOrDefault("language") ?? "";
        return !language.Equals("JScript", StringComparison.OrdinalIgnoreCase)
            && !language.Equals("JavaScript", StringComparison.OrdinalIgnoreCase);
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var k = 0; k < text.Length; k++)
        {
            if (text[k] == '\n')
                starts.Add(k + 1);
        }
        return starts;
    }

    private static int LineOf(List<int> lineStarts, int offset)
    {
        var index = lineStarts.BinarySearch(offset);
        return (index < 0 ? ~index - 1 : index) + 1;
    }
}
