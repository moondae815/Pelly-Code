using PellyCoder.Core.Verification;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>Runs the verifier steps (syntax, build, then tests with <c>--test</c>) in order and stops at the first one that fails.</summary>
public sealed class VerifyPhase(IReadOnlyList<IVerifierStep> steps) : IPhase
{
    public RunPhase Phase => RunPhase.Verify;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        foreach (var step in steps)
        {
            var result = await step.VerifyAsync(ctx, ct);
            if (result.EnvironmentFailure)
            {
                if (result.Message is not null) ctx.Messages.Add(result.Message);
                if (result.FailureKind is not null) ctx.FailureKind = result.FailureKind;
                return PhaseOutcome.EnvError;
            }
            if (result.Passed) continue;

            foreach (var code in result.NewErrors.Select(e => e.Code))
            {
                if (!ctx.ErrorCodes.Contains(code)) ctx.ErrorCodes.Add(code);
            }
            ctx.Feedback = result.Feedback;
            ctx.FailureKind = result.FailureKind ?? $"{step.Name}Errors";
            return PhaseOutcome.NewErrors;
        }
        return PhaseOutcome.Ok;
    }
}
