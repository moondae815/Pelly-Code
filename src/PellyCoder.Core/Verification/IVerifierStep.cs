using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Verification;

/// <summary>One VERIFY check (syntax, build, …). VERIFY runs the steps in order and stops at the first failure.</summary>
public interface IVerifierStep
{
    /// <summary>Short English name; the failure label is <c>{Name}Errors</c>.</summary>
    string Name { get; }

    Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct);
}

/// <summary>
/// Result of one step. <see cref="EnvironmentFailure"/> means the check itself could not run (timeout, tool missing):
/// VERIFY then ends the run with an environment error and <see cref="Message"/> instead of asking the model again.
/// </summary>
public sealed record StepResult(
    bool Passed,
    Feedback? Feedback,
    IReadOnlyList<Diagnostic> NewErrors,
    IReadOnlyList<Diagnostic> NewWarnings,
    bool EnvironmentFailure,
    string? Message)
{
    public static readonly StepResult Pass = new(true, null, [], [], false, null);

    public static StepResult Fail(Feedback feedback, IReadOnlyList<Diagnostic> newErrors, IReadOnlyList<Diagnostic> newWarnings) =>
        new(false, feedback, newErrors, newWarnings, false, null);

    public static StepResult Environment(string message) => new(false, null, [], [], true, message);
}
