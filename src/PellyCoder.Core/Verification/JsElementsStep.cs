using System.IO.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Verification;

/// <summary>
/// Warns when a page edit adds a JS lookup of an id or name that the page, its includes, its includers and its
/// <c>&lt;script src&gt;</c> files do not define (element check design §4). A warning only: the step always passes. A plain
/// <c>.js</c> file is skipped: which page uses it is unknown.
/// </summary>
public sealed class JsElementsStep(IFileSystem fs, ITextFileCodec codec) : IVerifierStep
{
    public const string Row = "Elements";

    public string Name => Row;

    public Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Web ?? throw new InvalidOperationException("JS verification needs the web run state.");
        if (state.Page is null)
        {
            state.Record(Row, AspCheckStatus.Skipped, "페이지가 아님");
            return Task.FromResult(StepResult.Pass);
        }

        var original = state.Page.OriginalPage;
        WebSiteIndexResult index;
        try { index = state.SiteIndex.Value; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            state.Record(Row, AspCheckStatus.Skipped, AspElementsStep.IndexUnreadable);
            return Task.FromResult(StepResult.Pass);
        }
        var related = PageScripts.RelatedTexts(fs, state.SiteRoot, ctx.FilePath, original, codec, index);
        var warnings = PageElements.NewBroken(original, ctx.Original.Text, AspPage.Parse(ctx.Current.Text), ctx.Current.Text, related);
        if (warnings.Count == 0) state.Record(Row, AspCheckStatus.Passed);
        else state.Record(Row, AspCheckStatus.Warned, string.Join(", ", warnings));
        return Task.FromResult(StepResult.Pass);
    }
}
