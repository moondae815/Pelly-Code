using System.Text.RegularExpressions;
using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Web;

public enum JsCallSource { Js, PageScript, Attribute }

/// <summary>
/// One JS call (W3 design §4.3). <paramref name="Name"/> is the whole dotted name before the parenthesis (<c>fnX</c>,
/// <c>Common.fnX</c>). <paramref name="Arguments"/> is null when the parentheses do not balance (「모름」) or for a
/// <paramref name="Reference"/> (the name passed on without a call). <paramref name="AttributeName"/> is the HTML attribute of an
/// <see cref="JsCallSource.Attribute"/> call: <c>onclick</c>, <c>javascript:</c>.
/// </summary>
public sealed record JsCallSite(string Name, int Line, int? Arguments, bool Reference, JsCallSource Source, string? AttributeName = null);

/// <summary>Finds JS calls in script text (strings and comments masked), in a page's script blocks and in its event attributes.</summary>
public static class JsCallFinder
{
    private static readonly Regex Call = new(
        @"(?<![\w$.])(?<name>[A-Za-z_$][\w$]*(?:\s*\.\s*[A-Za-z_$][\w$]*)*)\s*\(", RegexOptions.Compiled);

    private static readonly Regex EventAttribute = new(
        @"(?<![\w-])(?<attr>on[a-z]+)\s*=\s*(?<q>""""|""|')(?<v>.*?)\k<q>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex JavascriptHref = new(
        @"(?<![\w-])href\s*=\s*(?<q>""""|""|')\s*javascript:(?<v>.*?)\k<q>", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex FunctionKeyword = new(@"\bfunction\s*$", RegexOptions.Compiled);

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "if", "for", "while", "switch", "catch", "function", "return", "typeof", "with", "do", "else", "void", "delete", "in",
        "instanceof", "new", "throw", "case", "var", "let", "const",
    };

    /// <summary>The calls in <paramref name="textLf"/>; a declaration <c>function name(</c> is not a call, <c>new Name(</c> is.</summary>
    public static IReadOnlyList<JsCallSite> InScript(string textLf, JsCallSource source)
    {
        var mask = JsTokenScan.Mask(textLf);
        var starts = LineStarts(mask);
        var result = new List<JsCallSite>();
        foreach (Match m in Call.Matches(mask))
        {
            var name = Regex.Replace(m.Groups["name"].Value, @"\s+", "");
            if (Keywords.Contains(name) || PageBlockView.IsPlaceholder(name)) continue;
            var from = Math.Max(0, m.Index - 20);
            if (FunctionKeyword.IsMatch(mask.Substring(from, m.Index - from))) continue;
            var open = m.Index + m.Length - 1;
            result.Add(new JsCallSite(name, LineAt(starts, m.Index), CountArguments(mask, open), false, source));
        }
        return result;
    }

    /// <summary>
    /// Places that pass <paramref name="name"/> on without calling it: <c>setTimeout(fnX, 10)</c>, <c>b.onclick = fnX;</c>,
    /// <c>.click(fnX)</c> — the name after <c>(</c>, <c>,</c> or <c>=</c> and before <c>,</c>, <c>)</c>, <c>;</c> or a line end.
    /// </summary>
    public static IReadOnlyList<JsCallSite> References(string textLf, string name, JsCallSource source)
    {
        var mask = JsTokenScan.Mask(textLf);
        var starts = LineStarts(mask);
        var pattern = string.Join(@"\s*\.\s*", name.Split('.').Select(Regex.Escape));
        var result = new List<JsCallSite>();
        foreach (Match m in Regex.Matches(mask, @"(?<![\w$.])" + pattern + @"(?![\w$])"))
        {
            var before = m.Index - 1;
            while (before >= 0 && mask[before] is ' ' or '\t') before--;
            var after = m.Index + m.Length;
            while (after < mask.Length && mask[after] is ' ' or '\t') after++;
            var prevOk = before >= 0 && mask[before] is '(' or ',' or '=' && !(mask[before] == '=' && before > 0 && mask[before - 1] is '=' or '!');
            var nextOk = after >= mask.Length || mask[after] is ',' or ')' or ';' or '\n' or '\r';
            if (prevOk && nextOk) result.Add(new JsCallSite(name, LineAt(starts, m.Index), null, true, source));
        }
        return result;
    }

    /// <summary>The calls in the page's JavaScript blocks (page lines) and in its event attributes and <c>javascript:</c> links.</summary>
    public static IReadOnlyList<JsCallSite> InPage(AspPage page, string textLf)
    {
        var result = new List<JsCallSite>();
        for (var i = 0; i < page.ClientBlocks.Count; i++)
            if (PageBlockView.IsJavaScriptBlock(page.ClientBlocks[i]))
                result.AddRange(InScript(PageBlockView.Build(page, textLf, i).Text, JsCallSource.PageScript));
        result.AddRange(InAttributes(textLf, null).Select(a => a.Call));
        return result.OrderBy(c => c.Line).ToList();
    }

    /// <summary>The attribute values of the page holding JS, with their calls (or, with <paramref name="referenceTo"/>, its references).</summary>
    internal static IEnumerable<(string Value, JsCallSite Call)> InAttributes(string textLf, string? referenceTo)
    {
        var starts = LineStarts(textLf);
        foreach (var (regex, fixedName) in new[] { (EventAttribute, (string?)null), (JavascriptHref, "javascript:") })
        {
            foreach (Match m in regex.Matches(textLf))
            {
                var value = m.Groups["v"].Value;
                var attr = fixedName ?? m.Groups["attr"].Value.ToLowerInvariant();
                var line = LineAt(starts, m.Groups["v"].Index);
                var found = referenceTo is null ? InScript(value, JsCallSource.Attribute) : References(value, referenceTo, JsCallSource.Attribute);
                foreach (var c in found)
                    yield return (value, c with { Line = line + c.Line - 1, AttributeName = attr });
            }
        }
    }

    /// <summary>Top-level commas between the parenthesis at <paramref name="open"/> and its match, plus one; 0 for empty; null when unbalanced.</summary>
    private static int? CountArguments(string mask, int open)
    {
        var depth = 0;
        var commas = 0;
        var any = false;
        for (var i = open + 1; i < mask.Length; i++)
        {
            var c = mask[i];
            switch (c)
            {
                case '(' or '[' or '{': depth++; any = true; break;
                case ')' or ']' or '}':
                    if (depth == 0) return c == ')' ? (any ? commas + 1 : 0) : null;
                    depth--;
                    break;
                case ',' when depth == 0: commas++; any = true; break;
                default:
                    if (!char.IsWhiteSpace(c)) any = true;
                    break;
            }
        }
        return null;
    }

    private static List<int> LineStarts(string text)
    {
        var starts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
            if (text[i] == '\n') starts.Add(i + 1);
        return starts;
    }

    private static int LineAt(List<int> starts, int index)
    {
        var found = starts.BinarySearch(index);
        return found >= 0 ? found + 1 : ~found;
    }
}
