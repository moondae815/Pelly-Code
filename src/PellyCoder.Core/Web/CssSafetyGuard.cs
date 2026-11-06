using System.Text.RegularExpressions;

namespace PellyCoder.Core.Web;

/// <summary>Safety rules for CSS: <c>expression()</c>, <c>behavior</c>, <c>-moz-binding</c> and <c>javascript:</c> URLs.</summary>
public static class CssSafetyGuard
{
    public const string Expression = "CssExpression";
    public const string Behavior = "CssBehavior";
    public const string MozBinding = "CssMozBinding";
    public const string JavascriptUrl = "CssJavascriptUrl";

    private static readonly Regex ExpressionCall = new(@"expression\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex JsUrl = new(@"url\s*\(\s*[""']?\s*javascript\s*:", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static IReadOnlyList<WebSafetyFinding> NewFindings(CssSheet original, CssSheet patched) =>
        JsSafetyGuard.GrownFindings(Collect(original), Collect(patched));

    public static IReadOnlyList<WebSafetyFinding> NewFindings(IReadOnlyList<CssSheet> originals, IReadOnlyList<CssSheet> patched) =>
        JsSafetyGuard.GrownFindings(originals.SelectMany(Collect).ToList(), patched.SelectMany(Collect).ToList());

    private static List<WebSafetyFinding> Collect(CssSheet sheet)
    {
        var result = new List<WebSafetyFinding>();
        foreach (var d in sheet.Rules.SelectMany(r => r.Declarations))
        {
            var text = Regex.Replace($"{d.Property}: {d.Value}", @"\s+", " ").Trim().ToLowerInvariant();
            var prop = d.Property.TrimStart('*', '_').Trim().ToLowerInvariant();
            if (ExpressionCall.IsMatch(d.Value)) result.Add(new WebSafetyFinding(Expression, d.Line, text));
            if (prop == "behavior") result.Add(new WebSafetyFinding(Behavior, d.Line, text));
            if (prop == "-moz-binding") result.Add(new WebSafetyFinding(MozBinding, d.Line, text));
            if (JsUrl.IsMatch(d.Value)) result.Add(new WebSafetyFinding(JavascriptUrl, d.Line, text));
        }
        return result;
    }
}
