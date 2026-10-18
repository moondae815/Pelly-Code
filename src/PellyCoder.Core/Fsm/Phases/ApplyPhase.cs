using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Models;
using PellyCoder.Core.Patchers;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Verification;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// Applies the blocks in memory, checks the member shape against the original file, then (unless dry run) waits for the baseline build and tests, backs up once and writes atomically
/// with external-change detection.
/// </summary>
public sealed class ApplyPhase(FileGuard fileGuard, FeedbackBuilder feedback, IUserInteraction ui, BuildOptions build) : IPhase
{
    public RunPhase Phase => RunPhase.Apply;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var patch = SearchReplacePatcher.Apply(ctx.Current.Text, ctx.Blocks, ctx.SliceResult.Regions);
        if (!patch.Success)
            return Mismatch(ctx, patch.Failure!);

        // Before the dry-run branch and the baseline wait: a rejected shape must not wait for or touch anything.
        // Compared with the original (not the last write) so helpers added by earlier attempts stay editable.
        ShapeCheck shape;
        try
        {
            shape = MemberShapeGuard.Check(ctx.Original.Text, patch.NewText!, ctx.Candidate!.Display, ctx.Language!.Version);
        }
        catch (ShapeTargetNotFoundException ex)
        {
            ctx.Messages.Add($"대상 멤버 '{ex.TargetDisplay}'을(를) 원본에서 찾을 수 없습니다");
            ctx.FailureKind = "TargetNotFound";
            return PhaseOutcome.NotFound;
        }
        if (!shape.Ok)
        {
            ctx.Feedback = feedback.ForShapeRejection(shape.Reason!);
            ctx.FailureKind = "ShapeRejected";
            return PhaseOutcome.ShapeRejected;
        }
        foreach (var helper in shape.AddedPrivateMethods)
        {
            if (!ctx.AddedHelpers.Contains(helper)) ctx.AddedHelpers.Add(helper);
        }

        if (ctx.Request.DryRun)
        {
            PatchWriter.SetStages(ctx, patch);
            ui.ShowDiff(ctx.FilePath, ctx.Original.Text, patch.NewText!);
            return PhaseOutcome.DryRunCompleted;
        }

        // The baseline build and tests must describe the untouched project, so they have to finish before the first write.
        if (await AwaitBaselineAsync(ctx, ct) is { } failed)
            return failed;
        await AwaitBaselineTestsAsync(ctx, ct);

        return await PatchWriter.WriteAsync(ctx, fileGuard, feedback, patch, ct);
    }

    /// <summary>Null when the run may write; otherwise the outcome to end it with. Runs the checks once per run.</summary>
    private async Task<PhaseOutcome?> AwaitBaselineAsync(RunContext ctx, CancellationToken ct)
    {
        if (ctx.Baseline is null || ctx.BaselineSnapshot is not null) return null;

        BuildRun run;
        try
        {
            run = await ctx.Baseline.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // user cancel: the orchestrator maps it to exit 2
        }
        catch (ProcessStartException ex)
        {
            ctx.Messages.Add(BuildMessages.CannotStart(ctx.BuildTool!, ex));
            return PhaseOutcome.EnvError;
        }
        catch (Exception ex)
        {
            ctx.Messages.Add($"원본 상태 빌드에 실패했습니다 ({ctx.BuildTool!.Executable}): {ex.GetType().Name}: {ex.Message}");
            return PhaseOutcome.EnvError;
        }

        if (run.TimedOut)
        {
            ctx.Messages.Add($"원본 상태 빌드가 {build.TimeoutSeconds}초 안에 끝나지 않았습니다 (build.timeoutSeconds)");
            return PhaseOutcome.EnvError;
        }

        // A non-compiler error (NETSDK1004 missing restore, MSB*, NU*, the synthetic BUILD) means the compiler never ran:
        // every later build would fail the same way and each edit would pass vacuously. Checked regardless of --strict-baseline.
        if (run.Errors.FirstOrDefault(e => !BuildMessages.IsCompilerError(e)) is { } blocker)
        {
            ctx.Messages.Add(BuildMessages.Unverifiable(blocker, "프로젝트를 복원(dotnet restore 또는 Visual Studio 복원)하고 빌드되는지 먼저 확인하세요"));
            ctx.FailureKind = "BuildUnverifiable";
            return PhaseOutcome.EnvError;
        }

        if (run.Errors.Count > 0)
        {
            if (build.StrictBaseline)
            {
                ctx.Messages.Add($"원본 상태에서 이미 빌드 오류가 {run.Errors.Count}개 있습니다 (--strict-baseline)");
                ctx.FailureKind = "BaselineErrors";
                return PhaseOutcome.EnvError;
            }
            ctx.Messages.Add($"원본 상태에서 이미 빌드 오류가 {run.Errors.Count}개 있습니다 — 새로 생긴 오류만 검사합니다");
        }

        ctx.BaselineSnapshot = new BaselineSnapshot(run);
        return null;
    }

    /// <summary>
    /// Waits for the baseline tests once per run. Tests that could not run are announced and skipped by the test step (the
    /// build still verifies the edit): failing the run here would keep <c>--test</c> from ever editing such a project.
    /// </summary>
    private async Task AwaitBaselineTestsAsync(RunContext ctx, CancellationToken ct)
    {
        if (ctx.BaselineTests is null || ctx.BaselineTestRuns is not null) return;

        var usable = new TestRun?[ctx.TestProjects.Count];
        IReadOnlyList<TestRun> runs;
        try
        {
            runs = await ctx.BaselineTests.WaitAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // user cancel: the orchestrator maps it to exit 2
        }
        catch (Exception ex)
        {
            ctx.Messages.Add($"{CannotRunBaselineTests}: {ex.GetType().Name}: {ex.Message}");
            ctx.BaselineTestRuns = usable;
            return;
        }

        var reasons = new List<string>();
        for (var i = 0; i < usable.Length && i < runs.Count; i++)
        {
            var reason = runs[i] switch
            {
                { Error: { } error } => error,
                { TimedOut: true } => TestStep.TimedOutMessage(ctx.Options.Test),
                _ => null,
            };
            if (reason is null) usable[i] = runs[i];
            else if (!reasons.Contains(reason)) reasons.Add(reason);
        }
        foreach (var reason in reasons) ctx.Messages.Add($"{CannotRunBaselineTests}: {reason}");
        // Still run at VERIFY (a new compile error in the test project is feedback), but never compared: say so once.
        for (var i = 0; i < usable.Length; i++)
        {
            if (usable[i] is { BuildErrors.Count: > 0 })
                ctx.Messages.Add($"{TestStep.BaselineNotBuilt}: {Path.GetFileName(ctx.TestProjects[i])}");
        }
        ctx.BaselineTestRuns = usable;
    }

    private const string CannotRunBaselineTests = "원본 상태 테스트를 실행하지 못해 테스트 판정을 건너뜁니다";

    private PhaseOutcome Mismatch(RunContext ctx, PatchFailure failure)
    {
        ctx.Feedback = feedback.ForPatchFailure(failure);
        ctx.FailureKind = failure.Kind;
        return PhaseOutcome.MatchFailed;
    }
}
