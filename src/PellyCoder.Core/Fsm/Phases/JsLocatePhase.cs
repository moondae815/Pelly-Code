using PellyCoder.Core.Files;
using System.IO.Abstractions;
using Acornima;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Models;
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
        if (RefuseFile(fs, ctx, options, siteRoot, ".js", "pelly js") is { } refused)
            return refused;

        var text = ctx.Original.Text;
        var detected = JsLevels.Detect(text);
        var fallback = detected.Level is null;
        JsDocument? document = null;
        string? fallbackReason = null;
        EcmaVersion? targetLevel;
        var setting = options.Web.JsTarget ?? "auto";
        var configured = IsAuto(setting) ? null : JsLevels.FromSetting(setting);
        if (detected.Level is { } level)
        {
            document = JsDocument.Parse(text, level, detected.Module);
            targetLevel = level;
            if (configured is { } wanted)
            {
                if (JsLevels.Compare(wanted, level) >= 0)
                    targetLevel = wanted;
                else
                    ctx.Messages.Add($"원본이 {JsLevels.Name(level)} 문법을 써서 web.jsTarget({SettingLabel(setting, wanted)}) 대신 "
                        + $"원본 수준({JsLevels.Name(level)})을 기준으로 봅니다");
            }
        }
        else
        {
            targetLevel = configured;
            fallbackReason = $"원본이 해석되지 않음: 줄 {detected.LatestError?.Line ?? 1}";
            ctx.Messages.Add($"JS 문법 검사 건너뜀({fallbackReason})");
        }

        var functions = document?.Functions ?? JsTokenScan.FunctionDeclarations(text);
        var lineCount = LineCount(text);
        JsFunction? function = null;
        TextSpanInfo span;
        if (request.MethodName is { } name)
        {
            var matches = functions.Where(f => f.QualifiedName == name || f.Name == name).ToList();
            if (matches.Count == 0)
                return NotFound(ctx, $"함수 '{name}'을(를) 찾을 수 없습니다: {ctx.FilePath}");
            function = await ChooseAsync(ui, ctx, matches, "수정할 함수를 고르십시오", Label,
                $"'{name}' 함수가 {matches.Count}개입니다. 대화형으로 실행해 고르거나 --lines A-B로 범위를 주십시오: ", ct);
            if (function is null)
                return NoChoice(ctx);
        }
        else if (request.Line is { } line)
        {
            if (fallback)
                return NotFound(ctx, $"원본이 해석되지 않아 {line}번 줄을 감싸는 함수를 찾을 수 없습니다. --lines A-B로 범위를 주십시오");
            function = functions
                .Where(f => f.StartLine <= line && line <= f.EndLine)
                .OrderBy(f => f.EndLine - f.StartLine)
                .FirstOrDefault();
            if (function is null)
                return NotFound(ctx, $"{line}번 줄을 감싸는 이름 있는 함수가 없습니다. --lines A-B로 범위를 주십시오");
        }
        else if (request.Lines is null)
        {
            if (functions.Count == 0)
                return NotFound(ctx, "파일에 이름 있는 함수가 없습니다. --lines A-B로 범위를 주십시오");
            function = await ChooseAsync(ui, ctx, functions, "수정할 함수를 고르십시오", Label,
                $"이름 있는 함수가 {functions.Count}개입니다. --function으로 지정하십시오: ", ct);
            if (function is null)
                return NoChoice(ctx);
        }

        if (request.Lines is { } lines)
        {
            if (OutOfFile(lines, lineCount))
                return NotFound(ctx, OutOfFileMessage(lines, lineCount));
            if (document is not null && document.CutsStatement(lines))
                return NotFound(ctx, $"줄 {lines.StartLine}-{lines.EndLine} 범위가 문장 중간을 자릅니다. 문장 경계로 범위를 주십시오");
            span = lines;
        }
        else
        {
            span = new TextSpanInfo(function!.StartLine, function.EndLine);
        }

        var found = JqueryVersions.Find(fs, siteRoot);
        var jquery = JqueryVersions.Baseline(found, options.Web.JqueryVersion);
        if (string.IsNullOrWhiteSpace(options.Web.JqueryVersion) && found.Select(f => f.Version).Distinct().Count() > 1)
        {
            var listed = found.OrderBy(f => f.Version).ThenBy(f => f.RelativePath, StringComparer.Ordinal)
                .Select(f => $"{f.Version}({f.RelativePath})");
            ctx.Messages.Add($"jQuery 버전이 여럿입니다: {string.Join(", ", listed)} — 가장 낮은 버전({jquery})을 기준으로 봅니다");
        }

        var unit = request.Lines is null ? WebTargetUnit.Function : WebTargetUnit.Lines;
        var display = unit == WebTargetUnit.Function ? Display(function!) : $"줄 {span.StartLine}-{span.EndLine}";
        ctx.Target = new EditTarget(EditKind.Js, ctx.FilePath, display, span, null);
        ctx.ProjectStyle = "Web";
        ctx.Web = new WebRunState(EditKind.Js, unit, span, text, EncodingName(ctx.Original.Encoding), OriginalAscii(fs, ctx),
            siteRoot, SiteIndex(fs, ctx, options, siteRoot, codec))
        {
            Function = unit == WebTargetUnit.Function ? function : null,
            OriginalJs = document,
            Fallback = fallback,
            FallbackReason = fallbackReason,
            Module = detected.Module,
            OriginalLevel = detected.Level,
            TargetLevel = targetLevel,
            JqueryFound = found,
            Jquery = jquery,
        };
        return PhaseOutcome.Ok;
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
