using PellyCoder.Core.Files;
using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Web;
using static PellyCoder.Core.Fsm.Phases.WebLocateCommon;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// Finds the <c>pelly css</c> target (design §3, §4.1): the rule whose selector list holds <c>--selector</c> (group rules
/// excluded), the innermost rule around <c>--line</c> (group rules included), the <c>--lines</c> range (refused when it cuts a
/// rule), or the only rule in the file. Refuses other extensions, minified and vendor files. Problems already in the original
/// sheet do not stop the run: they are the baseline.
/// </summary>
public sealed class CssLocatePhase(IFileSystem fs, IUserInteraction ui, PellyOptions options, ITextFileCodec codec) : IPhase
{
    public RunPhase Phase => RunPhase.Locate;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var request = ctx.Request;
        var given = (request.Selector is not null ? 1 : 0) + (request.Line is not null ? 1 : 0) + (request.Lines is not null ? 1 : 0);
        if (given > 1)
        {
            ctx.Messages.Add("--selector, --line, --lines는 하나만 쓸 수 있습니다");
            return PhaseOutcome.EnvError;
        }

        var siteRoot = SiteRoot(fs, ctx, options);
        if (RefuseFile(fs, ctx, options, siteRoot, ".css", "pelly css", acceptPages: true) is { } refused)
            return refused;

        var text = ctx.Original.Text;
        var lineCount = LineCount(text);
        var page = WebPages.IsPage(ctx.FilePath, options.Asp.Extensions) ? AspPage.Parse(text) : null;
        var pieces = new List<Piece>();
        if (page is null)
        {
            pieces.Add(new Piece(-1, null, text, CssSheet.Parse(text)));
        }
        else if (request.Line is not null || request.Lines is not null)
        {
            var range = request.Lines ?? new TextSpanInfo(request.Line!.Value, request.Line.Value);
            if (request.Lines is { } outer && OutOfFile(outer, lineCount))
                return NotFound(ctx, OutOfFileMessage(outer, lineCount));
            var first = page.ClientBlockAt(range.StartLine);
            var last = page.ClientBlockAt(range.EndLine);
            string NotInside() => request.Lines is null
                ? $"{range.StartLine}번 줄은 <style> 블록 안이 아닙니다. pelly asp를 쓰십시오"
                : $"줄 {range.StartLine}-{range.EndLine} 범위가 <style> 블록 밖에 걸칩니다. pelly asp --lines를 쓰십시오";
            if (first is null || last is null || first.Value.Index != last.Value.Index)
                return Outside(ctx, NotInside());
            var block = first.Value.Block;
            if (block.Kind == AspClientBlockKind.Style && !block.Closed)
                return Outside(ctx, $"줄 {block.OpenLine}의 <style>가 닫히지 않았습니다");
            if (!IsEditable(block))
                return Outside(ctx, NotInside());
            pieces.Add(PieceOf(page, text, first.Value.Index));
        }
        else
        {
            for (var i = 0; i < page.ClientBlocks.Count; i++)
                if (IsEditable(page.ClientBlocks[i]))
                    pieces.Add(PieceOf(page, text, i));
            if (pieces.Count == 0)
                return Outside(ctx, "이 페이지에는 고칠 수 있는 <style> 블록이 없습니다");
        }

