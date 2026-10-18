using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Models;
using PellyCoder.Core.Patchers;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// The write half of APPLY shared by C# and SQL: backs up once before the first write, writes atomically with external-change
/// detection, maps the write status to an outcome and records the match stages.
/// </summary>
public static class PatchWriter
{
    public static async Task<PhaseOutcome> WriteAsync(RunContext ctx, FileGuard fileGuard, FeedbackBuilder feedback, PatchOutcome patch, CancellationToken ct)
    {
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
                var failure = new PatchFailure(PatchFailureKinds.Unencodable, write.Detail ?? "unencodable text", null);
                ctx.Feedback = feedback.ForPatchFailure(failure);
                ctx.FailureKind = failure.Kind;
                return PhaseOutcome.MatchFailed;
            default:
                throw new InvalidOperationException($"Unknown write status {write.Status}.");
        }
    }

    internal static void SetStages(RunContext ctx, PatchOutcome patch)
    {
        ctx.MatchStages.Clear();
        ctx.MatchStages.AddRange(patch.Stages);
    }

    private static PhaseOutcome External(RunContext ctx)
    {
        ctx.Messages.Add($"모델이 생성하는 동안 파일이 다른 곳에서 바뀌어 덮어쓰지 않았습니다: {ctx.FilePath}");
        return PhaseOutcome.FileChangedExternally;
    }
}
