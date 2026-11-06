using PellyCoder.Core.Files;
using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Models;
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
        if (RefuseFile(fs, ctx, options, siteRoot, ".css", "pelly css") is { } refused)
            return refused;

        var text = ctx.Original.Text;
        var sheet = CssSheet.Parse(text);
        var lineCount = LineCount(text);
        CssRule? rule = null;
        TextSpanInfo span;
        if (request.Selector is { } selector)
        {
            var matches = sheet.Rules.Where(r => !r.IsAtRule && CssSelector.Matches(r, selector)).ToList();
            if (matches.Count == 0)
                return NotFound(ctx, $"선택자 '{selector}'의 규칙을 찾을 수 없습니다: {ctx.FilePath}");
            rule = await ChooseAsync(ui, ctx, matches, "수정할 규칙을 고르십시오", Label,
                $"선택자 '{selector}'의 규칙이 {matches.Count}개입니다. 대화형으로 실행해 고르거나 --lines A-B로 범위를 주십시오: ", ct);
            if (rule is null)
                return NoChoice(ctx);
        }
        else if (request.Line is { } line)
        {
            rule = sheet.Rules
                .Where(r => r.StartLine <= line && line <= r.EndLine)
                .OrderBy(r => r.EndLine - r.StartLine)
                .ThenByDescending(r => r.StartLine)
                .FirstOrDefault();
            if (rule is null)
                return NotFound(ctx, $"{line}번 줄을 감싸는 규칙이 없습니다. --lines A-B로 범위를 주십시오");
        }
        else if (request.Lines is null)
        {
            if (sheet.Rules.Count != 1)
                return NotFound(ctx, "--selector, --line, --lines 중 하나를 주십시오");
            rule = sheet.Rules[0];
        }

        if (request.Lines is { } lines)
        {
            if (OutOfFile(lines, lineCount))
                return NotFound(ctx, OutOfFileMessage(lines, lineCount));
            if (CutsRule(sheet, lines))
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
        ctx.Web = new WebRunState(EditKind.Css, unit, span, text, EncodingName(ctx.Original.Encoding), OriginalAscii(fs, ctx),
            siteRoot, SiteIndex(fs, ctx, options, siteRoot, codec))
        {
            Rule = unit == WebTargetUnit.Rule ? rule : null,
            OriginalCss = sheet,
        };
        return PhaseOutcome.Ok;
    }

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
