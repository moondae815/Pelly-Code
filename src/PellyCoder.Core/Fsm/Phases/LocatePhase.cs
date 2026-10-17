using System.IO.Abstractions;
using System.Xml;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// Resolves the project, its C# version and the single member to edit, then the build tool. Unless this is a dry run
/// it starts the baseline build of the untouched project, which runs while SLICE and EDIT proceed (design §7.3).
/// </summary>
public sealed class LocatePhase(
    IFileSystem fs, IUserInteraction ui, BuildToolResolver tools, BuildVerifier builds, Func<bool>? isWindows = null) : IPhase
{
    private readonly Func<bool> _isWindows = isWindows ?? OperatingSystem.IsWindows;

    public RunPhase Phase => RunPhase.Locate;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var csproj = ProjectFileLocator.FindNearestCsproj(fs, ctx.FilePath);
        try
        {
            ctx.Language = LanguageVersionDetector.Detect(fs, csproj);
            ctx.ProjectStyle = ProjectStyleDetector.Detect(fs, csproj).ToString();
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
        return await StartBaselineAsync(ctx, csproj, ct);
    }

    private async Task<PhaseOutcome> StartBaselineAsync(RunContext ctx, string? csproj, CancellationToken ct)
    {
        var style = Enum.Parse<ProjectStyle>(ctx.ProjectStyle);
        if (style == ProjectStyle.None || csproj is null)
        {
            ctx.Messages.Add("csproj가 없어 빌드 검증을 건너뜁니다 (구문 검사만 합니다)");
            return PhaseOutcome.Ok;
        }
        if (style == ProjectStyle.Legacy && !_isWindows())
        {
            // MSBuild for .NET Framework exists only on Windows: verify syntax only, like a project without csproj.
            ctx.Messages.Add("레거시 .NET Framework 프로젝트는 Windows에서만 빌드 검증합니다 — 구문 검사만 합니다");
            return PhaseOutcome.Ok;
        }

        var resolution = await tools.ResolveAsync(style, ct);
        if (resolution.Tool is not { } tool)
        {
            ctx.Messages.Add(resolution.Error!);
            ctx.FailureKind = "BuildToolUnavailable";
            return PhaseOutcome.EnvError;
        }

        ctx.BuildTool = tool;
        if (ctx.Request.DryRun) return PhaseOutcome.Ok; // a dry run never builds

        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        ctx.BaselineCancellation = cts;
        // Started only; APPLY awaits it. The orchestrator cancels it and waits for it when the run ends.
        ctx.Baseline = Task.Run(() => builds.BuildAsync(tool, csproj, ctx.Workspace!.Root, cts.Token), CancellationToken.None);
        return PhaseOutcome.Ok;
    }

    private static string Label(MemberCandidate c) => $"{c.Display} (줄 {c.Span.StartLine}-{c.Span.EndLine})";
}
