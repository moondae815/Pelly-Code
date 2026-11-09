using System.IO.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Verification;

/// <summary>
/// The warning row <c>JsElements</c> of a <c>pelly asp</c> edit (element check design §4.4): JS lookups of ids and names the edit
/// broke, whether it changed the script or the HTML. Judged on the whole page whatever the edit range; no row when neither the
/// original nor the patched page has a script block. A warning only: the step always passes.
/// </summary>
public sealed class AspElementsStep(IFileSystem fs, PellyOptions options, ITextFileCodec codec) : IVerifierStep
{
    public const string Row = "JsElements";

    public string Name => "Elements";

    public Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Asp ?? throw new InvalidOperationException("ASP verification needs the ASP run state.");
        state.Checks.Remove(Row);

        var original = state.OriginalPage;
        var patched = AspPage.Parse(ctx.Current.Text);
        if (!HasScript(original) && !HasScript(patched)) return Task.FromResult(StepResult.Pass);

        var index = WebLocateCommon.SiteIndex(fs, ctx, options, state.SiteRoot, codec);
        var related = PageScripts.RelatedTexts(fs, state.SiteRoot, ctx.FilePath, original, codec, index.Value);
        var warnings = PageElements.NewBroken(original, ctx.Original.Text, patched, ctx.Current.Text, related);
        if (warnings.Count == 0) state.Record(Row, AspCheckStatus.Passed);
        else state.Record(Row, AspCheckStatus.Warned, string.Join(", ", warnings));
        return Task.FromResult(StepResult.Pass);
    }

    private static bool HasScript(AspPage page) => page.ClientBlocks.Any(b => b.Kind == AspClientBlockKind.Script);
}
