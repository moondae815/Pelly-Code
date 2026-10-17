namespace PellyCoder.Core.Fsm;

public enum RunPhase { Preflight, Locate, Slice, Edit, Apply, Verify, Review, Done, Failed }

public enum PhaseOutcome
{
    Ok, EnvError, NotFound, Ambiguous, BudgetExceeded, FormatError, MatchFailed, SafetyRejected,
    ShapeRejected, FileChangedExternally, NewErrors, Approved, Rejected, DryRunCompleted, Cancelled, Crashed,
}

/// <summary>ExitCode is set only when Next is Done or Failed.</summary>
public sealed record Transition(RunPhase Next, int? ExitCode);

public static class ExitCodes
{
    public const int Success = 0;
    public const int VerifyFailed = 1;
    public const int UserCancelled = 2;
    public const int LocateFailed = 3;
    public const int EnvError = 4;
    public const int SafetyRejected = 5;
}
