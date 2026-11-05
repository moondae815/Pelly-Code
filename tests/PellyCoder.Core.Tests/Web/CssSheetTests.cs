using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class CssSheetTests
{
    [Fact]
    public void Rules_SelectorsSplitAndNormalized()
    {
        var sheet = CssSheet.Parse(".a  >b, .c { color: red; }\n");
        var rule = Assert.Single(sheet.Rules);
        Assert.Equal(new[] { ".a > b", ".c" }, rule.Selectors);
        Assert.Equal(".a > b,.c", rule.Selector);
        Assert.False(rule.IsAtRule);
        Assert.Null(rule.Container);
        Assert.Empty(sheet.Problems);
    }

    [Fact]
    public void Rules_InsideMedia_HaveContainer()
    {
        var sheet = CssSheet.Parse("@media  screen and (min-width: 1px) {\n  .a { color: red; }\n}\n");
        Assert.Equal(2, sheet.Rules.Count);
        var inner = sheet.Rules[1];
        Assert.Equal(".a", inner.Selector);
        Assert.Equal("@media screen and (min-width: 1px)", inner.Container);
        Assert.Equal(2, inner.StartLine);
        Assert.Equal(2, inner.EndLine);
        Assert.Empty(sheet.Problems);
    }

    [Fact]
    public void AtRule_IsAtRuleBlock()
    {
        var sheet = CssSheet.Parse("@font-face {\n  font-family: X;\n}\n@media print {\n  a { b: c }\n}\n");
        Assert.True(sheet.Rules[0].IsAtRule);
        Assert.Equal("@font-face", sheet.Rules[0].Selector);
        Assert.Single(sheet.Rules[0].Declarations);
        Assert.True(sheet.Rules[1].IsAtRule);
        Assert.Equal(4, sheet.Rules[1].StartLine);
        Assert.Equal(6, sheet.Rules[1].EndLine);
        Assert.False(sheet.Rules[2].IsAtRule);
    }

    [Fact]
    public void Declarations_WithLines()
    {
        var sheet = CssSheet.Parse(".a {\n  color: red;\n  margin: 0 auto\n}\n");
        var d = sheet.Rules[0].Declarations;
        Assert.Equal(2, d.Count);
        Assert.Equal(new CssDeclaration("color", "red", 2), d[0]);
        Assert.Equal(new CssDeclaration("margin", "0 auto", 3), d[1]);
        Assert.Equal(1, sheet.Rules[0].StartLine);
        Assert.Equal(4, sheet.Rules[0].EndLine);
        Assert.Equal("  color: red;", sheet.LineText(2));
    }

    [Fact]
    public void IeHacks_NoProblems()
    {
        var sheet = CssSheet.Parse(
            ".a {\n  *zoom: 1;\n  _height: 1%;\n" +
            "  filter: progid:DXImageTransform.Microsoft.gradient(startColorstr='#fff', endColorstr='#000');\n" +
            "  color: red\\9;\n}\n");
        Assert.Empty(sheet.Problems);
        Assert.True(sheet.UsesIeHacks);
        var props = sheet.Rules[0].Declarations.Select(x => x.Property).ToArray();
        Assert.Equal(new[] { "*zoom", "_height", "filter", "color" }, props);
        Assert.Equal("red\\9", sheet.Rules[0].Declarations[3].Value);
    }

    [Fact]
    public void PlainCss_NoIeHacksNoVendor()
    {
        var sheet = CssSheet.Parse("a { color: red; --x: 1; }");
        Assert.False(sheet.UsesIeHacks);
        Assert.False(sheet.UsesVendorPrefixes);
    }

    [Fact]
    public void VendorPrefixes_Detected()
    {
        Assert.True(CssSheet.Parse("a { -webkit-transition: none; }").UsesVendorPrefixes);
        Assert.True(CssSheet.Parse("a { -moz-box-sizing: border-box; }").UsesVendorPrefixes);
        Assert.True(CssSheet.Parse("a { -ms-filter: x; }").UsesVendorPrefixes);
        Assert.True(CssSheet.Parse("a { -o-transition: none; }").UsesVendorPrefixes);
    }

    [Fact]
    public void UnclosedComment_CSS001()
    {
        var sheet = CssSheet.Parse("a { b: c; }\n/* open\nmore\n");
        var p = Assert.Single(sheet.Problems);
        Assert.Equal("CSS001", p.Code);
        Assert.Equal(2, p.Line);
        Assert.Equal(1, p.Column);
    }

    [Fact]
    public void UnclosedString_CSS002()
    {
        var sheet = CssSheet.Parse("a { content: \"abc; }\n");
        Assert.Contains(sheet.Problems, p => p.Code == "CSS002" && p.Line == 1 && p.Column == 14);
    }

    [Fact]
    public void StrayBrace_CSS003()
    {
        var sheet = CssSheet.Parse("a { b: c; }\n}\n");
        var p = Assert.Single(sheet.Problems);
        Assert.Equal("CSS003", p.Code);
        Assert.Equal(2, p.Line);
    }

    [Fact]
    public void UnclosedBlock_CSS004()
    {
        var sheet = CssSheet.Parse("a {\n  b: c;\n");
        var p = Assert.Single(sheet.Problems);
        Assert.Equal("CSS004", p.Code);
        Assert.Equal(1, p.Line);
        Assert.Equal(3, p.Column);
        Assert.Single(sheet.Rules[0].Declarations);
    }

    [Fact]
    public void UnbalancedParen_CSS005()
    {
        var sheet = CssSheet.Parse("a { width: calc(1px + 2px; }\n");
        Assert.Contains(sheet.Problems, p => p.Code == "CSS005" && p.Line == 1);
    }

    [Fact]
    public void MissingSemicolon_CSS006()
    {
        var sheet = CssSheet.Parse("a {\n  color: red\n  margin: 0;\n}\n");
        var p = Assert.Single(sheet.Problems);
        Assert.Equal("CSS006", p.Code);
        Assert.Equal(3, p.Line);
        var d = sheet.Rules[0].Declarations;
        Assert.Equal(new CssDeclaration("color", "red", 2), d[0]);
        Assert.Equal(new CssDeclaration("margin", "0", 3), d[1]);
    }

    [Fact]
    public void MultiLineValue_NotCSS006()
    {
        var sheet = CssSheet.Parse("a {\n  font-family: a,\n    b;\n}\n");
        Assert.Empty(sheet.Problems);
        Assert.Single(sheet.Rules[0].Declarations);
    }

    [Fact]
    public void DeclarationOutsideRule_CSS007()
    {
        var sheet = CssSheet.Parse("@import url(x.css);\ncolor: red;\na { b: c; }\n");
        var p = Assert.Single(sheet.Problems);
        Assert.Equal("CSS007", p.Code);
        Assert.Equal(2, p.Line);
    }

    [Fact]
    public void BraceInString_Ignored()
    {
        var sheet = CssSheet.Parse("a:after { content: \"}\"; color: red; }\n");
        Assert.Empty(sheet.Problems);
        Assert.Equal(2, sheet.Rules[0].Declarations.Count);
    }

    [Fact]
    public void UrlUnquoted_Ignored()
    {
        var sheet = CssSheet.Parse("a { background: url(a;b.png) no-repeat; color: red; }\n");
        Assert.Empty(sheet.Problems);
        var d = sheet.Rules[0].Declarations;
        Assert.Equal(2, d.Count);
        Assert.Equal("url(a;b.png) no-repeat", d[0].Value);
    }

    [Fact]
    public void ExpressionWithBraces_NoProblems()
    {
        var sheet = CssSheet.Parse("a { width: expression(function(){ return 1; }()); }\n");
        Assert.Empty(sheet.Problems);
        Assert.True(sheet.UsesIeHacks);
    }

    [Fact]
    public void Matches_PartOfList()
    {
        var rule = CssSheet.Parse(".a  >b, .c { x: y }").Rules[0];
        Assert.True(CssSelector.Matches(rule, ".c"));
        Assert.True(CssSelector.Matches(rule, ".a>b"));
        Assert.False(CssSelector.Matches(rule, ".a"));
    }

    [Fact]
    public void Normalize_KeepsBracketsAndStrings()
    {
        Assert.Equal("a[title=\"x > y\"] + b ~ c", CssSelector.Normalize("a[title=\"x > y\"]+b   ~c"));
        Assert.Equal("a  b".Replace("  ", " "), CssSelector.Normalize("a \n  b"));
    }
}
