using PellyCoder.Core.Asp;
using PellyCoder.Core.Models;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public sealed class WebTargetLookupTests
{
    private const string Js =
        "function fnSearch(f) { return 1; }\n" +
        "var Common = { fnX: function (a) { return a; } };\n" +
        "function twice() {}\n" +
        "function twice() {}\n";

    private const string Page =
        "<%@ Language=\"VBScript\" %>\n" + // 1
        "<style>\n" +                     // 2
        ".grid td { padding: 2px; }\n" +  // 3
        "</style>\n" +                    // 4
        "<p>x</p>\n" +                    // 5
        "<script>\n" +                    // 6
        "function goPage(n) {\n" +        // 7
        "  fnSearch(document.f, n);\n" +  // 8
        "}\n" +                           // 9
        "</script>\n";                    // 10

    [Fact]
    public void Function_InJsFile_FoundAmbiguousNotFound()
    {
        Assert.Equal(WebTargetFind.Found, WebTargetLookup.Function(Js, false, "fnSearch"));
        Assert.Equal(WebTargetFind.Found, WebTargetLookup.Function(Js, false, "Common.fnX"));
        Assert.Equal(WebTargetFind.Ambiguous, WebTargetLookup.Function(Js, false, "twice"));
        Assert.Equal(WebTargetFind.NotFound, WebTargetLookup.Function(Js, false, "nope"));
    }

    [Fact]
    public void Function_InPageBlock_Found()
    {
        Assert.Equal(WebTargetFind.Found, WebTargetLookup.Function(Page, true, "goPage"));
        Assert.Equal(WebTargetFind.NotFound, WebTargetLookup.Function(Page, true, "fnSearch"));
    }

    [Fact]
    public void Function_ServerStatementsBlock_UsesTokenScan()
    {
        const string page = "<script>\n<% If a Then %>\nfunction f() { return 1; }\n<% End If %>\n</script>\n";
        Assert.Equal(WebTargetFind.Found, WebTargetLookup.Function(page, true, "f"));
    }

    [Fact]
    public void Rule_InCssFile_And_PageStyle()
    {
        Assert.Equal(WebTargetFind.Found, WebTargetLookup.Rule(".a { color: red; }\n#main { x: 1; }\n", false, "#main"));
        Assert.Equal(WebTargetFind.NotFound, WebTargetLookup.Rule(".a { color: red; }\n", false, ".b"));
        Assert.Equal(WebTargetFind.Found, WebTargetLookup.Rule(Page, true, ".grid td"));
    }

    [Fact]
    public void LinesInOneBlock_TrueInside_FalseAcrossHtml()
    {
        var page = AspPage.Parse(Page);
        Assert.True(WebTargetLookup.LinesInOneBlock(page, new TextSpanInfo(7, 9), EditKind.Js));
        Assert.False(WebTargetLookup.LinesInOneBlock(page, new TextSpanInfo(5, 8), EditKind.Js));
        Assert.True(WebTargetLookup.LinesInOneBlock(page, new TextSpanInfo(3, 3), EditKind.Css));
        Assert.False(WebTargetLookup.LinesInOneBlock(page, new TextSpanInfo(3, 3), EditKind.Js));
    }
}
