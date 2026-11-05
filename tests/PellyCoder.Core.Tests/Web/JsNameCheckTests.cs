using Acornima;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class JsNameCheckTests
{
    private static JsDocument D(string s) => JsDocument.Parse(s, EcmaVersion.Latest, false);
    private static readonly IReadOnlySet<string> NoSite = new HashSet<string>();
    private static Func<string, bool> Known(bool jq = false, IReadOnlySet<string>? site = null, params string[] globals) =>
        JsNameCheck.KnownSet(WebBuiltins.Default, jq, site ?? NoSite, globals);

    [Fact]
    public void Invented_Warned()
    {
        var r = JsNameCheck.NewUnknown(D("var a = 1;\n"), D("var a = 1;\nfnInvented(a);\n"), Known());
        var u = Assert.Single(r);
        Assert.Equal("fnInvented", u.Name);
        Assert.Equal(2, u.Line);
    }

    [Fact]
    public void SiteIndexName_Known() =>
        Assert.Empty(JsNameCheck.NewUnknown(D(""), D("fnSite();\n"), Known(site: new HashSet<string> { "fnSite" })));

    [Fact]
    public void JqueryDollar_KnownOnlyWithJquery()
    {
        var p = D("$(x);\n");
        Assert.Contains(JsNameCheck.NewUnknown(D(""), p, Known(false)), u => u.Name == "$");
        Assert.DoesNotContain(JsNameCheck.NewUnknown(D(""), p, Known(true)), u => u.Name == "$");
    }

    [Fact]
    public void Globals_Known() =>
        Assert.Empty(JsNameCheck.NewUnknown(D(""), D("fnG();\n"), Known(false, null, "fnG")));

    [Fact]
    public void ExistingUnknown_NotCounted() =>
        Assert.Empty(JsNameCheck.NewUnknown(D("fnOld();\n"), D("fnOld();\nvar z = 1;\n"), Known()));

    [Fact]
    public void LocalVariable_NotFree() =>
        Assert.Empty(JsNameCheck.NewUnknown(D(""), D("function f() { var loc = 1; return loc; }\n"), Known()));
}
