using Acornima;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Asp;

/// <summary>
/// Result of <see cref="AspClientChecks.Judge"/>: the rows to record and, when a check blocks, the failure kind with the
/// material for the model's feedback. Without an overlapping client block there are no rows.
/// </summary>
public sealed record AspClientVerdict(
    IReadOnlyList<(string Check, AspCheckStatus Status, string? Detail)> Rows,
    string? FailureKind = null,
    string Title = "",
    IReadOnlyList<Diagnostic>? DiagnosticList = null,
    IReadOnlyList<WebSafetyFinding>? FindingList = null,
    string? LevelName = null,
    string? LevelFeature = null,
    int LevelLine = 0)
{
    public static readonly AspClientVerdict Empty = new([]);

    public IReadOnlyList<Diagnostic> Diagnostics => DiagnosticList ?? [];
    public IReadOnlyList<WebSafetyFinding> Findings => FindingList ?? [];

    /// <summary>A new unsafe construct (ends as <c>SafetyRejected</c> when it stays).</summary>
    public bool IsSafety => Findings.Count > 0;

    /// <summary>The feedback for the model; excerpts come from the page text, so line numbers are the page's.</summary>
    public Feedback ToFeedback(FeedbackBuilder builder, string patchedText)
    {
        if (Diagnostics.Count > 0) return builder.ForDiagnostics(Title, Diagnostics, patchedText);
        if (IsSafety) return builder.ForWebSafety(Findings);
        var lines = patchedText.Split('\n');
        var lineText = LevelLine >= 1 && LevelLine <= lines.Length ? lines[LevelLine - 1] : "";
        return builder.ForJsLevel(LevelName ?? "", LevelFeature ?? "newer syntax", LevelLine, lineText);
    }
}

/// <summary>
/// JS and CSS checks for the client blocks an <c>pelly asp</c> edit touches (design 4.5). A block is judged when its content
/// lines the original edit regions touch (the middle blocks; see <see cref="Pairs"/>); a patched block with no partner is new (empty baseline).
/// </summary>
public static class AspClientChecks
{
    public const string JsSyntax = "JsSyntax";
    public const string JsLevel = "JsLevel";
    public const string JsSafety = "JsSafety";
    public const string CssSyntax = "CssSyntax";
    public const string CssSafety = "CssSafety";

    /// <summary>
    /// One block to judge: the original block (null for a new one) and the patched block. <paramref name="Broken"/>: the block lost its
    /// shape (unclosed, or no longer the same kind of block), so it cannot be compared.
    /// </summary>
    internal readonly record struct Pair(int? OriginalIndex, int PatchedIndex, AspClientBlockKind Kind, bool Broken = false);

    /// <summary>
    /// Blocks wholly before the edit regions (<c>Before</c>) and wholly after them (<c>After</c>); the rest is the middle,
    /// the blocks the edit may have changed.
    /// </summary>
    private static (int Before, int After) Outside(AspPage page, IReadOnlyList<EditableRegion> regions)
    {
        if (regions.Count == 0) return (0, 0);
        var first = regions.Min(r => r.Span.StartLine);
        var last = regions.Max(r => r.Span.EndLine);
        var blocks = page.ClientBlocks;
        var before = 0;
        while (before < blocks.Count && blocks[before].CloseLine < first) before++;
        var after = 0;
        while (after < blocks.Count - before && blocks[blocks.Count - 1 - after].OpenLine > last) after++;
        return (before, after);
    }

    /// <summary>Indexes of the judged-kind blocks of <paramref name="page"/> in the middle (the blocks the edit regions touch).</summary>
    public static IReadOnlyList<int> OverlappedBlocks(AspPage page, IReadOnlyList<EditableRegion> regions)
    {
        var (before, after) = Outside(page, regions);
        return Enumerable.Range(before, page.ClientBlocks.Count - before - after).Where(i => Judged(page.ClientBlocks[i])).ToList();
    }

