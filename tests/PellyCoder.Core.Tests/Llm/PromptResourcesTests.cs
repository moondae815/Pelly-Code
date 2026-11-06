using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Tests.Llm;

public sealed class PromptResourcesTests
{
    private static readonly PromptResources Resources = PromptResources.LoadDefault();

    [Fact]
    public void Resources_AspVersion_AspV1()
    {
        Assert.Equal("asp-v1", Resources.AspVersion);
        Assert.Equal("asp-v1", Resources.VersionFor(EditKind.Asp));
        Assert.Equal("sql-v1", Resources.VersionFor(EditKind.Sql));
        Assert.DoesNotContain("# version", Resources.SystemAsp);
        Assert.DoesNotContain("# version", Resources.FewShotAsp);
        Assert.Contains("{LanguageHint}", Resources.SystemAsp);
        Assert.Contains("<<<<<<< SEARCH", Resources.SystemAsp);
        Assert.Contains("Replace(value, \"'\", \"''\")", Resources.SystemAsp);
        Assert.Contains("VBScript built-in functions and the site SQL functions named in RULES", Resources.SystemAsp);
        Assert.Contains("Methods of ADO objects such as conn.Execute or cmd.Execute are fine.", Resources.SystemAsp);
    }

    [Fact]
    public void SystemAsp_NoShortCircuitRule()
    {
        // Premeasure (docs/asp-premeasure.md): "If IsNumeric(x) And CLng(x) > 0" fails at run time on non-numbers.
        Assert.Contains("VBScript And/Or do not short-circuit: never convert or index a value in the same condition that "
            + "checks it (IsNumeric, IsDate, IsNull, IsEmpty, = \"\"); check first, convert inside a nested If.", Resources.SystemAsp);
    }

    [Fact]
    public void Resources_JsVersion_js_v1()
    {
        Assert.Equal("js-v1", Resources.JsVersion);
        Assert.Equal("js-v1", Resources.VersionFor(EditKind.Js));
        Assert.Equal(Resources.Version, Resources.VersionFor(EditKind.CSharp));
        Assert.DoesNotContain("# version", Resources.SystemJs);
        Assert.DoesNotContain("# version", Resources.FewShotJs);
        Assert.Contains("{LanguageHint}", Resources.SystemJs);
        Assert.Contains("<<<<<<< SEARCH", Resources.SystemJs);
        Assert.Contains("[EDITABLE: target]", Resources.SystemJs);
        Assert.Contains("[READ-ONLY: env]", Resources.SystemJs);
        Assert.Contains("document.write", Resources.SystemJs);
        Assert.Contains("<<<<<<< SEARCH", Resources.FewShotJs);
        Assert.Contains("alert(", Resources.FewShotJs);
    }

    [Fact]
    public void Resources_page_variants_js_page_v1_and_css_page_v1()
    {
        Assert.Equal("js-page-v1", Resources.JsPageVersion);
        Assert.Equal("css-page-v1", Resources.CssPageVersion);
        Assert.DoesNotContain("# version", Resources.SystemJsPage);
        Assert.DoesNotContain("# version", Resources.FewShotCssPage);
        Assert.Contains("{LanguageHint}", Resources.SystemJsPage);
        Assert.Contains("{LanguageHint}", Resources.SystemCssPage);
        Assert.Contains("<\\/script>", Resources.SystemJsPage);
        Assert.Contains("[READ-ONLY: html]", Resources.SystemJsPage);
        Assert.Contains("[READ-ONLY: page]", Resources.SystemJsPage);
        Assert.DoesNotContain("[READ-ONLY: file]", Resources.SystemJsPage);
        Assert.Contains("<%=", Resources.FewShotJsPage);
        Assert.Contains("<%=", Resources.FewShotCssPage);
        Assert.Contains("server code", Resources.SystemCssPage);
    }

    [Fact]
    public void Resources_CssVersion_css_v1()
    {
        Assert.Equal("css-v1", Resources.CssVersion);
        Assert.Equal("css-v1", Resources.VersionFor(EditKind.Css));
        Assert.DoesNotContain("# version", Resources.SystemCss);
        Assert.DoesNotContain("# version", Resources.FewShotCss);
        Assert.Contains("{LanguageHint}", Resources.SystemCss);
        Assert.Contains("<<<<<<< SEARCH", Resources.SystemCss);
        Assert.Contains("-moz-binding", Resources.SystemCss);
        Assert.Contains("<<<<<<< SEARCH", Resources.FewShotCss);
    }

    [Theory]
    [InlineData("fnSearch")]
    [InlineData("checkDate")]
    [InlineData("list.js")]
    [InlineData("admin.css")]
    [InlineData(".list_tbl")]
    public void Resources_WebFewShots_DoNotReuseCorpusNames(string name)
    {
        Assert.DoesNotContain(name, Resources.FewShotJs);
        Assert.DoesNotContain(name, Resources.FewShotCss);
    }
}
