using Acornima;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class JsApiCheckTests
{
    private static JsDocument D(string s) => JsDocument.Parse(s, EcmaVersion.Latest, false);
    private static IReadOnlyList<string> W(string o, string p, EcmaVersion t, string? jq) =>
        JsApiCheck.NewWarnings(D(o), D(p), t, jq is null ? null : Version.Parse(jq), WebBuiltins.Default);

    [Fact]
    public void Includes_Es5_Warned()
    {
        var w = Assert.Single(W("", "var a = s.includes(\"x\");\n", EcmaVersion.ES5, null));
        Assert.Contains("`.includes`는 ES2016 기능입니다(이 파일 기준 ES5)", w);
        Assert.Contains("(줄 1)", w);
    }

    [Fact]
    public void Includes_Es2016_NotWarned() => Assert.Empty(W("", "s.includes(\"x\");\n", EcmaVersion.ES2016, null));

    [Fact]
    public void On_Jquery142_Warned()
    {
        var w = Assert.Single(W("", "$(\"#a\").on(\"click\", f);\n", EcmaVersion.ES5, "1.4.2"));
        Assert.Contains("`.on`은 jQuery 1.7부터 있습니다(사이트 기준 1.4.2)", w);
    }

    [Fact]
    public void On_Jquery183_NotWarned() => Assert.Empty(W("", "$(\"#a\").on(\"click\", f);\n", EcmaVersion.ES5, "1.8.3"));

    [Fact]
    public void Live_Jquery1102_RemovedWarned()
    {
        var w = Assert.Single(W("", "$(\"#a\").live(\"click\", f);\n", EcmaVersion.ES5, "1.10.2"));
        Assert.Contains("`.live`은 jQuery 1.9에서 없어졌습니다(사이트 기준 1.10.2)", w);
    }

    [Fact]
    public void NoJquery_NoJqueryWarnings() => Assert.Empty(W("", "$(\"#a\").on(\"click\", f);\n", EcmaVersion.ES5, null));

    [Fact]
    public void Existing_NotCounted() =>
        Assert.Empty(W("s.includes(\"x\");\n", "s.includes(\"x\");\nvar z = 1;\n", EcmaVersion.ES5, null));

    [Fact]
    public void PromiseIdentifier_Es5_Warned()
    {
        var w = W("", "var p = new Promise(f);\n", EcmaVersion.ES5, null);
        var one = Assert.Single(w);
        Assert.Contains("`Promise`는 ES2015 기능입니다(이 파일 기준 ES5)", one);
    }

    private static IReadOnlyList<string> WV(string p, Version v) =>
        JsApiCheck.NewWarnings(D(""), D(p), EcmaVersion.ES5, v, WebBuiltins.Default);

    [Fact]
    public void On_SiteJquery17TwoPart_NotWarned() => Assert.Empty(WV("$(\"#a\").on(\"click\", f);\n", new Version(1, 7)));

    [Fact]
    public void On_SiteJquery170ThreePart_NotWarned() => Assert.Empty(WV("$(\"#a\").on(\"click\", f);\n", new Version(1, 7, 0)));

    [Fact]
    public void Removed_SiteJquery19TwoPart_Warned() =>
        Assert.Single(WV("$(\"#a\").live(\"click\", f);\n", new Version(1, 9)));
}
