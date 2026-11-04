using System.Text.RegularExpressions;

namespace PellyCoder.Core.Asp;

/// <summary>
/// The VBScript text <c>cscript</c> compiles for a page (design §4.5.7). It has exactly the page's lines, so an error line
/// is a page line: HTML, directives, includes and metadata become blank, <c>&lt;%= e %&gt;</c> becomes
/// <c>Response.Write (e)</c>, code and server script stay as they are, and pieces sharing a line are joined with
/// <c> : </c>. A <c>WScript.Quit 0</c> stops the script before its first statement runs; VBScript compiles the whole
/// script first, so compile errors are still reported.
/// </summary>
public static partial class VbsCompileScript
{
    private const string Quit = "WScript.Quit 0";

    public static string Build(AspPage page, string textLf)
    {
        var lines = new List<string>[page.LineCount];
        for (var i = 0; i < lines.Length; i++)
            lines[i] = [];

        var lineStarts = LineStarts(textLf);
        foreach (var segment in page.Segments)
        {
            var fragments = segment.Kind switch
            {
                AspSegmentKind.Code => Fragments(segment.Text),
                AspSegmentKind.Output => OutputFragments(segment.Text),
                AspSegmentKind.ServerScript when IsVbScript(segment, textLf, lineStarts) => Fragments(segment.Text),
                _ => null,
            };
            if (fragments is null)
                continue;
            var firstLine = ContentLine(segment, textLf, lineStarts);
            for (var k = 0; k < fragments.Count; k++)
            {
                var index = firstLine - 1 + k;
                if (index < lines.Length && fragments[k].Length > 0)
                    lines[index].Add(fragments[k]);
            }
        }

        var built = lines.Select(pieces => string.Join(" : ", pieces)).ToArray();
        AddQuit(page, built);
        return string.Join('\n', built);
    }

    /// <summary>After <c>Option Explicit</c> (which must stay the first statement), otherwise before everything else.</summary>
    private static void AddQuit(AspPage page, string[] built)
    {
        if (built.Length == 0)
            return;
        if (page.OptionExplicit)
        {
            for (var i = 0; i < built.Length; i++)
            {
                var match = OptionExplicitPattern().Match(built[i]);
                if (!match.Success)
                    continue;
                built[i] = built[i].Insert(match.Index + match.Length, " : " + Quit);
                return;
            }
        }
        built[0] = built[0].Length == 0 ? Quit : Quit + " : " + built[0];
    }

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
    /// Drops a <c>'</c> comment (outside string literals) so a following piece joined with <c> : </c> is not commented
    /// out. Strings never span lines in VBScript, so a line is enough context.
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

    [GeneratedRegex(@"\bOption\s+Explicit\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OptionExplicitPattern();
}
