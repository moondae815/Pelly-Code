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

    /// <summary>The target function in the original file; null unless <see cref="Unit"/> is Function.</summary>
    public JsFunction? Function { get; init; }

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

    // ---- CSS ----

    public CssSheet? OriginalCss { get; init; }

    // ---- counters ----

    public int LevelRejections { get; set; }
    public int NameWarnings { get; set; }
    public int ApiWarnings { get; set; }
    public int PropertyWarnings { get; set; }

    /// <summary>Latest result of each check by name.</summary>
    public Dictionary<string, AspCheck> Checks { get; } = new(StringComparer.Ordinal);

    public void Record(string name, AspCheckStatus status, string? detail = null) =>
        Checks[name] = new AspCheck(name, status, detail);

    public IReadOnlyList<EditableRegion> EditableRegions() => [new(CurrentSpan, "target")];
}
