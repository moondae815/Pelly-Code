using PellyCoder.Core.Asp;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Verification;

/// <summary>
/// Warns about ES APIs above the file's target level and jQuery methods outside the site's jQuery version that the edit
/// introduced. A warning only: the step always passes. Skipped in fallback mode (no syntax tree, no target level).
/// </summary>
public sealed class JsApiStep : IVerifierStep
{
    public string Name => "Api";

    public Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Web ?? throw new InvalidOperationException("JS verification needs the web run state.");
        if (state.Fallback || state.OriginalJs is null || state.TargetLevel is not { } target
            || JsVerify.Patched(ctx, state) is not { } patched)
        {
            state.ApiWarnings = 0;
            state.Record(Name, AspCheckStatus.Skipped, state.FallbackReason);
            return Task.FromResult(StepResult.Pass);
        }

        var warnings = JsApiCheck.NewWarnings(state.OriginalJs, patched, target, state.Jquery, WebBuiltins.Default);
        state.ApiWarnings = warnings.Count;
        if (warnings.Count == 0)
            state.Record(Name, AspCheckStatus.Passed);
        else
            state.Record(Name, AspCheckStatus.Warned, string.Join(", ", warnings));
        return Task.FromResult(StepResult.Pass);
    }
}
