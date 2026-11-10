using PellyCoder.Core.Asp;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Web;

public enum WebTargetFind { Found, NotFound, Ambiguous }

/// <summary>
/// Finds a <c>js</c> or <c>css</c> spec task's target the way <c>pelly js --function</c> and <c>pelly css --selector</c> do
/// (W3 design §3.3): in a <c>.js</c>/<c>.css</c> file, or in each editable script/style block of a page.
/// </summary>
public static class WebTargetLookup
{
    /// <summary>A function whose qualified name or bare name is <paramref name="name"/> (the <c>JsLocatePhase</c> rule).</summary>
    public static WebTargetFind Function(string textLf, bool isPage, string name)
    {
        var count = JsPieces(textLf, isPage).Sum(piece => Functions(piece).Count(f => f.QualifiedName == name || f.Name == name));
        return Of(count);
    }

    /// <summary>A rule that <see cref="CssSelector.Matches"/> <paramref name="selector"/> (the <c>CssLocatePhase</c> rule).</summary>
    public static WebTargetFind Rule(string textLf, bool isPage, string selector)
    {
        var count = CssPieces(textLf, isPage).Sum(piece => CssSheet.Parse(piece).Rules.Count(r => !r.IsAtRule && CssSelector.Matches(r, selector)));
        return Of(count);
    }

    /// <summary>True when <paramref name="lines"/> lies inside one editable script (js) or style (css) block of the page.</summary>
    public static bool LinesInOneBlock(AspPage page, TextSpanInfo lines, EditKind kind)
    {
        var first = page.ClientBlockAt(lines.StartLine);
        var last = page.ClientBlockAt(lines.EndLine);
        if (first is null || last is null || first.Value.Index != last.Value.Index) return false;
        return kind == EditKind.Css ? IsStyle(first.Value.Block) : PageBlockView.IsJavaScriptBlock(first.Value.Block);
    }

    /// <summary>The page's editable JS block views, or the whole file for a <c>.js</c>.</summary>
    internal static IEnumerable<string> JsPieces(string textLf, bool isPage)
    {
        if (!isPage) { yield return textLf; yield break; }
        var page = AspPage.Parse(textLf);
        for (var i = 0; i < page.ClientBlocks.Count; i++)
            if (PageBlockView.IsJavaScriptBlock(page.ClientBlocks[i]))
                yield return PageBlockView.Build(page, textLf, i).Text;
    }

    private static IEnumerable<string> CssPieces(string textLf, bool isPage)
    {
        if (!isPage) { yield return textLf; yield break; }
        var page = AspPage.Parse(textLf);
        for (var i = 0; i < page.ClientBlocks.Count; i++)
            if (IsStyle(page.ClientBlocks[i]))
                yield return PageBlockView.Build(page, textLf, i).Text;
    }

    /// <summary>The parsed functions when the piece parses at its own level, else the token-scan declarations (W1 fallback).</summary>
    internal static IReadOnlyList<JsFunction> Functions(string piece)
    {
        var detected = JsLevels.Detect(piece);
        return detected.Level is { } level
            ? JsDocument.Parse(piece, level, detected.Module).Functions
            : JsTokenScan.FunctionDeclarations(piece);
    }

    private static bool IsStyle(AspClientBlock block) => block.Kind == AspClientBlockKind.Style && block.Closed;

    private static WebTargetFind Of(int count) => count switch
    {
        0 => WebTargetFind.NotFound,
        1 => WebTargetFind.Found,
        _ => WebTargetFind.Ambiguous,
    };
}
