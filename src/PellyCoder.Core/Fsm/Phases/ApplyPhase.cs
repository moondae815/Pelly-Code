using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Models;
using PellyCoder.Core.Patchers;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>Applies the blocks in memory, then (unless dry run) backs up once and writes atomically with external-change detection.</summary>
public sealed class ApplyPhase(FileGuard fileGuard, FeedbackBuilder feedback, IUserInteraction ui) : IPhase
{
    public RunPhase Phase => RunPhase.Apply;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var patch = SearchReplacePatcher.Apply(ctx.Current.Text, ctx.Blocks, ctx.SliceResult.Regions);
        if (!patch.Success)
            return Mismatch(ctx, patch.Failure!);

        if (ctx.Request.DryRun)
        {
            SetStages(ctx, patch);
            ui.ShowDiff(ctx.FilePath, ctx.Original.Text, patch.NewText!);
            return PhaseOutcome.DryRunCompleted;
        }

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