        Piece target = pieces[0];
        CssRule? rule = null;
        TextSpanInfo span;
        if (request.Selector is { } selector)
        {
            var matches = pieces.SelectMany(p => p.Sheet.Rules.Where(r => !r.IsAtRule && CssSelector.Matches(r, selector))
                .Select(r => new Hit(p, r))).ToList();
            if (matches.Count == 0)
                return NotFound(ctx, $"선택자 '{selector}'의 규칙을 찾을 수 없습니다: {ctx.FilePath}");
            var chosen = await ChooseAsync(ui, ctx, matches, "수정할 규칙을 고르십시오", h => Label(h.Rule),
                $"선택자 '{selector}'의 규칙이 {matches.Count}개입니다. 대화형으로 실행해 고르거나 --lines A-B로 범위를 주십시오: ", ct);
            if (chosen is null)
                return NoChoice(ctx);
            target = chosen.Piece;
            rule = chosen.Rule;
        }
        else if (request.Line is { } line)
        {
            rule = target.Sheet.Rules
                .Where(r => r.StartLine <= line && line <= r.EndLine)
                .OrderBy(r => r.EndLine - r.StartLine)
                .ThenByDescending(r => r.StartLine)
                .FirstOrDefault();
            if (rule is null)
                return NotFound(ctx, $"{line}번 줄을 감싸는 규칙이 없습니다. --lines A-B로 범위를 주십시오");
        }
        else if (request.Lines is null)
        {
            var every = pieces.SelectMany(p => p.Sheet.Rules.Select(r => new Hit(p, r))).ToList();
            if (every.Count != 1)
                return NotFound(ctx, "--selector, --line, --lines 중 하나를 주십시오");
            target = every[0].Piece;
            rule = every[0].Rule;
        }

        if (request.Lines is { } lines)
        {
            if (OutOfFile(lines, lineCount))
                return NotFound(ctx, OutOfFileMessage(lines, lineCount));
            if (CutsRule(target.Sheet, lines))
                return NotFound(ctx, $"줄 {lines.StartLine}-{lines.EndLine} 범위가 규칙 중간을 자릅니다");
            span = lines;
        }
        else
        {
            span = new TextSpanInfo(rule!.StartLine, rule.EndLine);
        }

        var unit = request.Lines is null ? WebTargetUnit.Rule : WebTargetUnit.Lines;
        var display = unit == WebTargetUnit.Rule ? Display(rule!) : $"줄 {span.StartLine}-{span.EndLine}";
        ctx.Target = new EditTarget(EditKind.Css, ctx.FilePath, display, span, null);
        ctx.ProjectStyle = "Web";
        var pageState = page is null ? null : new WebPageState(page, target.BlockIndex, target.View!,
            Enumerable.Range(0, page.ClientBlocks.Count).Select(i => PageBlockView.Build(page, text, i)).ToList());
        ctx.Web = new WebRunState(EditKind.Css, unit, span, target.Text, EncodingName(ctx.Original.Encoding), OriginalAscii(fs, ctx),
            siteRoot, SiteIndex(fs, ctx, options, siteRoot, codec))
        {
            Page = pageState,
            Rule = unit == WebTargetUnit.Rule ? rule : null,
            OriginalCss = target.Sheet,
        };
        return PhaseOutcome.Ok;
    }

    private sealed record Piece(int BlockIndex, PageBlockView? View, string Text, CssSheet Sheet);

    private sealed record Hit(Piece Piece, CssRule Rule);

    private static Piece PieceOf(AspPage page, string pageText, int blockIndex)
    {
        var view = PageBlockView.Build(page, pageText, blockIndex);
        return new Piece(blockIndex, view, view.Text, CssSheet.Parse(view.Text));
    }

    /// <summary>A style block with content the run can edit: closed.</summary>
    private static bool IsEditable(AspClientBlock block) => block.Kind == AspClientBlockKind.Style && block.Closed;

    private static PhaseOutcome Outside(RunContext ctx, string message) =>
        Refuse(ctx, FailureKinds.WebOutsideBlock, message);

    /// <summary>True when a rule starts inside the range and ends outside it, or the other way round.</summary>
    private static bool CutsRule(CssSheet sheet, TextSpanInfo range) =>
        sheet.Rules.Any(r =>
        {
            var startsIn = r.StartLine >= range.StartLine && r.StartLine <= range.EndLine;
            var endsIn = r.EndLine >= range.StartLine && r.EndLine <= range.EndLine;
            return startsIn != endsIn;
        });

    private static string Display(CssRule r) => r.Container is null ? r.Selector : $"{r.Container} > {r.Selector}";

    private static string Label(CssRule r) => $"{Display(r)} (줄 {r.StartLine}-{r.EndLine})";
}
