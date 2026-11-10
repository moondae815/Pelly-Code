using PellyCoder.Core.Files;
using System.IO.Abstractions;
using Acornima;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Web;
using static PellyCoder.Core.Fsm.Phases.WebLocateCommon;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// Finds the <c>pelly js</c> target (design §3, §4.1, §4.4): the function named by <c>--function</c>, the innermost named
/// function around <c>--line</c>, the <c>--lines</c> range (refused when it cuts a statement), or the only named function
/// (several: selection UI, or exit 3 when not interactive). Refuses other extensions, minified and vendor files. Decides the
/// original and target language levels; an original that does not parse switches the run to fallback mode (token scan).
/// </summary>
public sealed class JsLocatePhase(IFileSystem fs, IUserInteraction ui, PellyOptions options, ITextFileCodec codec) : IPhase
{
    public RunPhase Phase => RunPhase.Locate;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var request = ctx.Request;
        var given = (request.MethodName is not null ? 1 : 0) + (request.Line is not null ? 1 : 0) + (request.Lines is not null ? 1 : 0);
        if (given > 1)
        {
            ctx.Messages.Add("--function, --line, --lines는 하나만 쓸 수 있습니다");
            return PhaseOutcome.EnvError;
        }

        var siteRoot = SiteRoot(fs, ctx, options);
        var isPage = WebPages.IsPage(ctx.FilePath, options.Asp.Extensions);
        if (RefuseFile(fs, ctx, options, siteRoot, ".js", "pelly js", acceptPages: true) is { } refused)
            return refused;

        var text = ctx.Original.Text;
        var lineCount = LineCount(text);
        var page = isPage ? AspPage.Parse(text) : null;
        Analysis target;
        int blockIndex = -1;
        IReadOnlyList<Analysis> all = [];

        if (page is null)
        {
            target = Analyze(text, null);
            ctx.Messages.AddRange(target.Notes);
            all = [target];
        }
        else if (request.Line is not null || request.Lines is not null)
        {
            var lines = request.Lines ?? new TextSpanInfo(request.Line!.Value, request.Line.Value);
            if (request.Lines is { } range && OutOfFile(range, lineCount))
                return NotFound(ctx, OutOfFileMessage(range, lineCount));
            var first = page.ClientBlockAt(lines.StartLine);
            var last = page.ClientBlockAt(lines.EndLine);
            if (first is null || last is null || first.Value.Index != last.Value.Index)
                return Outside(ctx, request.Lines is null
                    ? $"{lines.StartLine}번 줄은 <script> 블록 안이 아닙니다. pelly asp를 쓰십시오"
                    : $"줄 {lines.StartLine}-{lines.EndLine} 범위가 <script> 블록 밖에 걸칩니다. pelly asp --lines를 쓰십시오");
            blockIndex = first.Value.Index;
            var block = first.Value.Block;
            if (block.Kind == AspClientBlockKind.Script && !block.Closed)
                return Outside(ctx, $"줄 {block.OpenLine}의 <script>가 닫히지 않았습니다");
            if (!IsEditable(block))
                return Outside(ctx, request.Lines is null
                    ? $"{lines.StartLine}번 줄은 <script> 블록 안이 아닙니다. pelly asp를 쓰십시오"
                    : $"줄 {lines.StartLine}-{lines.EndLine} 범위가 <script> 블록 밖에 걸칩니다. pelly asp --lines를 쓰십시오");
            target = AnalyzeBlock(page, text, blockIndex);
            ctx.Messages.AddRange(target.Notes);
            all = [target];
        }
        else
        {
            var editable = Enumerable.Range(0, page.ClientBlocks.Count).Where(i => IsEditable(page.ClientBlocks[i])).ToList();
            if (editable.Count == 0)
                return Outside(ctx, "이 페이지에는 고칠 수 있는 <script> 블록이 없습니다");
            all = editable.Select(i => AnalyzeBlock(page, text, i)).ToList();
            target = all[0];
        }

