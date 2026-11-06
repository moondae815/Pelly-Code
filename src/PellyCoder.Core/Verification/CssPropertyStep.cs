using PellyCoder.Core.Asp;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Verification;

/// <summary>
/// Warns about unknown CSS property names the edit introduced (<c>Properties</c>) and about declarations in the target that a
/// later rule overrides (<c>Override</c>, recorded only when there is one). Warnings only: the step always passes.
/// </summary>
public sealed class CssPropertyStep : IVerifierStep
{
    public const string OverrideCheck = "Override";

    public string Name => "Properties";

    public Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Web ?? throw new InvalidOperationException("CSS verification needs the web run state.");
        var original = state.OriginalCss ?? CssSheet.Parse(state.OriginalText);
        if (state.ViewOf(ctx.Current.Text) is not { } view)
        {
            state.Record(Name, AspCheckStatus.Skipped);
            return Task.FromResult(StepResult.Pass);
        }
        var patched = CssSheet.Parse(view);

        var unknown = CssPropertyCheck.NewUnknown(original, patched, WebBuiltins.Default);
        state.PropertyWarnings = unknown.Count;
        if (unknown.Count == 0)
            state.Record(Name, AspCheckStatus.Passed);
        else
            state.Record(Name, AspCheckStatus.Warned, string.Join(", ", unknown));

        var overrides = CssPropertyCheck.OverrideNotices(patched, state.CurrentSpan);
        if (overrides.Count > 0)
            state.Record(OverrideCheck, AspCheckStatus.Warned, string.Join(", ", overrides));
        else
            state.Checks.Remove(OverrideCheck); // an earlier attempt's notice no longer applies
        return Task.FromResult(StepResult.Pass);
    }
}
