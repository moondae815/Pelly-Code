using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Models;
using PellyCoder.Core.Patchers;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Verification;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// Applies the blocks in memory, then lets <see cref="SqlSafetyGuard"/> judge the patched file before anything is written:
/// a parse error is a failed match (retry), a safety violation is a rejection (exit 5 when retries run out). The blocks are
/// applied to the file with the pending pull record refresh (<see cref="SqlRunState.EditBase"/>), which the guard's original
/// already carries, so that line is the only change it accepts outside the target batch.
/// </summary>
public sealed class SqlApplyPhase(FileGuard fileGuard, FeedbackBuilder feedback, IUserInteraction ui) : IPhase
{
    public RunPhase Phase => RunPhase.Apply;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Sql!;
        // The refresh replaces one line with one line, so the slice's line regions still hold.
        var patch = SearchReplacePatcher.Apply(state.EditBase(ctx.Current.Text), ctx.Blocks, ctx.SliceResult.Regions);
        if (!patch.Success)
        {
            ctx.Feedback = feedback.ForPatchFailure(patch.Failure!);
            ctx.FailureKind = patch.Failure!.Kind;
            return PhaseOutcome.MatchFailed;
        }

        var check = SqlSafetyGuard.Check(state.OriginalScript, state.TargetIndex, patch.NewText!, state.CompatLevel);
        if (check.ParseErrors.Count > 0)
        {
            var file = Path.GetFileName(ctx.FilePath);
            var errors = check.ParseErrors
                .Select(e => new Diagnostic(file, e.Line, e.Column, "error", $"SQL{e.Number}", e.Message))
                .ToList();
            foreach (var error in errors)
            {
                if (!ctx.ErrorCodes.Contains(error.Code)) ctx.ErrorCodes.Add(error.Code);
            }
            ctx.Feedback = feedback.ForDiagnostics("SQL PARSE FAILED", errors, patch.NewText!);
            ctx.FailureKind = "SqlParseErrors";
            return PhaseOutcome.MatchFailed;
        }
        if (!check.Ok)
        {
            ctx.Feedback = feedback.ForSqlSafety(check.Reason!);
            ctx.FailureKind = "SqlSafetyRejected";
            return PhaseOutcome.SafetyRejected;
        }

        // Compared with the original batch, not the last write: undoing an earlier attempt is no change either.
        if (NoChangeGuard.SqlUnchanged(state.OriginalScript, state.TargetIndex, patch.NewText!, state.CompatLevel))
        {
            ctx.Feedback = feedback.ForNoChange();
            ctx.FailureKind = FailureKinds.NoChange;
            return PhaseOutcome.MatchFailed;
        }

        if (ctx.Request.DryRun)
        {
            PatchWriter.SetStages(ctx, patch);
            ui.ShowDiff(ctx.FilePath, ctx.Original.Text, patch.NewText!);
            return PhaseOutcome.DryRunCompleted;
        }

        return await PatchWriter.WriteAsync(ctx, fileGuard, feedback, patch, ct);
    }
}
