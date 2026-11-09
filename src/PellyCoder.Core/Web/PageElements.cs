using System.Text.RegularExpressions;
using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Web;

public enum ElementKind { Id, Name }

/// <summary>A JS lookup of a page element by id or name. <paramref name="Line"/> is the page line.</summary>
public sealed record ElementRef(ElementKind Kind, string Value, int Line);

/// <summary>
/// An id or name the page (or a related file) defines. <paramref name="File"/> is null for the page itself, else a site-relative
/// path. A value ending in <c>*</c> is a prefix: the attribute value continues with server code (<c>id="row&lt;%= i %&gt;"</c>).
/// </summary>
public sealed record ElementDef(ElementKind Kind, string Value, string? File, int Line);

/// <summary>A file whose ids and names the page can see: an include, an includer page or a <c>&lt;script src&gt;</c> file.</summary>
public sealed record RelatedText(string File, string TextLf);

/// <summary>
/// The page element check (element check design §4): JS references to ids and names the page does not define. Only references
/// broken by the edit count — the count of broken references per (kind, value) is compared with the original page.
/// </summary>
public static class PageElements
{
    private static readonly Regex DocumentLookup = new(
        @"(?<![\w$.])document\s*\.\s*(?<m>getElementById|getElementsByName)\s*\(\s*(?<q>[""'])", RegexOptions.Compiled);
    private static readonly Regex JqueryCall = new(@"(?<![\w$.])(?:\$|jQuery)\s*\(\s*(?<q>[""'])", RegexOptions.Compiled);
    private static readonly Regex SelectorId = new(@"#(?<v>[\w-]+)", RegexOptions.Compiled);
    private static readonly Regex SelectorName = new(@"\[\s*name\s*=\s*[""']?(?<v>[\w-]+)", RegexOptions.Compiled);
    private static readonly Regex Placeholder = new(@"__asp[0-9]+__", RegexOptions.Compiled);

