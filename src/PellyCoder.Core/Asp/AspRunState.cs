using PellyCoder.Core.Models;

namespace PellyCoder.Core.Asp;

public enum AspTargetUnit { Procedure, Lines }

public enum AspCheckStatus { Passed, Warned, Skipped, Failed }

/// <summary>Result of one ASP check, shown in the review and the run summary.</summary>
public sealed record AspCheck(string Name, AspCheckStatus Status, string? Detail);

/// <summary>State of a <c>pelly asp</c> run (set by ASP LOCATE; null for other runs).</summary>
public sealed class AspRunState(
    AspPage originalPage, AspTargetUnit unit, TextSpanInfo originalSpan, AspProcedure? procedure,
    AspIncludeSet includes, string encodingName, string siteRoot)
{
    public AspPage OriginalPage { get; } = originalPage;
    public AspTargetUnit Unit { get; } = unit;
    public TextSpanInfo OriginalSpan { get; } = originalSpan;

    /// <summary>The target span in the current text; SLICE re-finds it after each write.</summary>
    public TextSpanInfo CurrentSpan { get; set; } = originalSpan;

    /// <summary>The target procedure in the original page; null for a line range target.</summary>
    public AspProcedure? Procedure { get; } = procedure;

    public AspIncludeSet Includes { get; } = includes;

    /// <summary><c>CP949</c> or <c>UTF-8</c>.</summary>
    public string EncodingName { get; } = encodingName;

    /// <summary>Full path the <c>include virtual=</c> paths resolve from.</summary>
    public string SiteRoot { get; } = siteRoot;

    /// <summary>
    /// The site index the client-side warning steps share across steps and retries (loaded at most once per run).
    /// Null until a step needs it.
    /// </summary>
    public Lazy<PellyCoder.Core.Web.WebSiteIndexResult>? SiteIndex { get; set; }

    /// <summary>
    /// ADO constants (<c>adOpenStatic</c> …) are known regardless of includes: <c>asp.adoConstants</c> is on or the site's
    /// <c>global.asa</c> has a typelib METADATA (set by LOCATE).
    /// </summary>
    public bool AdoConstants { get; init; }

    /// <summary>Compile errors of the original page (set by the first VERIFY); only errors beyond these count.</summary>
    public IReadOnlyList<Diagnostic>? OriginalCompileErrors { get; set; }

    /// <summary>Stored procedure calls found in the whole original page (set once, by the first SLICE).</summary>
    public IReadOnlyList<AspSpCall> OriginalSpCalls
    {
        get;
        set
        {
            field = value;
            OriginalSpCallsSet = true;
        }
    } = [];

    /// <summary>True once <see cref="OriginalSpCalls"/> has been set (it may legitimately be empty).</summary>
    public bool OriginalSpCallsSet { get; private set; }

    /// <summary>The JS-level / jQuery env lines for the client blocks the edit overlaps (set once by SLICE; empty when none overlap, null before).</summary>
    public string? ClientEnv { get; set; }

    /// <summary>Form field warnings of the last verify (W3 design §5.3); the apply end compares with its own recount.</summary>
    public int FormFieldWarnings { get; set; }

    /// <summary>Latest result of each check by name.</summary>
    public Dictionary<string, AspCheck> Checks { get; } = new(StringComparer.Ordinal);

    public void Record(string name, AspCheckStatus status, string? detail = null) =>
        Checks[name] = new AspCheck(name, status, detail);

    /// <summary>
    /// The editable parts of <see cref="CurrentSpan"/>: maximal runs of lines that are not locked (directive, include and
    /// metadata lines) in <paramref name="current"/>, the page parsed from the current text (default: the original page).
    /// </summary>
    public IReadOnlyList<EditableRegion> EditableRegions(AspPage? current = null) =>
        RegionsOf(CurrentSpan, (current ?? OriginalPage).LockedLines);

    /// <summary>The editable parts of <see cref="OriginalSpan"/> in the original page: where the edit was allowed to start from.</summary>
    public IReadOnlyList<EditableRegion> OriginalRegions() => RegionsOf(OriginalSpan, OriginalPage.LockedLines);

    private static List<EditableRegion> RegionsOf(TextSpanInfo span, IReadOnlySet<int> locked)
    {
        var regions = new List<EditableRegion>();
        int? start = null;
        for (var line = span.StartLine; line <= span.EndLine + 1; line++)
        {
            var editable = line <= span.EndLine && !locked.Contains(line);
            if (editable)
            {
                start ??= line;
            }
            else if (start is { } s)
            {
                regions.Add(new EditableRegion(new TextSpanInfo(s, line - 1), "target"));
                start = null;
            }
        }
        return regions;
    }
}
