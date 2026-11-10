using System.IO.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Verification;

/// <summary>
/// The warning row <c>JsSignature</c> of a <c>pelly asp</c> edit (W3 design §4.2): a page script function whose name or
/// parameters the edit changed, with how many calls the site index knows of the old name (rename) or the name. No row when the
/// edit range touches no script block of the original page. A warning only: the step always passes.
/// </summary>
public sealed class AspJsSignatureStep(IFileSystem fs, PellyOptions options, ITextFileCodec codec) : IVerifierStep
{
    public const string Row = "JsSignature";

    public string Name => "JsSignature";

    public Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Asp ?? throw new InvalidOperationException("ASP verification needs the ASP run state.");
        state.Checks.Remove(Row);

        var span = state.OriginalSpan;
        var touchesScript = state.OriginalPage.ClientBlocks.Any(b => b.Kind == AspClientBlockKind.Script
            && b.OpenLine <= span.EndLine && b.CloseLine >= span.StartLine);
        if (!touchesScript) return Task.FromResult(StepResult.Pass);

        var changes = JsSignatureChanges.InLines(ctx.Original.Text, ctx.Current.Text, isPage: true, span);
        if (changes.Count == 0)
        {
            state.Record(Row, AspCheckStatus.Passed);
            return Task.FromResult(StepResult.Pass);
        }

        state.SiteIndex ??= WebLocateCommon.SiteIndex(fs, ctx, options, state.SiteRoot, codec);
        WebSiteIndexResult index;
        try { index = state.SiteIndex.Value; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            state.Record(Row, AspCheckStatus.Skipped, AspElementsStep.IndexUnreadable);
            return Task.FromResult(StepResult.Pass);
        }
        var parts = changes.Select(c =>
        {
            var name = c.Renamed ? c.Before.Name : c.After.Name;
            return $"함수 서명 변경: {c.Display}, 부르는 곳 {index.JsCalls.Count(x => x.Call.Name == name)}곳";
        });
        state.Record(Row, AspCheckStatus.Warned, string.Join("; ", parts));
        return Task.FromResult(StepResult.Pass);
    }
}
