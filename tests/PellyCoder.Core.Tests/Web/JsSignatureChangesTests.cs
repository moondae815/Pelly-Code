using PellyCoder.Core.Models;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public sealed class JsSignatureChangesTests
{
    private const string Before =
        "function a(x) {}\n" +          // 1
        "function fnSearch(f) {\n" +    // 2
        "  return f;\n" +               // 3
        "}\n" +                         // 4
        "function z() {}\n";            // 5

    [Fact]
    public void Function_Renamed()
    {
        var change = Assert.Single(JsSignatureChanges.ForFunction(Before, Before.Replace("function fnSearch(", "function fnFind("), false, "fnSearch"));
        Assert.True(change.Renamed);
        Assert.False(change.CountChanged);
        Assert.Equal("fnSearch(f)", change.Before.Display);
        Assert.Equal("fnFind(f)", change.After.Display);
    }

    [Fact]
    public void Function_ParamAppended_OnlyAppended()
    {
        var change = Assert.Single(JsSignatureChanges.ForFunction(Before, Before.Replace("fnSearch(f)", "fnSearch(f, page)"), false, "fnSearch"));
        Assert.False(change.Renamed);
        Assert.True(change.CountChanged);
        Assert.True(change.OnlyAppended);
    }

    [Fact]
    public void Function_Reordered_SameCount()
    {
        const string two = "function g(a, b) {}\n";
        var change = Assert.Single(JsSignatureChanges.ForFunction(two, "function g(b, a) {}\n", false, "g"));
        Assert.False(change.CountChanged);
        Assert.False(change.Renamed);
        Assert.False(change.OnlyAppended);
    }

    [Fact]
    public void Function_ParamRemoved_NotOnlyAppended()
    {
        var change = Assert.Single(JsSignatureChanges.ForFunction("function g(a, b) {}\n", "function g(a) {}\n", false, "g"));
        Assert.True(change.CountChanged);
        Assert.False(change.OnlyAppended);
    }

    [Fact]
    public void Function_Unchanged_Empty() =>
        Assert.Empty(JsSignatureChanges.ForFunction(Before, Before.Replace("return f;", "return !f;"), false, "fnSearch"));

    [Fact]
    public void Lines_PairsDeclarationsInOrder()
    {
        var after = "function a(x) {}\nfunction fnSearch(f, p) {\n  return f;\n}\nfunction extra() {}\nfunction z() {}\n";
        var change = Assert.Single(JsSignatureChanges.InLines(Before, after, false, new TextSpanInfo(2, 4)));
        Assert.Equal("fnSearch(f, p)", change.After.Display);
    }

    [Fact]
    public void Page_BlockFunction()
    {
        const string page = "<p>x</p>\n<script>\nfunction go(n) {}\n</script>\n";
        var change = Assert.Single(JsSignatureChanges.ForFunction(page, page.Replace("go(n)", "go(n, m)"), true, "go"));
        Assert.Equal("go(n, m)", change.After.Display);
        Assert.Single(JsSignatureChanges.InLines(page, page.Replace("go(n)", "goTo(n)"), true, new TextSpanInfo(2, 4)));
    }
}