        Hit? hit = null;
        TextSpanInfo span;
        if (request.MethodName is { } name)
        {
            var matches = all.SelectMany(a => a.Functions.Where(f => f.QualifiedName == name || f.Name == name).Select(f => new Hit(a, f))).ToList();
            if (matches.Count == 0)
                return NotFound(ctx, $"함수 '{name}'을(를) 찾을 수 없습니다: {ctx.FilePath}");
            hit = await ChooseAsync(ui, ctx, matches, "수정할 함수를 고르십시오", h => Label(h.Function),
                $"'{name}' 함수가 {matches.Count}개입니다. 대화형으로 실행해 고르거나 --lines A-B로 범위를 주십시오: ", ct);
            if (hit is null)
                return NoChoice(ctx);
        }
        else if (request.Line is { } line)
        {
            if (target.Fallback)
                return NotFound(ctx, $"원본이 해석되지 않아 {line}번 줄을 감싸는 함수를 찾을 수 없습니다. --lines A-B로 범위를 주십시오");
            var inner = target.Functions
                .Where(f => f.StartLine <= line && line <= f.EndLine)
                .OrderBy(f => f.EndLine - f.StartLine)
                .FirstOrDefault();
            if (inner is null)
                return NotFound(ctx, $"{line}번 줄을 감싸는 이름 있는 함수가 없습니다. --lines A-B로 범위를 주십시오");
            hit = new Hit(target, inner);
        }
        else if (request.Lines is null)
        {
            var every = all.SelectMany(a => a.Functions.Select(f => new Hit(a, f))).ToList();
            if (every.Count == 0)
                return NotFound(ctx, page is null ? "파일에 이름 있는 함수가 없습니다. --lines A-B로 범위를 주십시오"
                    : "이 페이지의 <script> 블록에 이름 있는 함수가 없습니다. --lines A-B로 범위를 주십시오");
            hit = await ChooseAsync(ui, ctx, every, "수정할 함수를 고르십시오", h => Label(h.Function),
                $"이름 있는 함수가 {every.Count}개입니다. --function으로 지정하십시오: ", ct);
            if (hit is null)
                return NoChoice(ctx);
        }

        if (hit is not null && page is not null && request.Line is null)
        {
            // The block is only known now: its notes (level, skipped syntax check) are reported for it alone.
            target = hit.Analysis;
            blockIndex = target.BlockIndex;
            ctx.Messages.AddRange(target.Notes);
        }
        var function = hit?.Function;
        var document = target.Document;

        if (request.Lines is { } lines2)
        {
            if (OutOfFile(lines2, lineCount))
                return NotFound(ctx, OutOfFileMessage(lines2, lineCount));
            if (document is not null && document.CutsStatement(lines2))
                return NotFound(ctx, $"줄 {lines2.StartLine}-{lines2.EndLine} 범위가 문장 중간을 자릅니다. 문장 경계로 범위를 주십시오");
            span = lines2;
        }
        else
        {
            span = new TextSpanInfo(function!.StartLine, function.EndLine);
        }

        var found = JqueryVersions.Find(fs, siteRoot);
        var siteIndex = SiteIndex(fs, ctx, options, siteRoot, codec);
        PageScriptContext? pageScripts = page is null ? null
            : PageScripts.Collect(fs, siteRoot, ctx.FilePath, page, blockIndex, codec, siteIndex.Value, found, options.Web.JqueryVersion, text);
        var jquery = pageScripts is not null ? pageScripts.Jquery : JqueryVersions.Baseline(found, options.Web.JqueryVersion);
        var jquerySource = pageScripts?.JquerySource
            ?? (!string.IsNullOrWhiteSpace(options.Web.JqueryVersion) && Version.TryParse(options.Web.JqueryVersion.Trim(), out _) ? "setting" : "site");
        if (jquerySource == "site" && string.IsNullOrWhiteSpace(options.Web.JqueryVersion) && found.Select(f => f.Version).Distinct().Count() > 1)
        {
            var listed = found.OrderBy(f => f.Version).ThenBy(f => f.RelativePath, StringComparer.Ordinal)
                .Select(f => $"{f.Version}({f.RelativePath})");
            ctx.Messages.Add($"jQuery 버전이 여럿입니다: {string.Join(", ", listed)} — 가장 낮은 버전({jquery})을 기준으로 봅니다");
        }

