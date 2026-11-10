using System.IO.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Verification;

/// <summary>
/// The warning row <c>FormFields</c> of a <c>pelly asp</c> edit (W3 design §5.3): form field pairs the edit broke, as the page
/// that reads them or as the form or ajax that sends them. No row when neither the original nor the patched file has form facts.
/// A warning only: the step always passes.
/// </summary>
public sealed class AspFormFieldsStep(IFileSystem fs, PellyOptions options, ITextFileCodec codec) : IVerifierStep
{
    public const string Row = "FormFields";

    public string Name => Row;

    public Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Asp ?? throw new InvalidOperationException("ASP verification needs the ASP run state.");
        state.Checks.Remove(Row);
        state.FormFieldWarnings = 0;

        var rel = FormFieldsCheck.Relative(state.SiteRoot, ctx.FilePath);
        var original = FormFieldsCheck.Facts(rel, ctx.Original.Text);
        var current = FormFieldsCheck.Facts(rel, ctx.Current.Text);
        if (original.IsEmpty && current.IsEmpty) return Task.FromResult(StepResult.Pass);

        state.SiteIndex ??= WebLocateCommon.SiteIndex(fs, ctx, options, state.SiteRoot, codec);
        WebSiteIndexResult index;
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

/// <summary>The form field check shared by <c>pelly asp</c> and <c>pelly js</c>.</summary>
internal static class FormFieldsCheck
{
    public static string Relative(string siteRoot, string path) =>
        WebSiteIndex.Relative(siteRoot.Replace('\\', '/').TrimEnd('/'), path);

    public static FileFormFacts Facts(string rel, string textLf) =>
        rel.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? FormFacts.OfScript(textLf) : FormFacts.OfPage(AspPage.Parse(textLf), textLf);

    /// <summary>
    /// The mismatches the edit made. Both worlds take the file's facts from the texts, not from the index: the index may have
    /// read the file before or after the write.
    /// </summary>
    public static IReadOnlyList<string> Warnings(WebSiteIndexResult index, string rel, FileFormFacts original, FileFormFacts current)
    {
        var before = FormSite.Build(index, new Dictionary<string, FileFormFacts> { [rel] = original });
        var after = FormSite.Build(index, new Dictionary<string, FileFormFacts> { [rel] = current });
        return FormSite.NewMismatches(before, after, [rel]).Select(m => m.Message).ToList();
    }
}
