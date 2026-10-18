using PellyCoder.Core.Fsm;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Verification;

/// <summary>The SQL steps' shared view of the target batch as it is in the file now.</summary>
internal static class SqlVerifyTarget
{
    /// <summary>
    /// Re-splits the written file: <see cref="SqlRunState.CurrentTarget"/> is set by SLICE, before the edit,
    /// so after APPLY it still holds the previous text. APPLY's safety guard keeps the batch count and target index.
    /// </summary>
    public static SqlBatch Current(RunContext ctx, SqlRunState state)
    {
        var script = SqlBatchSplitter.Split(ctx.Current.Text, state.CompatLevel);
        var batch = script.Batches[state.TargetIndex];
        state.CurrentTarget = batch;
        return batch;
    }
}
