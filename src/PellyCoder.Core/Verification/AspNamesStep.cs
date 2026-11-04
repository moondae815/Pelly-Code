using PellyCoder.Core.Asp;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;

namespace PellyCoder.Core.Verification;

/// <summary>
/// Fails on names the edit introduced that no declaration, procedure, include or built-in explains (design §4.5.5).
/// When the check cannot be sure (missing includes, dynamic code) the names become a warning instead.
/// </summary>
public sealed class AspNamesStep(FeedbackBuilder feedback) : IVerifierStep
{
    /// <summary>Diagnostic code of one unknown name.</summary>
    public const string UnknownNameCode = "AspUnknownName";

    public string Name => "AspNames";

    public Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Asp ?? throw new InvalidOperationException("ASP verification needs the ASP run state.");
        var current = AspPage.Parse(ctx.Current.Text);
        var result = AspNameCheck.NewUnknown(state.OriginalPage, current, state.Includes, AspBuiltins.Default);
        var names = string.Join(", ", result.Unknown.Select(u => u.Name).Distinct(StringComparer.OrdinalIgnoreCase));

        if (result.Downgraded)
        {
            state.Record(Name, AspCheckStatus.Warned,
                result.Unknown.Count == 0 ? result.DowngradeReason : $"{result.DowngradeReason} {names}");
            return Task.FromResult(StepResult.Pass);
        }
        if (result.Unknown.Count == 0)
        {
            state.Record(Name, AspCheckStatus.Passed);
            return Task.FromResult(StepResult.Pass);
        }

        var file = Path.GetFileName(ctx.FilePath);
        var errors = result.Unknown.Select(u => new Diagnostic(file, u.Line, 0, "error", UnknownNameCode, u.Name)).ToList();
        state.Record(Name, AspCheckStatus.Failed, names);
        return Task.FromResult(StepResult.Fail(feedback.ForAspNames(result.Unknown), errors, [], FailureKinds.AspUnknownNames));
    }
}
