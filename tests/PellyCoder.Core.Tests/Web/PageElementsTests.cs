using PellyCoder.Core.Asp;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public sealed class PageElementsTests
{
    private static IReadOnlyList<ElementRef> Refs(string page) => PageElements.References(AspPage.Parse(page), page);

    private static IReadOnlyList<string> Check(string original, string patched, params RelatedText[] related) =>
        PageElements.NewBroken(AspPage.Parse(original), original, AspPage.Parse(patched), patched, related);

    [Fact]
    public void References_GetElementById_Jquery_GetElementsByName()
    {
        var page =
            "<p>x</p>\n" +                                  // 1
            "<script>\n" +                                  // 2
            "document.getElementById(\"a\").value = 1;\n" + // 3
            "$('#b').hide();\n" +                           // 4
            "var c = document.getElementsByName(\"c\");\n" + // 5
            "</script>\n";
        Assert.Equal(
            [new ElementRef(ElementKind.Id, "a", 3), new ElementRef(ElementKind.Id, "b", 4), new ElementRef(ElementKind.Name, "c", 5)],
            Refs(page));
    }

    [Fact]
    public void References_JquerySelectorParts()
    {
        var page = "<script>\njQuery(\"#f1 input[name=custId]\").val('');\n</script>\n";
        Assert.Equal([new ElementRef(ElementKind.Id, "f1", 2), new ElementRef(ElementKind.Name, "custId", 2)], Refs(page));
    }

    [Fact]
    public void References_OtherDocument_Ignored()
    {
        var page = "<script>\n" +
                   "opener.document.getElementById(\"x\").focus();\n" +
                   "$(\"#y\", opener.document).hide();\n" +
                   "$(\"<div id='z'>\").appendTo('body');\n" +
                   "</script>\n";
        Assert.Empty(Refs(page));
    }

    [Fact]
    public void References_InComment_Ignored()
    {
        var page = "<script>\n// $(\"#x\").hide();\n/* document.getElementById(\"y\") */\n</script>\n";
        Assert.Empty(Refs(page));
    }

    [Fact]
    public void References_Concat_Or_ServerValue_Ignored()
    {
        var page = "<script>\n$(\"#\" + id).hide();\n$(\"#row<%= i %>\").hide();\n</script>\n";
        Assert.Empty(Refs(page));
    }

    [Fact]
    public void References_ServerStatementsBlock_Counted()
    {
        var page = "<script>\n<% If x Then %>\n$(\"#a\").hide();\n<% End If %>\n</script>\n";
        Assert.Equal([new ElementRef(ElementKind.Id, "a", 3)], Refs(page));
    }

    [Fact]
    public void Definitions_Attributes_And_Runtime()
    {
        var text = "<input id=\"a\" name='b'>\n" +   // 1
                   "el.id = \"c\";\n" +               // 2
                   "x.setAttribute(\"name\", \"d\");\n"; // 3
        Assert.Equal(
            [
                new ElementDef(ElementKind.Id, "a", null, 1), new ElementDef(ElementKind.Name, "b", null, 1),
                new ElementDef(ElementKind.Id, "c", null, 2), new ElementDef(ElementKind.Name, "d", null, 3),
            ],
            PageElements.Definitions(text, null).OrderBy(d => d.Line).ThenBy(d => d.Value, StringComparer.Ordinal));
    }

    [Fact]
    public void Definitions_VbDoubledQuotes()
    {
        var text = "<% Response.Write \"<input name=\"\"custId\"\">\" %>\n";
        Assert.Contains(new ElementDef(ElementKind.Name, "custId", null, 1), PageElements.Definitions(text, null));
    }

    [Fact]
    public void Definitions_ServerValuePrefix()
    {
        var text = "<tr id=\"row<%= i %>\">\n<input name=\"<%= n %>\">\n";
        var defs = PageElements.Definitions(text, null);
        Assert.Contains(new ElementDef(ElementKind.Id, "row*", null, 1), defs);
        Assert.Contains(new ElementDef(ElementKind.Name, "*", null, 2), defs);
    }

    private const string FormHead =
        "<form>\n" +                       // 1
        "<input name=\"custId\">\n" +      // 2
        "</form>\n" +                      // 3
        "<script>\n" +                     // 4
        "function go(f) {\n";              // 5

    [Fact]
    public void Check_NewJqueryIdWithOnlyName_Warns()
    {
        var original = FormHead + "  f.custId.focus();\n}\n</script>\n";
        var patched = FormHead + "  $(\"#custId\").focus();\n}\n</script>\n";
        Assert.Equal(["#custId(줄 6) — id 없음, name=\"custId\"는 있음(줄 2)"], Check(original, patched));
    }

    [Fact]
    public void Check_ExistingBrokenRef_NotReported()
    {
        var original = FormHead + "  $(\"#gone\").hide();\n  var a = 1;\n}\n</script>\n";
        var patched = FormHead + "  $(\"#gone\").hide();\n  var a = 2;\n}\n</script>\n";
        Assert.Empty(Check(original, patched));
    }

    private const string Tail = "<script>\n$(\"#a\").hide();\n</script>\n";

    [Fact]
    public void Check_IdRemovedFromHtml_Warns()
    {
        var original = "<div id=\"a\"></div>\n<p></p>\n" + Tail;
        var patched = "<div></div>\n<p></p>\n" + Tail;
        Assert.Equal(["#a(줄 4) — id 없음"], Check(original, patched));
    }

    [Fact]
    public void Check_IdMoved_NoWarning()
    {
        var original = "<div id=\"a\"></div>\n<p></p>\n" + Tail;
        var patched = "<div></div>\n<p id=\"a\"></p>\n" + Tail;
        Assert.Empty(Check(original, patched));
    }

    [Fact]
    public void Check_CaseOnly_Hint()
    {
        var original = "<input id=\"CustId\">\n<script>\nvar x = 1;\n</script>\n";
        var patched = "<input id=\"CustId\">\n<script>\n$(\"#custId\").hide();\n</script>\n";
        Assert.Equal(["#custId(줄 3) — id 없음, 대소문자만 다름: CustId(줄 1)"], Check(original, patched));
    }

    [Fact]
    public void Check_PrefixDefinition_Matches()
    {
        var original = "<tr id=\"row<%= i %>\"></tr>\n<script>\nvar x = 1;\n</script>\n";
        var patched = "<tr id=\"row<%= i %>\"></tr>\n<script>\ndocument.getElementById(\"row3\").focus();\n</script>\n";
        Assert.Empty(Check(original, patched));
    }

    [Fact]
    public void Check_RelatedFileDefinition_Matches_And_FileHint()
    {
        var original = "<script>\nvar x = 1;\n</script>\n";
        var patchedQ = "<script>\n$(\"#q\").hide();\n</script>\n";
        Assert.Empty(Check(original, patchedQ, new RelatedText("inc/head.inc", "<p></p>\n<input id=\"q\">\n")));

        var patchedP = "<script>\n$(\"#p\").hide();\n</script>\n";
        Assert.Equal(["#p(줄 2) — id 없음, name=\"p\"는 있음(inc/head.inc:2)"],
            Check(original, patchedP, new RelatedText("inc/head.inc", "<p></p>\n<input name=\"p\">\n")));
    }

    [Fact]
    public void Check_NameReference_WithOnlyId_Hint()
    {
        var original = "<input id=\"k\">\n<script>\nvar x = 1;\n</script>\n";
        var patched = "<input id=\"k\">\n<script>\nvar e = document.getElementsByName(\"k\");\n</script>\n";
        Assert.Equal(["[name=k](줄 3) — name 없음, id=\"k\"는 있음(줄 1)"], Check(original, patched));
    }

    [Fact]
    public void Definitions_LargePage_IsFast()
    {
        var text = string.Concat(Enumerable.Range(0, 8000).Select(i => $"<td><input id=\"a{i}\" name=\"n{i}\"></td>\n"));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var defs = PageElements.Definitions(text, null);
        watch.Stop();
        Assert.Equal(16000, defs.Count);
        Assert.Equal(new ElementDef(ElementKind.Name, "n7999", null, 8000), defs[^1]);
        Assert.True(watch.ElapsedMilliseconds < 1000, $"took {watch.ElapsedMilliseconds} ms");
    }
}
