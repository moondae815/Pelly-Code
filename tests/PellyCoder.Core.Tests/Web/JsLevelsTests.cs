using Acornima;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class JsLevelsTests
{
    [Fact]
    public void Detect_PureEs5_Es5()
    {
        var r = JsLevels.Detect("var a = 1;\nfunction f(x) { return x + a; }\n");
        Assert.Equal(EcmaVersion.ES5, r.Level);
        Assert.False(r.Module);
        Assert.Null(r.LatestError);
    }

    [Fact]
    public void Detect_OneLet_Es2015()
    {
        var r = JsLevels.Detect("var a = 1;\nlet b = 2;\n");
        Assert.Equal(EcmaVersion.ES2015, r.Level);
    }

    [Fact]
    public void Detect_Exponent_Es2016()
    {
        var r = JsLevels.Detect("var a = 2 ** 3;\n");
        Assert.Equal(EcmaVersion.ES2016, r.Level);
    }

    [Fact]
    public void Detect_ImportExport_Module()
    {
        var r = JsLevels.Detect("import a from 'x';\nexport const b = a;\n");
        Assert.True(r.Module);
        Assert.Equal(EcmaVersion.ES2015, r.Level);
    }

    [Fact]
    public void Detect_Broken_NullWithError()
    {
        var r = JsLevels.Detect("var a = ;\n");
        Assert.Null(r.Level);
        Assert.NotNull(r.LatestError);
        Assert.Equal(1, r.LatestError!.Line);
    }

    [Fact]
    public void FromSetting_AutoNullAndValues()
    {
        Assert.Null(JsLevels.FromSetting("auto"));
        Assert.Equal(EcmaVersion.ES5, JsLevels.FromSetting("es5"));
        Assert.Equal(EcmaVersion.ES2015, JsLevels.FromSetting("ES2015"));
        Assert.Equal(EcmaVersion.ES2026, JsLevels.FromSetting("es2026"));
        Assert.Equal(EcmaVersion.Latest, JsLevels.FromSetting("latest"));
    }

    [Fact]
    public void Compare_AliasesEqual_LatestHighest()
    {
        Assert.Equal(0, JsLevels.Compare(EcmaVersion.ES6, EcmaVersion.ES2015));
        Assert.True(JsLevels.Compare(EcmaVersion.Latest, EcmaVersion.ES2026) >= 0);
        Assert.True(JsLevels.Compare(EcmaVersion.Latest, EcmaVersion.ES2025) > 0);
        Assert.True(JsLevels.Compare(EcmaVersion.ES5, EcmaVersion.ES2015) < 0);
        Assert.Equal("ES5", JsLevels.Name(EcmaVersion.ES5));
        Assert.Equal("ES2020", JsLevels.Name(EcmaVersion.ES2020));
    }
}