    /// <summary>
    /// Blocks before the regions pair with the patched page's first blocks and blocks after them with its last (neither is judged);
    /// the middle pairs in order when the counts agree, else from the front and from the back while the kinds match.
    /// A patched middle block with no partner is new; a deleted original one needs no judgement. No middle block on either side: nothing.
    /// </summary>
    internal static IReadOnlyList<Pair> Pairs(
        AspPage original, string originalText, AspPage patched, string patchedText, IReadOnlyList<EditableRegion> regions)
    {
        var (before, after) = Outside(original, regions);
        var o = original.ClientBlocks;
        var p = patched.ClientBlocks;
        after = Math.Min(after, Math.Max(0, p.Count - before));
        before = Math.Min(before, p.Count);
        var oMid = o.Count - before - after;
        var pMid = p.Count - before - after;
        if (oMid <= 0 && pMid <= 0) return [];

        var pairs = new List<Pair>();
        void Add(int? oi, int pi)
        {
            var po = p[pi];
            if (oi is null)
            {
                if (!po.Closed) pairs.Add(new Pair(null, pi, po.Kind, true));
                else if (Judged(po)) pairs.Add(new Pair(null, pi, po.Kind));
                return;
            }
            var oo = o[oi.Value];
            if (!Judged(oo)) return;
            pairs.Add(new Pair(oi, pi, oo.Kind, oo.Kind != po.Kind || !Judged(po)));
        }

        if (oMid == pMid)
        {
            for (var i = 0; i < oMid; i++) Add(before + i, before + i);
            return pairs;
        }

        // Counts differ: blocks that kept their kind and content anchor the match (so an inserted block does not shift the
        // others); between anchors, pair from the front and from the back while the kinds match, and the rest are new or deleted.
        string Content(AspPage page, string text, int i) => text[page.ClientBlocks[i].ContentStart..page.ClientBlocks[i].ContentEnd];
        bool Same(int oi, int pi) => o[oi].Kind == p[pi].Kind && Content(original, originalText, oi) == Content(patched, patchedText, pi);
        var lcs = new int[oMid + 1, pMid + 1];
        for (var i = oMid - 1; i >= 0; i--)
            for (var j = pMid - 1; j >= 0; j--)
                lcs[i, j] = Same(before + i, before + j) ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        int io = 0, jp = 0;
        void Gap(int oEnd, int pEnd)
        {
            int front = 0, back = 0;
            while (io + front < oEnd && jp + front < pEnd && o[before + io + front].Kind == p[before + jp + front].Kind)
            {
                Add(before + io + front, before + jp + front);
                front++;
            }
            while (io + front < oEnd - back && jp + front < pEnd - back
                   && o[before + oEnd - 1 - back].Kind == p[before + pEnd - 1 - back].Kind)
            {
                Add(before + oEnd - 1 - back, before + pEnd - 1 - back);
                back++;
            }
            for (var j = jp + front; j < pEnd - back; j++) Add(null, before + j);
            io = oEnd;
            jp = pEnd;
        }
        while (io < oMid && jp < pMid)
        {
            if (Same(before + io, before + jp) && lcs[io, jp] == lcs[io + 1, jp + 1] + 1)
            {
                Add(before + io, before + jp);
                io++;
                jp++;
                continue;
            }
            // Next anchor: the first (o, p) at or after the cursor that is on an optimal path.
            var found = false;
            for (var i = io; i < oMid && !found; i++)
                for (var j = jp; j < pMid && !found; j++)
                    if (Same(before + i, before + j) && lcs[i, j] == lcs[io, jp] && lcs[i, j] == lcs[i + 1, j + 1] + 1)
                    {
                        Gap(i, j);
                        found = true;
                    }
            if (!found) break;
        }
        Gap(oMid, pMid);
        return pairs;
    }

    /// <summary>A closed inline JavaScript <c>&lt;script&gt;</c> or a closed <c>&lt;style&gt;</c>.</summary>
    private static bool Judged(AspClientBlock b) =>
        b.Closed && (b.Kind == AspClientBlockKind.Style || PageBlockView.IsJavaScriptBlock(b));

    public static AspClientVerdict Judge(
        AspPage original, string originalText, AspPage patched, string patchedText,
        IReadOnlyList<EditableRegion> originalRegions, string? jsTarget, string fileName = "")
    {
        var pairs = Pairs(original, originalText, patched, patchedText, originalRegions);
        if (pairs.Count == 0) return AspClientVerdict.Empty;

        var rows = new Dictionary<string, (AspCheckStatus Status, string? Detail)>(StringComparer.Ordinal);
        foreach (var pair in pairs)
        {
            if (pair.Broken)
            {
                Put(rows, pair.Kind == AspClientBlockKind.Style ? CssSyntax : JsSyntax, AspCheckStatus.Failed, "블록 경계가 바뀜");
                return BoundaryFailure(patched.ClientBlocks[pair.PatchedIndex], pair.Kind, fileName) with { Rows = Ordered(rows) };
            }
            var pv = PageBlockView.Build(patched, patchedText, pair.PatchedIndex);
            var ov = pair.OriginalIndex is { } oi ? PageBlockView.Build(original, originalText, oi) : null;
            var failure = pair.Kind == AspClientBlockKind.Style
                ? JudgeCss(ov, pv, rows, fileName)
                : JudgeJs(original, originalText, ov, pv, jsTarget, rows, fileName);
            if (failure is not null) return failure with { Rows = Ordered(rows) };
        }
        return new AspClientVerdict(Ordered(rows));
    }

