using Acornima;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class JsFeaturesTests
{
    [Fact]
    public void Arrow_Es5_Found()
    {
        var r = JsFeatures.FirstAbove("var a = 1;\nvar f = x => x;\n", false, EcmaVersion.ES5);
        Assert.NotNull(r);
        Assert.Equal("화살표 함수(=>)", r!.Value.Feature.Name);
        Assert.Equal(2, r.Value.Line);
    }

    [Fact]
    public void TemplateLiteral_Es5_Found()
    {
        var r = JsFeatures.FirstAbove("var s = `a${b}`;\n", false, EcmaVersion.ES5);
        Assert.Equal("템플릿 문자열(`)", r!.Value.Feature.Name);
    }

    [Fact]
    public void Let_Es5_Found()
    {
        var r = JsFeatures.FirstAbove("\nlet a = 1;\n", false, EcmaVersion.ES5);
        Assert.Equal("let/const", r!.Value.Feature.Name);
        Assert.Equal(2, r.Value.Line);
    }

    [Fact]
    public void OptionalChain_Es2015_Found()
    {
        var r = JsFeatures.FirstAbove("let a = 1;\nvar b = a?.c;\n", false, EcmaVersion.ES2015);
        Assert.Equal("?.", r!.Value.Feature.Name);
        Assert.Equal(2, r.Value.Line);
    }

    [Fact]
    public void NothingAbove_Null()
    {
        Assert.Null(JsFeatures.FirstAbove("var a = 1;\n", false, EcmaVersion.ES5));
        Assert.Null(JsFeatures.FirstAbove("var a = ;\n", false, EcmaVersion.ES5));
    }

    [Fact]
    public void ObjectSpread_Es2015_Found()
    {
        var r = JsFeatures.FirstAbove("var b = {...a};\n", false, EcmaVersion.ES2015);
        Assert.Equal("...(펼치기)", r!.Value.Feature.Name);
        Assert.Equal("object spread/rest (...)", r.Value.Feature.EnglishName);
        Assert.Null(JsFeatures.FirstAbove("var b = {...a};\n", false, EcmaVersion.ES2018));
    }

    [Fact]
    public void ArgumentSpread_Es2015_NotFound() =>
        Assert.Null(JsFeatures.FirstAbove("f(...args);\n", false, EcmaVersion.ES2015));
}
