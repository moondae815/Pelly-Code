namespace PellyCoder.Core.Fsm;

/// <summary>One FSM phase. Phases never call each other; they exchange data only through <see cref="RunContext"/>.</summary>
public interface IPhase
{
    RunPhase Phase { get; }

    Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct);
}
