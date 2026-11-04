using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// Finds the ASP target (design §3, §4.1): the procedure named by <c>--proc</c>, the procedure around <c>--line</c>, the
/// <c>--lines</c> range as given, or the only procedure in the file (several: selection UI, or exit 3 when not interactive —
/// no model pick). Refuses <c>global.asa</c>, other extensions, non-VBScript pages, server JScript in the target range and
/// ranges the parser could not read (an unclosed <c>&lt;%</c> or server script).
/// </summary>
public sealed class AspLocatePhase(IFileSystem fs, IUserInteraction ui, AspOptions options, AspTextCodec codec) : IPhase
{
    public RunPhase Phase => RunPhase.Locate;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var request = ctx.Request;
        var given = (request.MethodName is not null ? 1 : 0) + (request.Line is not null ? 1 : 0) + (request.Lines is not null ? 1 : 0);
        if (given > 1)
        {
            ctx.Messages.Add("--proc, --line, --lines는 하나만 쓸 수 있습니다");
            return PhaseOutcome.EnvError;
        }

        var fileName = fs.Path.GetFileName(ctx.FilePath);
        if (fileName.Equals("global.asa", StringComparison.OrdinalIgnoreCase))
            return Refuse(ctx, FailureKinds.AspUnsupportedFile, $"global.asa는 pelly asp로 편집할 수 없습니다: {ctx.FilePath}");
        var extension = fs.Path.GetExtension(ctx.FilePath);
        if (!options.Extensions.Any(e => e.Equals(extension, StringComparison.OrdinalIgnoreCase)))
            return Refuse(ctx, FailureKinds.AspUnsupportedFile,
                $"ASP 대상 확장자가 아닙니다(asp.extensions: {string.Join(", ", options.Extensions)}): {ctx.FilePath}");

        var page = AspPage.Parse(ctx.Original.Text);
        if (!IsVbScript(page.Language))
            return Refuse(ctx, FailureKinds.AspUnsupportedLanguage, $"VBScript가 아닌 페이지는 지원하지 않습니다(Language={page.Language})");

        var lineCount = LineCount(ctx.Original.Text);
        AspProcedure? procedure = null;
        TextSpanInfo span;
        if (request.MethodName is { } wanted)
        {
            var matches = Matching(page, wanted);
            if (matches.Count == 0)
                return NotFound(ctx, $"프로시저 '{wanted}'을(를) 찾을 수 없습니다: {ctx.FilePath}");
            procedure = await ChooseAsync(ctx, matches, ct);
        }
        else if (request.Line is { } line)
        {
            procedure = page.Procedures
                .Where(p => p.StartLine <= line && line <= p.EndLine)
                .OrderBy(p => p.EndLine - p.StartLine)
                .FirstOrDefault();
            if (procedure is null)
                return NotFound(ctx, $"{line}번 줄은 Function·Sub 안이 아닙니다. --lines A-B로 범위를 주십시오");
        }
        else if (request.Lines is null)
        {
            if (page.Procedures.Count == 0)
                return NotFound(ctx, "파일에 Function·Sub가 없습니다. --lines A-B로 범위를 주십시오");
            procedure = await ChooseAsync(ctx, page.Procedures, ct);
        }

        if (request.Lines is { } lines)
        {
            if (lines.StartLine < 1 || lines.EndLine < lines.StartLine || lines.EndLine > lineCount)
                return NotFound(ctx, $"줄 {lines.StartLine}-{lines.EndLine}이(가) 파일 범위(1-{lineCount}) 밖입니다");
            span = lines;
        }
        else if (procedure is null)
        {
            return ctx.FailureKind == FailureKinds.TargetAmbiguous ? PhaseOutcome.Ambiguous : PhaseOutcome.NotFound;
        }
        else
        {
            span = new TextSpanInfo(procedure.StartLine, procedure.EndLine);
        }

        if (HasServerJScriptIn(page, span))
            return Refuse(ctx, FailureKinds.AspUnsupportedLanguage, "대상 범위에 서버 JScript 블록이 있어 편집할 수 없습니다");

        // An unclosed block runs from where it opens to the end of the file: a span reaching it cannot be read.
        if (page.Problems.FirstOrDefault(p => p.Code is "ASP001" or "ASP002" && span.EndLine >= p.Line) is { } broken)
            return NotFound(ctx, $"대상 범위를 해석할 수 없습니다: {broken.Message} (줄 {broken.Line})");

        var root = ctx.Workspace!.Root;
        var siteRoot = options.SiteRoot is { Length: > 0 } configured
            ? fs.Path.GetFullPath(fs.Path.IsPathRooted(configured) ? configured : fs.Path.Combine(root, configured))
            : root;
        var includes = AspIncludeResolver.Resolve(fs, ctx.FilePath, page, siteRoot, codec);
        if (includes.Unresolved.Count > 0)
            ctx.Messages.Add($"include를 찾지 못했습니다: {string.Join(", ", includes.Unresolved)} (asp.siteRoot 확인)");

