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

    /// <summary>Latest result of each check by name.</summary>
    public Dictionary<string, AspCheck> Checks { get; } = new(StringComparer.Ordinal);

    public void Record(string name, AspCheckStatus status, string? detail = null) =>
        Checks[name] = new AspCheck(name, status, detail);

    /// <summary>
    /// The editable parts of <see cref="CurrentSpan"/>: maximal runs of lines that are not locked (directive, include and
    /// metadata lines) in <paramref name="current"/>, the page parsed from the current text (default: the original page).
    /// </summary>
    public IReadOnlyList<EditableRegion> EditableRegions(AspPage? current = null)
    {
        var locked = (current ?? OriginalPage).LockedLines;
        var regions = new List<EditableRegion>();
        int? start = null;
        for (var line = CurrentSpan.StartLine; line <= CurrentSpan.EndLine + 1; line++)
        {
            var editable = line <= CurrentSpan.EndLine && !locked.Contains(line);
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
