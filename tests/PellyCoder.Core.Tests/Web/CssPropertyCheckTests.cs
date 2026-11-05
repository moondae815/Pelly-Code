using PellyCoder.Core.Models;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class CssPropertyCheckTests
{
    private static CssSheet S(string s) => CssSheet.Parse(s);
    private static IReadOnlyList<string> U(string o, string p) => CssPropertyCheck.NewUnknown(S(o), S(p), WebBuiltins.Default);

    [Fact]
    public void Unknown_Warned()
    {
        var w = Assert.Single(U("a { color: red; }\n", "a { color: red; colr: blue; }\n"));
        Assert.Equal("알 수 없는 CSS 속성: colr(줄 1)", w);
    }

    [Fact]
    public void FontFaceDescriptorsAndOldGridGaps_Known() =>
        Assert.Empty(U("a { color: red; }\n",
            "@font-face {\n  font-family: X;\n  src: url(x.woff);\n  font-display: swap;\n  unicode-range: U+0-FF;\n}\n"
            + "a { color: red; grid-gap: 1px; grid-row-gap: 1px; grid-column-gap: 1px; }\n"));

    [Fact]
    public void IeHackPrefix_AllowedWhenOriginalUses() =>
        Assert.Empty(U("a { *zoom: 1; }\n", "a { *zoom: 1; _height: 1%; }\n"));

    [Fact]
    public void IeHackPrefix_WarnedWhenOriginalDoesNot() =>
        Assert.Single(U("a { color: red; }\n", "a { color: red; *zoom: 1; }\n"));

    [Fact]
    public void VendorPrefix_AllowedWhenOriginalUses() =>
        Assert.Empty(U("a { -webkit-foo: 1; }\n", "a { -webkit-foo: 1; -moz-bar: 2; }\n"));

    [Fact]
    public void Override_SameContainerLaterRule_Notice()
    {
        var sheet = S("a, b { color: red; }\nb { COLOR: blue; }\n");
        var n = Assert.Single(CssPropertyCheck.OverrideNotices(sheet, new TextSpanInfo(1, 1)));
        Assert.Equal("줄 1의 `color`은 줄 2의 `b` 규칙이 덮어씁니다", n);
    }

    [Fact]
    public void Override_OtherMedia_NoNotice()
    {
        var sheet = S("a { color: red; }\n@media print {\n a { color: blue; }\n}\n");
        Assert.Empty(CssPropertyCheck.OverrideNotices(sheet, new TextSpanInfo(1, 1)));
    }
}
