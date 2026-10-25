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
        // A new non-compiler error (MSB3027 locked output, MSB*, NETSDK*, NU*, the synthetic BUILD) is not something the
        // model can fix by editing the member: end the run as an environment error (the orchestrator restores the file).
        if (newErrors.FirstOrDefault(e => !BuildMessages.IsCompilerError(e)) is { } blocker)
            return StepResult.Environment(BuildMessages.Unverifiable(blocker, "프로젝트가 빌드되는지 먼저 확인하세요"), "BuildUnverifiable");

        var newWarnings = BaselineDiff.New(baseline.Run.Warnings, run.Warnings);
        IReadOnlyList<Diagnostic> failing = newErrors;
        if (options.WarnAsError)
            failing = [.. newErrors, .. newWarnings];
        // Warnings exist only when the build prints them: the default SDK command uses ErrorsOnly, so in practice this
        // notice appears with build.warnAsError (NoSummary only) or a tool that prints warnings anyway.
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
    /// <summary>MSBuild copy failures caused by a locked output file (a running program or debugger holds the dll).</summary>
    private static readonly HashSet<string> LockCodes = new(StringComparer.Ordinal) { "MSB3021", "MSB3026", "MSB3027" };

    public static bool IsCompilerError(Diagnostic d) => d.Code.StartsWith("CS", StringComparison.Ordinal);

    /// <summary>
    /// The message for an error that keeps the build from verifying the edit. Locked outputs get their own hint;
    /// <paramref name="otherwiseHint"/> ends the message for every other code.
    /// </summary>
    public static string Unverifiable(Diagnostic blocker, string otherwiseHint) =>
        LockCodes.Contains(blocker.Code)
            ? $"빌드 출력 파일이 잠겨 있어 빌드 검증을 할 수 없습니다: {blocker.Code} {blocker.Message}. 실행 중인 프로그램(디버거 포함)을 닫고 다시 시도하세요"
            : $"빌드 검증을 할 수 없습니다: {blocker.Code} {blocker.Message}. {otherwiseHint}";

    /// <summary>
    /// The same cause without the "빌드 검증을 할 수 없습니다" lead, for a caller whose own prefix already says the build
    /// cannot verify (a group's "그룹 빌드 검증 불가 (project): ").
    /// </summary>
    public static string BlockerDetail(Diagnostic blocker, string otherwiseHint) =>
        LockCodes.Contains(blocker.Code)
            ? $"빌드 출력 파일이 잠겨 있습니다: {blocker.Code} {blocker.Message.TrimEnd('.')}. 실행 중인 프로그램(디버거 포함)을 닫고 다시 시도하세요"
            : $"{blocker.Code} {blocker.Message.TrimEnd('.')}. {otherwiseHint}";

    public static string CannotStart(BuildTool tool, Exception ex) =>
        $"빌드 도구를 실행할 수 없습니다: {tool.Executable} ({ex.InnerException?.Message ?? ex.Message}). pelly doctor로 확인하세요";
}
