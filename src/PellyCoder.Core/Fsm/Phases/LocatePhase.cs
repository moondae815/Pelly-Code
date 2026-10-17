using System.IO.Abstractions;
using System.Xml;
using System.Xml.Linq;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>Resolves the project, its C# version and the single member to edit.</summary>
public sealed class LocatePhase(IFileSystem fs, IUserInteraction ui) : IPhase
{
    public RunPhase Phase => RunPhase.Locate;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var csproj = ProjectFileLocator.FindNearestCsproj(fs, ctx.FilePath);
        try
        {
            ctx.Language = LanguageVersionDetector.Detect(fs, csproj);
            ctx.ProjectStyle = csproj is null ? "None" : StyleOf(XDocument.Parse(fs.File.ReadAllText(csproj)).Root);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            ctx.Messages.Add($"프로젝트 파일을 읽을 수 없습니다: {csproj} ({ex.Message})");
            return PhaseOutcome.EnvError;
        }

        // Baseline for VERIFY (design §8.9): only errors beyond these count. Parsed once, with the detected version.
        ctx.OriginalSyntax = SyntaxBaseline.Of(ctx.Original.Text, ctx.Language.Version, fs.Path.GetFileName(ctx.FilePath));
        if (ctx.OriginalSyntax.Errors.Count > 0)
            ctx.Messages.Add($"원본 파일에 이미 구문 오류가 {ctx.OriginalSyntax.Errors.Count}개 있습니다 — 새로 생긴 오류만 검사합니다");

        var name = ctx.Request.ClassName is null ? ctx.Request.MethodName : $"{ctx.Request.ClassName}.{ctx.Request.MethodName}";
        var candidates = MemberLocator.FindByName(ctx.Current.Text, ctx.Language.Version, ctx.Request.ClassName, ctx.Request.MethodName);
        MemberCandidate chosen;
        switch (candidates.Count)
        {
            case 0:
                ctx.Messages.Add($"대상 멤버 '{name}'을(를) 찾을 수 없습니다: {ctx.FilePath}");
                ctx.FailureKind = "TargetNotFound";
                return PhaseOutcome.NotFound;
            case 1:
                chosen = candidates[0];
                break;
            default:
                if (!ui.IsInteractive)
                {
                    ctx.Messages.Add($"'{name}'과(와) 일치하는 멤버가 {candidates.Count}개입니다. --class로 좁히거나 대화형으로 실행하세요: "
                        + string.Join(", ", candidates.Select(Label)));
                    ctx.FailureKind = "TargetAmbiguous";
                    return PhaseOutcome.Ambiguous;
                }
                chosen = await ui.SelectAsync("수정할 멤버를 고르세요", candidates, Label, ct);
                break;
        }

        ctx.Candidate = chosen;
        ctx.Target = new EditTarget(EditKind.CSharp, ctx.FilePath, chosen.Display, chosen.Span, csproj);
        return PhaseOutcome.Ok;
    }

    private static string Label(MemberCandidate c) => $"{c.Display} (줄 {c.Span.StartLine}-{c.Span.EndLine})";

    /// <summary>Same rule as <see cref="LanguageVersionDetector"/>: SDK style has an <c>Sdk</c> attribute or element.</summary>
    private static string StyleOf(XElement? root) =>
        root is not null && (root.Attribute("Sdk") is not null || root.Elements().Any(e => e.Name.LocalName == "Sdk"))
            ? "Sdk"
            : "Legacy";
}
