using PellyCoder.Core.Fsm;

namespace PellyCoder.Core.Tests.Fsm;

public class TransitionsTests
{
    [Theory]
    [InlineData(RunPhase.Preflight, PhaseOutcome.Ok, 0, RunPhase.Locate, null)]
    [InlineData(RunPhase.Preflight, PhaseOutcome.EnvError, 0, RunPhase.Failed, 4)]
    [InlineData(RunPhase.Locate, PhaseOutcome.Ok, 0, RunPhase.Slice, null)]
    [InlineData(RunPhase.Locate, PhaseOutcome.NotFound, 0, RunPhase.Failed, 3)]
    [InlineData(RunPhase.Locate, PhaseOutcome.Ambiguous, 0, RunPhase.Failed, 3)]
    [InlineData(RunPhase.Slice, PhaseOutcome.Ok, 0, RunPhase.Edit, null)]
    [InlineData(RunPhase.Slice, PhaseOutcome.BudgetExceeded, 0, RunPhase.Failed, 4)]
    [InlineData(RunPhase.Slice, PhaseOutcome.NotFound, 2, RunPhase.Failed, 3)] // member no longer found on a retry
    [InlineData(RunPhase.Edit, PhaseOutcome.Ok, 1, RunPhase.Apply, null)]
    [InlineData(RunPhase.Edit, PhaseOutcome.BudgetExceeded, 1, RunPhase.Failed, 4)]
    [InlineData(RunPhase.Edit, PhaseOutcome.EnvError, 1, RunPhase.Failed, 4)] // LLM server dropped
    [InlineData(RunPhase.Edit, PhaseOutcome.FormatError, 1, RunPhase.Slice, null)]
    [InlineData(RunPhase.Edit, PhaseOutcome.FormatError, 4, RunPhase.Failed, 1)]
    [InlineData(RunPhase.Apply, PhaseOutcome.Ok, 1, RunPhase.Verify, null)]
    [InlineData(RunPhase.Apply, PhaseOutcome.MatchFailed, 3, RunPhase.Slice, null)]
    [InlineData(RunPhase.Apply, PhaseOutcome.MatchFailed, 4, RunPhase.Failed, 1)]
    [InlineData(RunPhase.Apply, PhaseOutcome.SafetyRejected, 4, RunPhase.Failed, 5)]
    [InlineData(RunPhase.Apply, PhaseOutcome.ShapeRejected, 4, RunPhase.Failed, 1)]
    [InlineData(RunPhase.Apply, PhaseOutcome.NotFound, 1, RunPhase.Failed, 3)] // shape guard: target not in the original
    [InlineData(RunPhase.Apply, PhaseOutcome.FileChangedExternally, 1, RunPhase.Failed, 4)]
    [InlineData(RunPhase.Apply, PhaseOutcome.EnvError, 1, RunPhase.Failed, 4)]
    [InlineData(RunPhase.Apply, PhaseOutcome.DryRunCompleted, 1, RunPhase.Done, 0)]
    [InlineData(RunPhase.Verify, PhaseOutcome.Ok, 1, RunPhase.Review, null)]
    [InlineData(RunPhase.Verify, PhaseOutcome.NewErrors, 2, RunPhase.Slice, null)]
    [InlineData(RunPhase.Verify, PhaseOutcome.NewErrors, 4, RunPhase.Failed, 1)]
    [InlineData(RunPhase.Review, PhaseOutcome.Approved, 1, RunPhase.Done, 0)]
    [InlineData(RunPhase.Review, PhaseOutcome.Rejected, 1, RunPhase.Failed, 2)]
    [InlineData(RunPhase.Edit, PhaseOutcome.Cancelled, 2, RunPhase.Failed, 2)]
    [InlineData(RunPhase.Verify, PhaseOutcome.Crashed, 2, RunPhase.Failed, 4)]
    public void Next_FollowsDesignTable(RunPhase cur, PhaseOutcome o, int attempt, RunPhase next, int? exit)
        => Assert.Equal(new Transition(next, exit), Transitions.Next(cur, o, attempt, maxRetries: 3));

    [Fact]
    public void Next_UndefinedPair_Throws()
        => Assert.Throws<InvalidOperationException>(() => Transitions.Next(RunPhase.Locate, PhaseOutcome.Approved, 0, 3));

    [Fact]
    public void Next_MaxRetriesZero_FailsOnFirstRetryableOutcome()
        => Assert.Equal(new Transition(RunPhase.Failed, 1), Transitions.Next(RunPhase.Apply, PhaseOutcome.MatchFailed, 1, 0));
}
