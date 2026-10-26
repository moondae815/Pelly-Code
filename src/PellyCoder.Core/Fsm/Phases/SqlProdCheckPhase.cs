using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// Compares the target batch with the production definition before the model is called (design 6.3). Production changed since
/// the pull stops the run (exit 4, <c>SqlProdChanged</c>); every other outcome only informs. Reads production only.
/// Without a production connection nothing happens and nothing is recorded. The orchestrator keys phases by <see cref="RunPhase"/>,
/// so this step wraps LOCATE and runs right after it succeeded (before SLICE), like <see cref="SqlPreflightPhase"/> wraps PREFLIGHT.
/// </summary>
public sealed class SqlProdCheckPhase(IPhase locate, SqlReaderOpen prod) : IPhase
{
    public RunPhase Phase => RunPhase.Locate;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var located = await locate.ExecuteAsync(ctx, ct);
        if (located != PhaseOutcome.Ok)
            return located;
        if (prod.Reader is null && prod.Problem is null)
            return PhaseOutcome.Ok;
        var state = ctx.Sql!;
        var check = await SqlProdComparer.CompareAsync(ctx.Original.Text, state.CurrentTarget, state.Target, prod, state.CompatLevel, ct);
        ctx.SqlProd = check;
        switch (check.Outcome)
        {
            case ProdCheckOutcome.ChangedSincePull:
                ctx.Messages.Add(check.Message);
                ctx.FailureKind = "SqlProdChanged";
                return PhaseOutcome.EnvError;
            case ProdCheckOutcome.DiffersNoRecord:
            case ProdCheckOutcome.Unavailable:
            case ProdCheckOutcome.NotInProd:
                ctx.Messages.Add(check.Message);
                break;
        }
        return PhaseOutcome.Ok;
    }
}