        var unit = request.Lines is null ? WebTargetUnit.Function : WebTargetUnit.Lines;
        var display = unit == WebTargetUnit.Function ? Display(function!) : $"줄 {span.StartLine}-{span.EndLine}";
        ctx.Target = new EditTarget(EditKind.Js, ctx.FilePath, display, span, null);
        ctx.ProjectStyle = "Web";
        var pageState = page is null ? null : new WebPageState(page, blockIndex, target.View!,
            Enumerable.Range(0, page.ClientBlocks.Count).Select(i => PageBlockView.Build(page, text, i)).ToList());
        ctx.Web = new WebRunState(EditKind.Js, unit, span, target.Text, EncodingName(ctx.Original.Encoding), OriginalAscii(fs, ctx),
            siteRoot, siteIndex)
        {
            AllowSignatureChange = request.AllowSignatureChange,
            Page = pageState,
            Function = unit == WebTargetUnit.Function ? function : null,
            OriginalJs = document,
            Fallback = target.Fallback,
            FallbackReason = target.FallbackReason,
            Module = target.Detected.Module,
            OriginalLevel = target.Detected.Level,
            TargetLevel = target.TargetLevel,
            JqueryFound = found,
            Jquery = jquery,
            JquerySource = jquerySource,
            PageNames = pageScripts?.VisibleNames,
        };
        return PhaseOutcome.Ok;
    }

    private sealed record Hit(Analysis Analysis, JsFunction Function);

    /// <summary>One piece of JS the phase analyses: a whole <c>.js</c> file or one script block of a page.</summary>
    private sealed record Analysis(
        int BlockIndex, PageBlockView? View, string Text, JsLevelResult Detected, JsDocument? Document, bool Fallback,
        string? FallbackReason, EcmaVersion? TargetLevel, List<string> Notes, IReadOnlyList<JsFunction> Functions);

    /// <summary>A script block with content the run can edit: closed, inline and JavaScript.</summary>
    private static bool IsEditable(AspClientBlock block) => PageBlockView.IsJavaScriptBlock(block);

    private static PhaseOutcome Outside(RunContext ctx, string message) =>
        Refuse(ctx, FailureKinds.WebOutsideBlock, message);

    private Analysis AnalyzeBlock(AspPage page, string pageText, int blockIndex)
    {
        var view = PageBlockView.Build(page, pageText, blockIndex);
        return Analyze(view.Text, view) with { BlockIndex = blockIndex };
    }

    /// <summary>Level detection and parsing of one piece of JS; a block holding server statements goes to the W1 fallback.</summary>
    private Analysis Analyze(string text, PageBlockView? view)
    {
        var notes = new List<string>();
        var setting = options.Web.JsTarget ?? "auto";
        var configured = IsAuto(setting) ? null : JsLevels.FromSetting(setting);
        var statements = view?.ServerCode == PageServerCode.Statements;
        var detected = statements ? new JsLevelResult(null, false, null) : JsLevels.Detect(text);
        JsDocument? document = null;
        string? fallbackReason = null;
        EcmaVersion? targetLevel;
        if (detected.Level is { } level)
        {
            document = JsDocument.Parse(text, level, detected.Module);
            targetLevel = level;
            if (configured is { } wanted)
            {
                if (JsLevels.Compare(wanted, level) >= 0)
                    targetLevel = wanted;
                else
                    notes.Add($"원본이 {JsLevels.Name(level)} 문법을 써서 web.jsTarget({SettingLabel(setting, wanted)}) 대신 "
                        + $"원본 수준({JsLevels.Name(level)})을 기준으로 봅니다");
            }
        }
        else
        {
            targetLevel = configured;
            fallbackReason = statements ? "서버 코드가 섞인 스크립트" : $"원본이 해석되지 않음: 줄 {detected.LatestError?.Line ?? 1}";
            notes.Add($"JS 문법 검사 건너뜀({fallbackReason})");
        }
        var functions = document?.Functions ?? JsTokenScan.FunctionDeclarations(text);
        return new Analysis(-1, view, text, detected, document, detected.Level is null, fallbackReason, targetLevel, notes, functions);
    }

    private static bool IsAuto(string setting) => setting.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Display name of a configured level. <c>latest</c> shows as 「최신」: in Acornima 1.9.0 Latest equals ES2026, so
    /// <see cref="JsLevels.Name"/> cannot tell them apart and the label comes from the setting string.
    /// </summary>
    internal static string SettingLabel(string setting, EcmaVersion level) =>
        setting.Trim().Equals("latest", StringComparison.OrdinalIgnoreCase) ? "최신" : JsLevels.Name(level);

    private static string Display(JsFunction f) => $"function {f.QualifiedName}";

    private static string Label(JsFunction f) => $"{Display(f)} (줄 {f.StartLine}-{f.EndLine})";
}