    private static AspClientVerdict BoundaryFailure(AspClientBlock block, AspClientBlockKind kind, string fileName)
    {
        var tag = kind == AspClientBlockKind.Style ? "<style>" : "<script>";
        var error = new Diagnostic(fileName, block.OpenLine, 1, "error", "ClientBlockBoundary",
            $"the {tag} block opened on this line is not a closed block of the same kind any more; keep every {tag} and its closing tag");
        return kind == AspClientBlockKind.Style
            ? new AspClientVerdict([], FailureKinds.CssStructureErrors, "CSS STRUCTURE CHECK FAILED", [error])
            : new AspClientVerdict([], FailureKinds.JsSyntaxErrors, "JS SYNTAX CHECK FAILED", [error]);
    }

    private static readonly string[] RowOrder = [JsSyntax, JsLevel, JsSafety, CssSyntax, CssSafety];

    private static List<(string, AspCheckStatus, string?)> Ordered(Dictionary<string, (AspCheckStatus Status, string? Detail)> rows) =>
        RowOrder.Where(rows.ContainsKey).Select(k => (k, rows[k].Status, rows[k].Detail)).ToList();

    // Failed beats Passed beats Skipped, so one bad block shows and a later good block does not hide it.
    private static void Put(Dictionary<string, (AspCheckStatus Status, string? Detail)> rows, string check, AspCheckStatus status, string? detail = null)
    {
        if (rows.TryGetValue(check, out var old) && Rank(old.Status) >= Rank(status)) return;
        rows[check] = (status, detail);
    }

    private static int Rank(AspCheckStatus s) => s switch { AspCheckStatus.Failed => 2, AspCheckStatus.Passed => 1, _ => 0 };

    private static AspClientVerdict? JudgeCss(
        PageBlockView? ov, PageBlockView pv, Dictionary<string, (AspCheckStatus Status, string? Detail)> rows, string fileName)
    {
        var before = CssSheet.Parse(ov?.Text ?? "");
        var after = CssSheet.Parse(pv.Text);
        var problems = CssChecks.NewProblems(before, after);
        if (problems.Count > 0)
        {
            Put(rows, CssSyntax, AspCheckStatus.Failed, string.Join(", ", problems.Select(p => p.Code).Distinct()));
            var errors = problems.Select(p => new Diagnostic(fileName, p.Line, p.Column, "error", p.Code, p.Message)).ToList();
            return new AspClientVerdict([], FailureKinds.CssStructureErrors, "CSS STRUCTURE CHECK FAILED", errors);
        }
        Put(rows, CssSyntax, AspCheckStatus.Passed);

        var findings = CssSafetyGuard.NewFindings(before, after);
        if (findings.Count > 0)
        {
            Put(rows, CssSafety, AspCheckStatus.Failed, string.Join(", ", findings.Select(f => f.Rule).Distinct()));
            return new AspClientVerdict([], FailureKinds.CssSafetyRejected, FindingList: findings);
        }
        Put(rows, CssSafety, AspCheckStatus.Passed);
        return null;
    }

