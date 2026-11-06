using Acornima;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Verification;

/// <summary>
/// Warns about free names the edit introduced that no built-in, jQuery, site file or <c>web.globals</c> entry defines. A warning
/// only: the step always passes. Skipped in fallback mode (the original does not parse, so there is no syntax tree to compare).
/// </summary>
public sealed class JsNamesStep(IReadOnlyList<string> globals) : IVerifierStep
{
    public string Name => "Names";

    public Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Web ?? throw new InvalidOperationException("JS verification needs the web run state.");
        if (state.Fallback || state.OriginalJs is null || JsVerify.Patched(ctx, state) is not { } patched)
        {
            state.NameWarnings = 0;
            state.Record(Name, AspCheckStatus.Skipped, state.FallbackReason);
            return Task.FromResult(StepResult.Pass);
        }

        var jquery = state.Jquery is not null || state.JqueryFound.Count > 0;
        var known = JsNameCheck.KnownSet(WebBuiltins.Default, jquery, state.SiteIndex.Value.Names, globals);
        // One entry per name, at its first new use.
        var unknown = JsNameCheck.NewUnknown(state.OriginalJs, patched, known)
            .Where(u => !PageBlockView.IsPlaceholder(u.Name))
            .GroupBy(u => u.Name, StringComparer.Ordinal)
            .Select(g => (Name: g.Key, Line: g.Min(u => u.Line)))
            .OrderBy(u => u.Line).ThenBy(u => u.Name, StringComparer.Ordinal)
            .ToList();
        state.NameWarnings = unknown.Count;
        if (unknown.Count == 0)
            state.Record(Name, AspCheckStatus.Passed);
        else
            state.Record(Name, AspCheckStatus.Warned, string.Join(", ", unknown.Select(u => $"{u.Name}(줄 {u.Line})")));
        return Task.FromResult(StepResult.Pass);
    }
}

internal static class JsVerify
{
    /// <summary>
    /// The current (patched) file parsed at the target level; null when it does not parse there. APPLY already accepted it, so
    /// null only happens when a later step rewrote the text.
    /// </summary>
    public static JsDocument? Patched(RunContext ctx, WebRunState state)
    {
        var doc = state.ViewOf(ctx.Current.Text) is not { } view ? null : JsDocument.Parse(view, state.TargetLevel ?? EcmaVersion.Latest, state.Module);
        return doc?.Error is null ? doc : null;
    }
}