        var display = procedure is not null && request.Lines is null ? Display(procedure) : $"줄 {span.StartLine}-{span.EndLine}";
        ctx.Target = new EditTarget(EditKind.Asp, ctx.FilePath, display, span, null);
        ctx.ProjectStyle = "Asp";
        ctx.Asp = new AspRunState(page, request.Lines is null ? AspTargetUnit.Procedure : AspTargetUnit.Lines, span,
            request.Lines is null ? procedure : null, includes, EncodingName(ctx.Original.Encoding), siteRoot)
        {
            AdoConstants = options.AdoConstants || GlobalAsaHasTypeLib(siteRoot),
        };
        return PhaseOutcome.Ok;
    }

    /// <summary><c>&lt;siteRoot&gt;/global.asa</c> declares a typelib METADATA (ADO constants for every page); read only.</summary>
    private bool GlobalAsaHasTypeLib(string siteRoot)
    {
        var path = fs.Path.Combine(siteRoot, "global.asa");
        try
        {
            return fs.File.Exists(path)
                && AspNameCheck.HasTypeLibMetadata(AspPage.Parse(codec.Decode(path, fs.File.ReadAllBytes(path)).Text));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return false;
        }
    }

    /// <summary><c>Class.Name</c> matches that class member; a bare name matches top-level procedures, else class members.</summary>
    private static List<AspProcedure> Matching(AspPage page, string wanted)
    {
        var dot = wanted.IndexOf('.');
        if (dot >= 0)
        {
            var className = wanted[..dot];
            var name = wanted[(dot + 1)..];
            return page.Procedures.Where(p => Same(p.ClassName, className) && Same(p.Name, name)).ToList();
        }
        var named = page.Procedures.Where(p => Same(p.Name, wanted)).ToList();
        var topLevel = named.Where(p => p.ClassName is null).ToList();
        return topLevel.Count > 0 ? topLevel : named;
    }

    private static bool Same(string? a, string b) => a is not null && a.Equals(b, StringComparison.OrdinalIgnoreCase);

    /// <summary>One candidate is the target; several go to the selection UI, or fail as ambiguous when not interactive.</summary>
    private async Task<AspProcedure?> ChooseAsync(RunContext ctx, IReadOnlyList<AspProcedure> candidates, CancellationToken ct)
    {
        if (candidates.Count == 1)
            return candidates[0];
        if (!ui.IsInteractive)
        {
            ctx.Messages.Add($"프로시저가 {candidates.Count}개입니다. --proc로 지정하십시오: "
                + string.Join(", ", candidates.Select(QualifiedName)));
            ctx.FailureKind = FailureKinds.TargetAmbiguous;
            return null;
        }
        return await ui.SelectAsync("수정할 프로시저를 고르십시오", candidates, p => $"{Display(p)} (줄 {p.StartLine}-{p.EndLine})", ct);
    }

    private static string QualifiedName(AspProcedure p) => p.ClassName is null ? p.Name : $"{p.ClassName}.{p.Name}";

    private static string Display(AspProcedure p) => $"{p.Keyword} {QualifiedName(p)}";

    private static bool IsVbScript(string language) =>
        language.Equals("VBScript", StringComparison.OrdinalIgnoreCase) || language.Equals("VBS", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when a <c>&lt;script runat="server" language="JScript"&gt;</c> block overlaps <paramref name="span"/>.</summary>
    private static bool HasServerJScriptIn(AspPage page, TextSpanInfo span)
    {
        if (!page.HasServerJScript)
            return false;
        foreach (var segment in page.Segments)
        {
            if (segment.Kind != AspSegmentKind.ServerScript || segment.EndLine < span.StartLine || segment.StartLine > span.EndLine)
                continue;
            var language = AspPage.Attributes(OpeningTag(page, segment)).GetValueOrDefault("language") ?? "";
            if (language.Equals("JScript", StringComparison.OrdinalIgnoreCase) || language.Equals("JavaScript", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>Text of the segment's opening tag (from its <c>&lt;</c> to the first <c>&gt;</c>, across lines).</summary>
    private static string OpeningTag(AspPage page, AspSegment segment)
    {
        var tag = new StringBuilder();
        for (var line = segment.StartLine; line <= segment.EndLine; line++)
        {
            var text = page.LineText(line);
            if (line == segment.StartLine)
                text = text[Math.Min(segment.StartColumn - 1, text.Length)..];
            var gt = text.IndexOf('>');
            if (gt >= 0)
                return tag.Append(text, 0, gt).ToString();
            tag.Append(text).Append('\n');
        }
        return tag.ToString();
    }

    private static int LineCount(string textLf)
    {
        var count = textLf.Split('\n').Length;
        return textLf.EndsWith('\n') ? count - 1 : count;
    }

    private static string EncodingName(Encoding encoding) => encoding.CodePage switch
    {
        949 => "CP949",
        65001 => "UTF-8",
        _ => encoding.WebName.ToUpperInvariant(),
    };

    private static PhaseOutcome Refuse(RunContext ctx, string failureKind, string message)
    {
        ctx.Messages.Add(message);
        ctx.FailureKind = failureKind;
        return PhaseOutcome.NotFound;
    }

    private static PhaseOutcome NotFound(RunContext ctx, string message) => Refuse(ctx, FailureKinds.TargetNotFound, message);
}
