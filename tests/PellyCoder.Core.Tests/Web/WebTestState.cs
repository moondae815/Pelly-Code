using Acornima;
using PellyCoder.Core.Models;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

/// <summary>Builds <see cref="WebRunState"/> values for the check tests.</summary>
internal static class WebTestState
{
    private static readonly Lazy<WebSiteIndexResult> NoIndex = new(() => throw new InvalidOperationException("site index not used"));

    public static WebRunState Js(string original, string? function = null, EcmaVersion? target = null, bool module = false, WebTargetUnit? unit = null, int occurrence = 0,
        bool allowSignature = false)
    {
        var level = JsLevels.Detect(original);
        var fallback = level.Level is null;
        var doc = fallback ? null : JsDocument.Parse(original, level.Level!.Value, level.Module);
        var fn = function is null ? null : (doc?.Functions ?? JsTokenScan.FunctionDeclarations(original)).Where(f => f.QualifiedName == function).ElementAt(occurrence);
        return new WebRunState(EditKind.Js, unit ?? (fn is null ? WebTargetUnit.Lines : WebTargetUnit.Function), new TextSpanInfo(1, 1), original, "UTF-8", false, "/site", NoIndex)
        {
            Function = fn,
            OriginalJs = doc,
            Fallback = fallback,
            Module = level.Module || module,
            OriginalLevel = level.Level,
            TargetLevel = fallback ? null : (target ?? level.Level),
            AllowSignatureChange = allowSignature,
        };
    }

    public static WebRunState Css(string original, string? selector = null, int occurrence = 0, bool atRule = false)
    {
        var sheet = CssSheet.Parse(original);
        var rule = selector is null ? null : sheet.Rules.Where(r => r.Selector == selector && r.IsAtRule == atRule).ElementAt(occurrence);
        return new WebRunState(EditKind.Css, rule is null ? WebTargetUnit.Lines : WebTargetUnit.Rule, new TextSpanInfo(1, 1), original, "UTF-8", false, "/site", NoIndex)
        {
            Rule = rule,
            OriginalCss = sheet,
        };
    }
}
