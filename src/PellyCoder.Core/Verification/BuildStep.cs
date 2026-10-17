using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Verification;

/// <summary>
/// Builds the project with the edit applied and fails only on errors the baseline build did not have (design §8.9).
/// New warnings are a notice, or failures with <c>build.warnAsError</c>.
/// </summary>
public sealed class BuildStep(BuildVerifier builds, FeedbackBuilder feedback, BuildOptions options) : IVerifierStep
{
    public string Name => "Build";

    public async Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        if (ctx.BuildTool is not { } tool) return StepResult.Pass; // no project to build (or dry run never gets here)
        var baseline = ctx.BaselineSnapshot
            ?? throw new InvalidOperationException("APPLY must await the baseline build before VERIFY builds.");

        BuildRun run;
        try
        {
            run = await builds.BuildAsync(tool, ctx.Target!.ProjectPath!, ctx.Workspace!.Root, ct);
        }
        catch (ProcessStartException ex)
        {
            return StepResult.Environment(BuildMessages.CannotStart(tool, ex));
        }

        if (run.TimedOut)
            return StepResult.Environment($"빌드가 {options.TimeoutSeconds}초 안에 끝나지 않았습니다 (build.timeoutSeconds)");

        var newErrors = BaselineDiff.New(baseline.Run.Errors, run.Errors);
        var newWarnings = BaselineDiff.New(baseline.Run.Warnings, run.Warnings);
        IReadOnlyList<Diagnostic> failing = newErrors;
        if (options.WarnAsError)
            failing = [.. newErrors, .. newWarnings];
        else if (newWarnings.Count > 0)
            ctx.Messages.Add($"새 경고 {newWarnings.Count}개: {string.Join(", ", newWarnings.Select(w => w.Code).Distinct())}");

        if (failing.Count == 0) return new StepResult(true, null, [], newWarnings, false, null);

        var target = Path.GetRelativePath(ctx.Workspace.Root, ctx.FilePath).Replace('\\', '/');
        return StepResult.Fail(feedback.ForBuild(failing, target, ctx.Current.Text), failing, newWarnings);
    }
}

/// <summary>User-facing build messages shared by APPLY (baseline) and the build step.</summary>
public static class BuildMessages
{
    public static string CannotStart(BuildTool tool, Exception ex) =>
        $"빌드 도구를 실행할 수 없습니다: {tool.Executable} ({ex.InnerException?.Message ?? ex.Message}). pelly doctor로 확인하세요";
}