    private static AspClientVerdict? JudgeJs(
        AspPage page, string pageText, PageBlockView? ov, PageBlockView pv, string? jsTarget,
        Dictionary<string, (AspCheckStatus Status, string? Detail)> rows, string fileName)
    {
        var baseline = ov?.Text ?? "";
        var plan = Plan(page, pageText, ov, pv, jsTarget);

        if (plan.Level is not { } level)
        {
            // Server statements in the block, or an original that does not parse: bracket/string/comment pairing.
            var syntax = JsChecks.FallbackSyntax(baseline, fileName, pv.Text);
            if (syntax.Errors.Count > 0) return SyntaxFailure(rows, syntax.Errors);
            Put(rows, JsSyntax, AspCheckStatus.Skipped, plan.Reason);
            Put(rows, JsLevel, AspCheckStatus.Skipped, plan.Reason);
            return Safety(rows, JsSafetyGuard.NewFindingsText(baseline, pv.Text));
        }

        var verdict = JsChecks.Syntax(level, plan.Module, fileName, pv.Text);
        if (verdict.Errors.Count > 0) return SyntaxFailure(rows, verdict.Errors);
        Put(rows, JsSyntax, AspCheckStatus.Passed);
        if (verdict.LevelExceeded)
        {
            var feature = verdict.Feature ?? "newer syntax";
            var line = verdict.FeatureLine ?? 1;
            Put(rows, JsLevel, AspCheckStatus.Failed, $"{feature} (줄 {line})");
            return new AspClientVerdict([], FailureKinds.JsLevelExceeded, LevelName: JsLevels.Name(level),
                LevelFeature: verdict.FeatureEnglish ?? feature, LevelLine: line);
        }
        Put(rows, JsLevel, AspCheckStatus.Passed);

        var before = JsDocument.Parse(baseline, plan.OriginalLevel ?? level, plan.Module);
        return Safety(rows, before.Error is null && verdict.Patched is not null
            ? JsSafetyGuard.NewFindings(before, verdict.Patched)
            : JsSafetyGuard.NewFindingsText(baseline, pv.Text));
    }

    private static AspClientVerdict SyntaxFailure(Dictionary<string, (AspCheckStatus Status, string? Detail)> rows, IReadOnlyList<Diagnostic> errors)
    {
        Put(rows, JsSyntax, AspCheckStatus.Failed, $"줄 {errors[0].Line}: {errors[0].Message}");
        return new AspClientVerdict([], FailureKinds.JsSyntaxErrors, "JS SYNTAX CHECK FAILED", errors);
    }

    private static AspClientVerdict? Safety(Dictionary<string, (AspCheckStatus Status, string? Detail)> rows, IReadOnlyList<WebSafetyFinding> findings)
    {
        if (findings.Count == 0)
        {
            Put(rows, JsSafety, AspCheckStatus.Passed);
            return null;
        }
        Put(rows, JsSafety, AspCheckStatus.Failed, string.Join(", ", findings.Select(f => f.Rule).Distinct()));
        return new AspClientVerdict([], FailureKinds.JsSafetyRejected, FindingList: findings);
    }

    /// <summary>How a JS block is judged: the level it must parse at (null: pairing check), module flag, and the original's own level.</summary>
    internal sealed record JsPlan(EcmaVersion? Level, bool Module, EcmaVersion? OriginalLevel, string? Reason);

    internal static JsPlan Plan(AspPage page, string pageText, PageBlockView? ov, PageBlockView pv, string? jsTarget)
    {
        var setting = (jsTarget ?? "auto").Trim();
        var configured = setting.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : JsLevels.FromSetting(setting);
        if (ov is null)
        {
            if (pv.ServerCode == PageServerCode.Statements) return new JsPlan(null, false, null, "서버 코드가 섞인 스크립트");
            return new JsPlan(configured ?? LowestOtherLevel(page, pageText, -1) ?? EcmaVersion.ES5, false, null, null);
        }
        if (ov.ServerCode == PageServerCode.Statements || pv.ServerCode == PageServerCode.Statements)
            return new JsPlan(null, false, null, "서버 코드가 섞인 스크립트");
        var detected = JsLevels.Detect(ov.Text);
        if (detected.Level is not { } level)
            return new JsPlan(null, false, null, $"원본이 해석되지 않음: 줄 {detected.LatestError?.Line ?? 1}");
        var target = configured is { } wanted && JsLevels.Compare(wanted, level) >= 0 ? wanted : level;
        return new JsPlan(target, detected.Module, level, null);
    }

    /// <summary>The lowest level any other inline JS block of the page parses at; null when there is none.</summary>
    internal static EcmaVersion? LowestOtherLevel(AspPage page, string pageText, int exceptIndex)
    {
        EcmaVersion? lowest = null;
        for (var i = 0; i < page.ClientBlocks.Count; i++)
        {
            if (i == exceptIndex || !Judged(page.ClientBlocks[i]) || page.ClientBlocks[i].Kind != AspClientBlockKind.Script) continue;
            var view = PageBlockView.Build(page, pageText, i);
            if (view.ServerCode == PageServerCode.Statements) continue;
            if (JsLevels.Detect(view.Text).Level is { } level && (lowest is null || JsLevels.Compare(level, lowest.Value) < 0))
                lowest = level;
        }
        return lowest;
    }
}