    private static readonly Regex Attribute = new(
        @"(?<![\w-])(?<k>id|name)\s*=\s*(?:""""|""|')?(?<v>[^""'\s>]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex PropertySet = new(
        @"\.\s*(?<k>id|name)\s*=\s*(?<q>[""'])(?<v>[\w-]+)\k<q>", RegexOptions.Compiled);
    private static readonly Regex SetAttribute = new(
        @"setAttribute\s*\(\s*[""'](?<k>id|name)[""']\s*,\s*[""'](?<v>[\w-]+)[""']", RegexOptions.Compiled);

    /// <summary>The element lookups in every inline JavaScript block of the page, in page order.</summary>
    public static IReadOnlyList<ElementRef> References(AspPage page, string pageTextLf)
    {
        var result = new List<ElementRef>();
        for (var i = 0; i < page.ClientBlocks.Count; i++)
        {
            if (!PageBlockView.IsJavaScriptBlock(page.ClientBlocks[i])) continue;
            var view = PageBlockView.Build(page, pageTextLf, i).Text;
            var mask = JsTokenScan.Mask(view);
            var starts = LineStarts(view);
            foreach (Match m in DocumentLookup.Matches(mask))
            {
                if (Literal(view, mask, m) is not { } value) continue;
                var kind = m.Groups["m"].Value == "getElementById" ? ElementKind.Id : ElementKind.Name;
                if (Regex.IsMatch(value, @"^[\w-]+$")) result.Add(new ElementRef(kind, value, LineAt(starts, m.Index)));
            }
            foreach (Match m in JqueryCall.Matches(mask))
            {
                if (Literal(view, mask, m) is not { } value || value.TrimStart().StartsWith('<')) continue;
                var line = LineAt(starts, m.Index);
                foreach (Match s in SelectorId.Matches(value)) result.Add(new ElementRef(ElementKind.Id, s.Groups["v"].Value, line));
                foreach (Match s in SelectorName.Matches(value)) result.Add(new ElementRef(ElementKind.Name, s.Groups["v"].Value, line));
            }
        }
        return result.OrderBy(r => r.Line).ToList();
    }

    /// <summary>
    /// The string literal that is the only argument of the call <paramref name="m"/> opened: null when the argument is not one plain
    /// literal (concatenation, a second argument) or holds server output.
    /// </summary>
    private static string? Literal(string view, string mask, Match m)
    {
        var quote = m.Groups["q"];
        var open = quote.Index;
        // String contents are blanked in the mask, so the next same quote closes the literal.
        var close = mask.IndexOf(quote.Value[0], open + 1);
        if (close < 0) return null;
        var after = close + 1;
        while (after < mask.Length && char.IsWhiteSpace(mask[after])) after++;
        if (after >= mask.Length || mask[after] != ')') return null;
        var value = view.Substring(open + 1, close - open - 1);
        return Placeholder.IsMatch(value) ? null : value;
    }

    /// <summary>The ids and names <paramref name="textLf"/> defines: element attributes (also inside strings) and runtime assignments.</summary>
    public static IReadOnlyList<ElementDef> Definitions(string textLf, string? file)
    {
        var result = new List<ElementDef>();
        var starts = LineStarts(textLf);
        foreach (Match m in Attribute.Matches(textLf))
        {
            var value = m.Groups["v"].Value;
            var server = value.IndexOf("<%", StringComparison.Ordinal);
            if (server >= 0) value = value[..server] + "*";
            result.Add(new ElementDef(KindOf(m), value, file, LineAt(starts, m.Index)));
        }
        foreach (var regex in new[] { PropertySet, SetAttribute })
            foreach (Match m in regex.Matches(textLf))
                result.Add(new ElementDef(KindOf(m), m.Groups["v"].Value, file, LineAt(starts, m.Index)));
        return result.Distinct().ToList();
    }

    /// <summary>One warning per (kind, value) whose broken references the edit added, in the patched page's line order.</summary>
    public static IReadOnlyList<string> NewBroken(AspPage original, string originalTextLf, AspPage patched, string patchedTextLf,
        IReadOnlyList<RelatedText> related)
    {
        var relatedDefs = related.SelectMany(r => Definitions(r.TextLf, r.File)).ToList();
        var originalDefs = Definitions(originalTextLf, null).Concat(relatedDefs).ToList();
        var patchedDefs = Definitions(patchedTextLf, null).Concat(relatedDefs).ToList();

        var before = References(original, originalTextLf).Where(r => Broken(r, originalDefs))
            .GroupBy(r => (r.Kind, r.Value)).ToDictionary(g => g.Key, g => g.Count());
        return References(patched, patchedTextLf).Where(r => Broken(r, patchedDefs))
            .GroupBy(r => (r.Kind, r.Value))
            .Where(g => g.Count() > before.GetValueOrDefault(g.Key))
            .Select(g => g.MinBy(r => r.Line)!)
            .OrderBy(r => r.Line).ThenBy(r => r.Value, StringComparer.Ordinal)
            .Select(r => Warning(r, patchedDefs))
            .ToList();
    }

    private static bool Broken(ElementRef r, IReadOnlyList<ElementDef> defs) =>
        !defs.Any(d => d.Kind == r.Kind && (d.Value == r.Value
            || (d.Value.EndsWith('*') && r.Value.StartsWith(d.Value[..^1], StringComparison.Ordinal))));

    private static string Warning(ElementRef r, IReadOnlyList<ElementDef> defs)
    {
        var kind = r.Kind == ElementKind.Id ? "id" : "name";
        var parts = new List<string> { $"{(r.Kind == ElementKind.Id ? "#" + r.Value : $"[name={r.Value}]")}(줄 {r.Line}) — {kind} 없음" };
        // Page definitions first (in line order), then related files in the order given.
        var ordered = defs.Where(d => d.File is null).OrderBy(d => d.Line).Concat(defs.Where(d => d.File is not null)).ToList();
        var other = r.Kind == ElementKind.Id ? ElementKind.Name : ElementKind.Id;
        if (ordered.FirstOrDefault(d => d.Kind == other && d.Value == r.Value) is { } o)
            parts.Add($"{(other == ElementKind.Id ? "id" : "name")}=\"{r.Value}\"는 있음({Where(o)})");
        if (ordered.FirstOrDefault(d => d.Kind == r.Kind && !d.Value.EndsWith('*') && d.Value != r.Value
                && string.Equals(d.Value, r.Value, StringComparison.OrdinalIgnoreCase)) is { } c)
            parts.Add($"대소문자만 다름: {c.Value}({Where(c)})");
        return string.Join(", ", parts);
    }

    private static string Where(ElementDef d) => d.File is null ? $"줄 {d.Line}" : $"{d.File}:{d.Line}";

    private static ElementKind KindOf(Match m) =>
        string.Equals(m.Groups["k"].Value, "id", StringComparison.OrdinalIgnoreCase) ? ElementKind.Id : ElementKind.Name;

    /// <summary>Start offset of every line; built once per text so each match costs a binary search, not a rescan.</summary>
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
