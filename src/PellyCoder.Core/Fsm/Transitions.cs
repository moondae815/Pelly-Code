namespace PellyCoder.Core.Fsm;

public static class Transitions
{
    /// <param name="attempt">Number of EDIT entries so far (value after the Edit phase incremented it).</param>
    public static Transition Next(RunPhase current, PhaseOutcome outcome, int attempt, int maxRetries)
    {
        bool canRetry = attempt < 1 + maxRetries;
        Transition Retry(int exitWhenExhausted) =>
            canRetry ? new(RunPhase.Slice, null) : Fail(exitWhenExhausted);

        if (IsTerminal(current))
            throw new InvalidOperationException($"No transition out of terminal phase {current}.");

        return (current, outcome) switch
        {
            (_, PhaseOutcome.Cancelled) => Fail(ExitCodes.UserCancelled),
            (_, PhaseOutcome.Crashed) => Fail(ExitCodes.EnvError),
            (_, PhaseOutcome.EnvError) => Fail(ExitCodes.EnvError),

            (RunPhase.Preflight, PhaseOutcome.Ok) => new(RunPhase.Locate, null),
            (RunPhase.Preflight or RunPhase.Locate, PhaseOutcome.SafetyRejected) => Fail(ExitCodes.SafetyRejected),
            (RunPhase.Locate, PhaseOutcome.Ok) => new(RunPhase.Slice, null),
            (RunPhase.Locate, PhaseOutcome.NotFound or PhaseOutcome.Ambiguous) => Fail(ExitCodes.LocateFailed),
            (RunPhase.Slice, PhaseOutcome.Ok) => new(RunPhase.Edit, null),
            // A retry re-finds the member; a patch that renamed or broke it leaves no target.
            (RunPhase.Slice, PhaseOutcome.NotFound) => Fail(ExitCodes.LocateFailed),
            (RunPhase.Slice or RunPhase.Edit, PhaseOutcome.BudgetExceeded) => Fail(ExitCodes.EnvError),
            (RunPhase.Edit, PhaseOutcome.Ok) => new(RunPhase.Apply, null),
            (RunPhase.Edit, PhaseOutcome.FormatError) => Retry(ExitCodes.VerifyFailed),
            (RunPhase.Apply, PhaseOutcome.Ok) => new(RunPhase.Verify, null),
            (RunPhase.Apply, PhaseOutcome.MatchFailed or PhaseOutcome.ShapeRejected) => Retry(ExitCodes.VerifyFailed),
            (RunPhase.Apply, PhaseOutcome.SafetyRejected) => Retry(ExitCodes.SafetyRejected),
            // The shape guard cannot find the target in the original text: nothing safe to compare against.
            (RunPhase.Apply, PhaseOutcome.NotFound) => Fail(ExitCodes.LocateFailed),
            (RunPhase.Apply, PhaseOutcome.FileChangedExternally) => Fail(ExitCodes.EnvError),
            (RunPhase.Apply, PhaseOutcome.DryRunCompleted) => new(RunPhase.Done, ExitCodes.Success),
            (RunPhase.Verify, PhaseOutcome.Ok) => new(RunPhase.Review, null),
            (RunPhase.Verify, PhaseOutcome.NewErrors) => Retry(ExitCodes.VerifyFailed),
            (RunPhase.Review, PhaseOutcome.Approved) => new(RunPhase.Done, ExitCodes.Success),
            (RunPhase.Review, PhaseOutcome.Rejected) => Fail(ExitCodes.UserCancelled),

            _ => throw new InvalidOperationException($"Undefined transition: {current} + {outcome}."),
        };
    }

    public static bool IsTerminal(RunPhase p) => p is RunPhase.Done or RunPhase.Failed;

    private static Transition Fail(int exitCode) => new(RunPhase.Failed, exitCode);
}
