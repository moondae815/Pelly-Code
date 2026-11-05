using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class JsTokenScanTests
{
    [Fact]
    public void Balanced_NoProblems() =>
        Assert.Empty(JsTokenScan.Problems("function f(a) {\n  return [a, (1)];\n}\n"));

    [Fact]
    public void ExtraBrace_JS901()
    {
        var p = Assert.Single(JsTokenScan.Problems("function f() {\n}\n}\n"));
        Assert.Equal("JS901", p.Code);
        Assert.Equal(3, p.Line);
    }

    [Fact]
    public void BraceInString_Ignored() =>
        Assert.Empty(JsTokenScan.Problems("var s = \"{\" + '(' + `[${1}`;\n// }\n/* ) */\n"));

    [Fact]
    public void RegexWithQuote_NotString() =>
        Assert.Empty(JsTokenScan.Problems("var r = /'/g;\nvar t = 1;\n"));

    [Fact]
    public void UnclosedComment_JS905()
    {
        var p = Assert.Single(JsTokenScan.Problems("var a;\n/* never closed\n"));
        Assert.Equal("JS905", p.Code);
        Assert.Equal(2, p.Line);
    }

    [Fact]
    public void UnclosedString_JS904_UnclosedParen_JS902()
    {
        Assert.Contains(JsTokenScan.Problems("var a = 'x;\n"), p => p.Code == "JS904" && p.Line == 1);
        Assert.Contains(JsTokenScan.Problems("f(1;\n"), p => p.Code == "JS902");
        Assert.Contains(JsTokenScan.Problems("var a = [1;\n"), p => p.Code == "JS903");
    }

    [Fact]
    public void FunctionDeclarations_FoundWithEndLine()
    {
        var f = JsTokenScan.FunctionDeclarations("// c\nfunction a(x, y) {\n  if (x) { '}' }\n}\nvar z = 1;\nfunction b() {}\n");
        Assert.Equal(2, f.Count);
        Assert.Equal("a", f[0].Name);
        Assert.Equal(new[] { "x", "y" }, f[0].Parameters.ToArray());
        Assert.Equal(1, f[0].StartLine);
        Assert.Equal(2, f[0].DeclLine);
        Assert.Equal(4, f[0].EndLine);
        Assert.Equal("b", f[1].Name);
        Assert.Equal(6, f[1].EndLine);
    }

    [Fact]
    public void RegexAfterReturn_NotString() =>
        Assert.Empty(JsTokenScan.Problems("function t(x){ return /'/.test(x); }\n"));
}
