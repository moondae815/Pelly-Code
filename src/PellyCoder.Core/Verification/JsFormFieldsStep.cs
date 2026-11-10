using PellyCoder.Core.Asp;
using PellyCoder.Core.Fsm;

namespace PellyCoder.Core.Verification;

/// <summary>
/// The warning row <c>Fields</c> of a <c>pelly js</c> edit (W3 design §5.3): form field pairs the edit broke. A page edit can change
/// a form's action assignment or ajax; a <c>.js</c> edit its ajax and assignments. No row when neither text has form facts.
/// A warning only: the step always passes.
/// </summary>
public sealed class JsFormFieldsStep : IVerifierStep
{
    public const string Row = "Fields";

    public string Name => Row;

    public Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Web ?? throw new InvalidOperationException("JS verification needs the web run state.");
        state.Checks.Remove(Row);
        state.FormFieldWarnings = 0;

        var rel = FormFieldsCheck.Relative(state.SiteRoot, ctx.FilePath);
        var original = FormFieldsCheck.Facts(rel, ctx.Original.Text);
        var current = FormFieldsCheck.Facts(rel, ctx.Current.Text);
        if (original.IsEmpty && current.IsEmpty) return Task.FromResult(StepResult.Pass);

        Web.WebSiteIndexResult index;
        try { index = state.SiteIndex.Value; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            state.Record(Row, AspCheckStatus.Skipped, AspElementsStep.IndexUnreadable);
            return Task.FromResult(StepResult.Pass);
        }
        var warnings = FormFieldsCheck.Warnings(index, rel, original, current);
        state.FormFieldWarnings = warnings.Count;
        if (warnings.Count == 0) state.Record(Row, AspCheckStatus.Passed);
        else state.Record(Row, AspCheckStatus.Warned, string.Join("; ", warnings));
        return Task.FromResult(StepResult.Pass);
    }
}
