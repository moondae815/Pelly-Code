using Acornima;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Web;

public enum WebTargetUnit { Function, Rule, Lines }

/// <summary>State of a <c>pelly js</c> or <c>pelly css</c> run (set by JS/CSS LOCATE; null for other runs).</summary>
public sealed class WebRunState(
    EditKind kind, WebTargetUnit unit, TextSpanInfo originalSpan, string originalText, string encodingName, bool originalAscii,
    string siteRoot, Lazy<WebSiteIndexResult> siteIndex)
{
    /// <summary><see cref="EditKind.Js"/> or <see cref="EditKind.Css"/>.</summary>
    public EditKind Kind { get; } = kind;
    public WebTargetUnit Unit { get; } = unit;
    public TextSpanInfo OriginalSpan { get; } = originalSpan;

    /// <summary>The target span in the current text; SLICE re-finds it after each write.</summary>
    public TextSpanInfo CurrentSpan { get; set; } = originalSpan;

    /// <summary>The original file text (LF line ends); the baseline every check compares with.</summary>
    public string OriginalText { get; } = originalText;

    /// <summary>Set when the run edits a page: the target block and the original views. Null for <c>.js</c> and <c>.css</c> files.</summary>
    public WebPageState? Page { get; init; }

    /// <summary>
    /// The target block's view of a page text (LF line ends); the input itself when the run is not on a page.
    /// Null when the page no longer has the same blocks (count or kind).
    /// </summary>
    public string? ViewOf(string pageTextLf)
    {
        if (Page is null)
            return pageTextLf;
        var page = AspPage.Parse(pageTextLf);
        var original = Page.OriginalPage.ClientBlocks;
        if (page.ClientBlocks.Count != original.Count || Page.BlockIndex >= original.Count)
            return null;
        for (var i = 0; i < original.Count; i++)
            if (page.ClientBlocks[i].Kind != original[i].Kind)
                return null;
        return PageBlockView.Build(page, pageTextLf, Page.BlockIndex).Text;
    }

    /// <summary>The target function in the original file; null unless <see cref="Unit"/> is Function.</summary>
    public JsFunction? Function { get; init; }

    /// <summary>The target function's name and parameters may change: a spec js task or <c>pelly js --signature</c> (W3 design §4.2).</summary>
    public bool AllowSignatureChange { get; init; }

    /// <summary>The target function's signature before and after the last accepted patch; null when it kept its signature.</summary>
    public (JsSignature Before, JsSignature After)? SignatureChange { get; set; }

    /// <summary>The target rule in the original sheet; null unless <see cref="Unit"/> is Rule.</summary>
    public CssRule? Rule { get; init; }

    /// <summary><c>CP949</c> or <c>UTF-8</c>.</summary>
    public string EncodingName { get; } = encodingName;

    /// <summary>Every byte of the original file is below 0x80.</summary>
    public bool OriginalAscii { get; } = originalAscii;

    public string SiteRoot { get; } = siteRoot;

    /// <summary>Top-level JS names of the site; built (or read from the cache) on first use.</summary>
    public Lazy<WebSiteIndexResult> SiteIndex { get; } = siteIndex;

    // ---- JS ----

    /// <summary>The original file parsed at <see cref="OriginalLevel"/>; null when it does not parse (<see cref="Fallback"/>).</summary>
    public JsDocument? OriginalJs { get; init; }

    /// <summary>The original does not parse: checks fall back to bracket/string/comment pairing.</summary>
    public bool Fallback { get; init; }

    /// <summary>「원본이 해석되지 않음: 줄 N」 when <see cref="Fallback"/>.</summary>
    public string? FallbackReason { get; init; }

    public bool Module { get; init; }

    /// <summary>Lowest level the original parses at; null in fallback mode.</summary>
    public EcmaVersion? OriginalLevel { get; init; }

    /// <summary>Level the edited file must parse at: the original level, raised to <c>web.jsTarget</c> when that is higher.</summary>
    public EcmaVersion? TargetLevel { get; init; }

    public IReadOnlyList<JqueryFound> JqueryFound { get; init; } = [];

    /// <summary>jQuery baseline version: <c>web.jqueryVersion</c>, else the lowest found; null when unknown.</summary>
    public Version? Jquery { get; init; }

    /// <summary><c>setting</c>, <c>page</c> or <c>site</c>: where <see cref="Jquery"/> came from.</summary>
    public string JquerySource { get; init; } = "site";

    /// <summary>
    /// Names the target page's scripts can see (other blocks, includes, loaded <c>.js</c>; the union over the includers for a
    /// shared <c>.inc</c>). Null when the run is not on a page.
    /// </summary>
    public IReadOnlySet<string>? PageNames { get; init; }

    // ---- CSS ----

    public CssSheet? OriginalCss { get; init; }

    // ---- counters ----

    public int LevelRejections { get; set; }
    public int NameWarnings { get; set; }
    public int ApiWarnings { get; set; }
    public int PropertyWarnings { get; set; }

    /// <summary>Form field warnings of the last verify (W3 design §5.3).</summary>
    public int FormFieldWarnings { get; set; }

    /// <summary>The form field mismatches of the last verify (the apply end compares them with what is left).</summary>
    public IReadOnlyList<PellyCoder.Core.Web.FormMismatch> FormFieldMismatches { get; set; } = [];

    /// <summary>Latest result of each check by name.</summary>
    public Dictionary<string, AspCheck> Checks { get; } = new(StringComparer.Ordinal);

    public void Record(string name, AspCheckStatus status, string? detail = null) =>
        Checks[name] = new AspCheck(name, status, detail);

    public IReadOnlyList<EditableRegion> EditableRegions() => [new(CurrentSpan, "target")];
}
