using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Models;
using PellyCoder.Core.Patchers;
using PellyCoder.Core.Verification;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// Applies the blocks in memory, checks the member shape against the original file, then (unless dry run) waits for the baseline build, backs up once and writes atomically
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
        var shape = MemberShapeGuard.Check(ctx.Original.Text, patch.NewText!, ctx.Candidate!.Display, ctx.Language!.Version);
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
            SetStages(ctx, patch);
            ui.ShowDiff(ctx.FilePath, ctx.Original.Text, patch.NewText!);
            return PhaseOutcome.DryRunCompleted;
        }

        // The baseline must describe the untouched project, so it has to finish before the first write.
        if (await AwaitBaselineAsync(ctx, ct) is { } failed)
            return failed;

        if (ctx.BackupPath is null)
        {
            try
            {
                ctx.BackupPath = fileGuard.Backup(ctx.Workspace!, ctx.RunId, ctx.Original);
            }
            catch (InvalidOperationException)
            {
                // FileGuard.Backup throws only when the disk no longer holds the original bytes.
                return External(ctx);
            }
        }

        var write = await fileGuard.WriteAsync(ctx.Current, patch.NewText!, ct);
        switch (write.Status)
        {
            case FileWriteStatus.Written:
                ctx.Current = write.Snapshot!;
                SetStages(ctx, patch);
                return PhaseOutcome.Ok;
            case FileWriteStatus.ExternallyChanged:
                return External(ctx);
            case FileWriteStatus.Locked:
                ctx.Messages.Add($"다른 프로그램이 파일을 잠그고 있어 쓸 수 없습니다: {ctx.FilePath}");
                return PhaseOutcome.EnvError;
            case FileWriteStatus.Unencodable:
                return Mismatch(ctx, new PatchFailure(PatchFailureKinds.Unencodable, write.Detail ?? "unencodable text", null));
            default:
                throw new InvalidOperationException($"Unknown write status {write.Status}.");
        }
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
        if (run.Errors.FirstOrDefault(e => !e.Code.StartsWith("CS", StringComparison.Ordinal)) is { } blocker)
        {
            ctx.Messages.Add($"빌드 검증을 할 수 없습니다: {blocker.Code} {blocker.Message}. "
                + "프로젝트를 복원(dotnet restore 또는 Visual Studio 복원)하고 빌드되는지 먼저 확인하세요");
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

    private PhaseOutcome Mismatch(RunContext ctx, PatchFailure failure)
    {
        ctx.Feedback = feedback.ForPatchFailure(failure);
        ctx.FailureKind = failure.Kind;
        return PhaseOutcome.MatchFailed;
    }

    private static PhaseOutcome External(RunContext ctx)
    {
        ctx.Messages.Add($"모델이 생성하는 동안 파일이 다른 곳에서 바뀌어 덮어쓰지 않았습니다: {ctx.FilePath}");
        return PhaseOutcome.FileChangedExternally;
    }

    private static void SetStages(RunContext ctx, PatchOutcome patch)
    {
        ctx.MatchStages.Clear();
        ctx.MatchStages.AddRange(patch.Stages);
    }
}
