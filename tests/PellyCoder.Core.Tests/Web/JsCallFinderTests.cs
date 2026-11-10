using PellyCoder.Core.Asp;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public sealed class JsCallFinderTests
{
    private static IReadOnlyList<JsCallSite> Script(string text) => JsCallFinder.InScript(text, JsCallSource.Js);

    private static IReadOnlyList<JsCallSite> Page(string text) => JsCallFinder.InPage(AspPage.Parse(text), text);

    [Fact]
    public void Calls_PlainQualifiedAndArgs()
    {
        var calls = Script("fnX(a, b(c, d), \"x,y\");\nCommon.fnX();\n");
        var fnX = calls.Single(c => c.Name == "fnX");
        Assert.Equal(3, fnX.Arguments);
        Assert.Equal(1, fnX.Line);
        Assert.False(fnX.Reference);
        Assert.Equal(JsCallSource.Js, fnX.Source);
        var q = calls.Single(c => c.Name == "Common.fnX");
        Assert.Equal(0, q.Arguments);
        Assert.Equal(2, q.Line);
        Assert.Contains(calls, c => c.Name == "b" && c.Arguments == 2);
    }

    [Fact]
    public void Calls_IgnoreStringsCommentsAndDeclarations()
    {
        var calls = Script("var s = \"fnX(\";\n// fnX(1)\n/* fnX(2) */\nfunction fnX(a) {}\nvar o = { fnX: function (a) {} };\nif (a) { while (b) {} }\n");
        Assert.DoesNotContain(calls, c => c.Name == "fnX");
        Assert.DoesNotContain(calls, c => c.Name is "if" or "while" or "function");
    }

    [Fact]
    public void Calls_New_Counted() =>
        Assert.Contains(Script("var x = new Grid(a);\n"), c => c.Name == "Grid" && c.Arguments == 1);

    [Fact]
    public void Calls_Unbalanced_ArgumentsNull() =>
        Assert.Null(Script("fnX(a, (b\n").Single(c => c.Name == "fnX").Arguments);

    [Fact]
    public void Calls_PlaceholderArgument_Counted()
    {
        const string page = "<script>\nfnX('<%= a %>', <%= b %>);\n</script>\n";
        var call = Page(page).Single(c => c.Name == "fnX");
        Assert.Equal(2, call.Arguments);
        Assert.Equal(2, call.Line);
        Assert.Equal(JsCallSource.PageScript, call.Source);
    }

    [Fact]
    public void Page_Attributes()
    {
        const string page =
            "<p>x</p>\n" +                                              // 1
            "<input type=\"button\" onclick=\"fnX(1)\">\n" +            // 2
            "<a href=\"javascript:fnX(2, 3)\">go</a>\n" +               // 3
            "<% Response.Write \"<a onclick=\"\"fnX()\"\">\" %>\n";     // 4
        var calls = Page(page).Where(c => c.Name == "fnX").OrderBy(c => c.Line).ToList();
        Assert.Equal(3, calls.Count);
        Assert.All(calls, c => Assert.Equal(JsCallSource.Attribute, c.Source));
        Assert.Equal((2, 1, "onclick"), (calls[0].Line, calls[0].Arguments!.Value, calls[0].AttributeName!));
        Assert.Equal((3, 2, "javascript:"), (calls[1].Line, calls[1].Arguments!.Value, calls[1].AttributeName!));
        Assert.Equal((4, 0, "onclick"), (calls[2].Line, calls[2].Arguments!.Value, calls[2].AttributeName!));
    }

    [Fact]
    public void Page_ScriptBlockLines_ArePageLines()
    {
        const string page = "<p>a</p>\n<p>b</p>\n<script>\nvar a = 1;\ngo(a);\n</script>\n";
        Assert.Equal(5, Page(page).Single(c => c.Name == "go").Line);
    }

    [Fact]
    public void References_SetTimeoutAssignClick()
    {
        const string text = "setTimeout(fnX, 10);\nb.onclick = fnX;\n$(a).click(fnX)\nfnXy(1);\nfnX(2);\nvar fnX2 = 1;\n";
        var refs = JsCallFinder.References(text, "fnX", JsCallSource.Js);
        Assert.Equal([1, 2, 3], refs.Select(r => r.Line));
        Assert.All(refs, r => Assert.True(r.Reference));
        Assert.All(refs, r => Assert.Null(r.Arguments));
    }
}
