using System.IO.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Verification;

/// <summary>
/// The warning rows <c>JsNames</c> and <c>JsApi</c> of a <c>pelly asp</c> edit that touched a client script block (design 4.5, 4.6):
/// free names the edit introduced that nothing the page sees defines, and ES or jQuery APIs above the block's level. A warning
/// only: the step always passes. A block judged by pairing (server statements, original not parsing) has no syntax tree and is skipped.
/// </summary>
public sealed class AspClientWarningsStep(IFileSystem fs, PellyOptions options, ITextFileCodec codec) : IVerifierStep
{
    public const string NamesRow = "JsNames";
    public const string ApiRow = "JsApi";

    public string Name => "ClientWarnings";

    public Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        var state = ctx.Asp ?? throw new InvalidOperationException("ASP verification needs the ASP run state.");
        state.Checks.Remove(NamesRow);
        state.Checks.Remove(ApiRow);

        var text = ctx.Current.Text;
        var patched = AspPage.Parse(text);
        var pairs = AspClientChecks.Pairs(state.OriginalPage, patched, state.OriginalRegions())
            .Where(p => p.Kind == AspClientBlockKind.Script).ToList();
        if (pairs.Count == 0) return Task.FromResult(StepResult.Pass);

        var names = new List<string>();
        var apis = new List<string>();
        var judged = false;
        Lazy<WebSiteIndexResult>? index = null;
        IReadOnlyList<JqueryFound>? found = null;
        foreach (var pair in pairs)
        {
            var pv = PageBlockView.Build(patched, text, pair.PatchedIndex);
            var ov = pair.OriginalIndex is { } oi ? PageBlockView.Build(state.OriginalPage, ctx.Original.Text, oi) : null;
            var plan = AspClientChecks.Plan(state.OriginalPage, ctx.Original.Text, ov, pv, options.Web.JsTarget);
            if (plan.Level is not { } level) continue;
            var after = JsDocument.Parse(pv.Text, level, plan.Module);
            if (after.Error is not null) continue;
            var before = JsDocument.Parse(ov?.Text ?? "", plan.OriginalLevel ?? level, plan.Module);
            judged = true;

            index ??= WebLocateCommon.SiteIndex(fs, ctx, options, state.SiteRoot, codec);
            found ??= JqueryVersions.Find(fs, state.SiteRoot);
            var scripts = PageScripts.Collect(fs, state.SiteRoot, ctx.FilePath, patched, pair.PatchedIndex, codec, index.Value, found,
                options.Web.JqueryVersion, text);
            var known = JsNameCheck.KnownSet(WebBuiltins.Default, scripts.Jquery is not null || found.Count > 0,
                JsNameCheck.SiteNames(index.Value, scripts.VisibleNames), options.Web.Globals);
            names.AddRange(JsNameCheck.NewUnknown(before, after, known)
                .Where(u => !PageBlockView.IsPlaceholder(u.Name))
                .GroupBy(u => u.Name, StringComparer.Ordinal)
                .Select(g => (Name: g.Key, Line: g.Min(u => u.Line)))
                .OrderBy(u => u.Line).ThenBy(u => u.Name, StringComparer.Ordinal)
                .Select(u => $"{u.Name}(줄 {u.Line})"));
            apis.AddRange(JsApiCheck.NewWarnings(before, after, level, scripts.Jquery, WebBuiltins.Default));
        }

        Record(state, NamesRow, judged, names);
        Record(state, ApiRow, judged, apis);
        return Task.FromResult(StepResult.Pass);
    }

    private static void Record(AspRunState state, string row, bool judged, List<string> warnings)
    {
        if (!judged) state.Record(row, AspCheckStatus.Skipped, "서버 코드가 섞였거나 원본이 해석되지 않음");
        else if (warnings.Count == 0) state.Record(row, AspCheckStatus.Passed);
        else state.Record(row, AspCheckStatus.Warned, string.Join(", ", warnings));
    }
}
