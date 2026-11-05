using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class CssSafetyGuardTests
{
    private static IReadOnlyList<WebSafetyFinding> New(string original, string patched) =>
        CssSafetyGuard.NewFindings(CssSheet.Parse(original), CssSheet.Parse(patched));

    [Fact]
    public void Expression_New_Rejected()
    {
        var f = Assert.Single(New(".a { color: red; }\n", ".a {\n  width: Expression(document.body.clientWidth > 1 ? 1 : 2);\n}\n"));
        Assert.Equal("CssExpression", f.Rule);
        Assert.Equal(2, f.Line);
    }

    [Fact]
    public void Behavior_Rejected() => Assert.Equal("CssBehavior", Assert.Single(New("", ".a { behavior: url(x.htc); }\n")).Rule);

    [Fact]
    public void MozBinding_Rejected() => Assert.Equal("CssMozBinding", Assert.Single(New("", ".a { -moz-binding: url(x.xml#y); }\n")).Rule);

    [Fact]
    public void JavascriptUrl_Rejected() =>
        Assert.Equal("CssJavascriptUrl", Assert.Single(New("", ".a { background: URL( 'JavaScript:alert(1)' ); }\n")).Rule);

    [Fact]
    public void ExistingExpression_NotCounted()
    {
        const string original = ".a { width: expression(1); }\n";
        Assert.Empty(New(original, ".b { color: red; }\n" + original));
    }
}
