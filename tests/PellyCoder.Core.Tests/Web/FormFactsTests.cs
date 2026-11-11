using PellyCoder.Core.Asp;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public sealed class FormFactsTests
{
    private static FileFormFacts Page(string text) => FormFacts.OfPage(AspPage.Parse(text), text);

    private static FormSender OnlySender(string text) => Assert.Single(Page(text).Senders);

    [Fact]
    public void Reads_FormAnyQueryString()
    {
        var text =
            "<%\n" +                                  // 1
            "a = Request.Form(\"a\")\n" +             // 2
            "b = Request(\"b\")\n" +                  // 3
            "c = Request.QueryString(\"c\")\n" +      // 4
            "' z = Request.Form(\"z\")\n" +           // 5
            "%>\n";
        var facts = Page(text);
        Assert.Equal(
            [new FormRead("a", FormReadSource.Form, 2), new FormRead("b", FormReadSource.Any, 3),
             new FormRead("c", FormReadSource.QueryString, 4)],
            facts.Reads);
        Assert.False(facts.AllRead);
    }

    [Fact]
    public void Reads_AllRead_ForEachAndVariableKey()
    {
        Assert.True(Page("<%\nFor Each k In Request.Form\nNext\n%>\n").AllRead);
        Assert.True(Page("<%\nFunction Get(name)\nGet = Request(name)\nEnd Function\n%>\n").AllRead);
        Assert.True(Page("<%\nn = Request.Form.Count\n%>\n").AllRead);
        Assert.False(Page("<%\nv = Request.QueryString(name)\n%>\n").AllRead);
    }

    [Fact]
    public void Form_ActionMethodNameFields()
    {
        var s = OnlySender(
            "<form name=\"frm\" action=\"save.asp\" method=\"post\"><input name=\"custId\"><select name=\"status\"></select>" +
            "<textarea name=\"memo\"></textarea></form>\n");
        Assert.Equal(FormSenderKind.Form, s.Kind);
        Assert.Equal("save.asp", s.Url);
        Assert.False(s.UrlUnknown);
        Assert.True(s.Post);
        Assert.Equal(["custId", "status", "memo"], s.Fields);
        Assert.Equal(1, s.Line);
        Assert.Equal("frm", s.FormName);
    }

    [Fact]
    public void Form_NoAction_UrlNullNotUnknown()
    {
        var s = OnlySender("<p>x</p>\n<form id=\"f1\" method=\"POST\">\n<input name=\"a\">\n</form>\n");
        Assert.Null(s.Url);
        Assert.False(s.UrlUnknown);
        Assert.Equal("f1", s.FormId);
        Assert.Equal(2, s.Line);
    }

    [Fact]
    public void Form_DefaultMethodIsGet() =>
        Assert.False(OnlySender("<form action=\"list.asp\"><input name=\"q\"></form>\n").Post);

    [Fact]
    public void Form_ServerValueName_Prefix()
    {
        var s = OnlySender("<form action=\"s.asp\" method=\"post\">\n<% For i = 1 To 3 %>\n<input name=\"row<%= i %>\">\n<% Next %>\n</form>\n");
        Assert.Equal(["row*"], s.Fields);
    }

    [Fact]
    public void Form_InVbString()
    {
        var s = OnlySender(
            "<% Response.Write \"<form action=\"\"save.asp\"\" method=\"\"post\"\"><input name=\"\"k\"\">\" %>\n" +
            "</form>\n");
        Assert.Equal("save.asp", s.Url);
        Assert.True(s.Post);
        Assert.Equal(["k"], s.Fields);
    }

    [Fact]
    public void Form_FieldsInIfBranches_Union()
    {
        var s = OnlySender(
            "<form action=\"s.asp\" method=\"post\">\n<% If mode = 1 Then %>\n<input name=\"a\">\n<% Else %>\n" +
            "<input name=\"b\">\n<% End If %>\n</form>\n");
        Assert.Equal(["a", "b"], s.Fields);
    }

    [Fact]
    public void Form_Open_ConcatName_Include_ProcedureCall()
    {
        Assert.Null(OnlySender(
            "<form action=\"s.asp\" method=\"post\">\n<% Response.Write \"<input name=\"\"\" & n & \"\"\">\" %>\n</form>\n").Fields);
        Assert.Null(OnlySender(
            "<form action=\"s.asp\" method=\"post\">\n<!--#include file=\"fields.inc\"-->\n</form>\n").Fields);
        Assert.Null(OnlySender(
            "<%\nSub WriteFields()\nResponse.Write \"<input name=\"\"x\"\">\"\nEnd Sub\n%>\n" +
            "<form action=\"s.asp\" method=\"post\">\n<% WriteFields %>\n</form>\n").Fields);
        Assert.Null(OnlySender(
            "<form action=\"s.asp\" method=\"post\">\n<% Call Other(1) %>\n</form>\n").Fields);
    }

    [Fact]
    public void Form_Open_JsAddsFields()
    {
        var s = OnlySender(
            "<form id=\"frm\" action=\"s.asp\" method=\"post\"><input name=\"a\"></form>\n" +
            "<script>\n$(\"#frm\").append('<input name=\"b\">');\n</script>\n");
        Assert.Null(s.Fields);
    }

    [Fact]
    public void Form_ActionWithServerCode_UrlUnknown()
    {
        var s = OnlySender("<form action=\"<%= url %>\" method=\"post\"><input name=\"a\"></form>\n");
        Assert.True(s.UrlUnknown);
        Assert.Null(s.Url);
    }

    [Fact]
    public void Form_UrlQueryKeys()
    {
        var s = OnlySender("<form action=\"save.asp?mode=del&amp;x=1\" method=\"post\"><input name=\"a\"></form>\n");
        Assert.Equal("save.asp?mode=del&amp;x=1", s.Url);
        Assert.Equal(["a", "?mode", "?x"], s.Fields);
    }

    private static string Script(string js) => "<p>x</p>\n<script>\n" + js + "\n</script>\n";

    [Fact]
    public void Ajax_ObjectData_Post()
    {
        var s = OnlySender(Script("$.ajax({ url: \"/admin/x.asp\", type: \"POST\", data: { a: 1, \"b\": 2 } });"));
        Assert.Equal(FormSenderKind.Ajax, s.Kind);
        Assert.Equal("/admin/x.asp", s.Url);
        Assert.True(s.Post);
        Assert.Equal(["a", "b"], s.Fields);
        Assert.Equal(3, s.Line);
    }

    [Fact]
    public void Ajax_Serialize_Open() =>
        Assert.Null(OnlySender(Script("$.ajax({ url: \"x.asp\", type: \"post\", data: $(\"#f\").serialize() });")).Fields);

    [Fact]
    public void Ajax_VariableData_Open() =>
        Assert.Null(OnlySender(Script("jQuery.ajax({ url: 'x.asp', method: 'POST', data: params });")).Fields);

    [Fact]
    public void Ajax_PostShorthand()
    {
        var s = OnlySender(Script("$.post(\"save.asp\", { orderNo: no }, function (r) { done(r); });"));
        Assert.True(s.Post);
        Assert.Equal("save.asp", s.Url);
        Assert.Equal(["orderNo"], s.Fields);
    }

    [Fact]
    public void Ajax_GetShorthand_NoData_ZeroFields()
    {
        var s = OnlySender(Script("$.get(\"list.asp\", function (r) { show(r); });"));
        Assert.False(s.Post);
        Assert.Equal("list.asp", s.Url);
        Assert.Empty(s.Fields!);
    }

    [Fact]
    public void Ajax_VariableUrl_Unknown()
    {
        var s = OnlySender(Script("$.ajax({ url: base + \"x.asp\", type: \"POST\", data: { a: 1 } });"));
        Assert.True(s.UrlUnknown);
        Assert.Null(s.Url);
    }

    [Fact]
    public void Actions_FourShapes()
    {
        var facts = Page(Script(
            "frm.action = \"save.asp\";\n" +             // 3
            "document.frm.action = \"a.asp?m=\" + m;\n" + // 4
            "$(\"#frm\").attr(\"action\", \"b.asp\");\n" + // 5
            "document.getElementById(\"frm\").action = url;")); // 6
        Assert.Equal(
            [new FormAction("frm", "save.asp", 3), new FormAction("frm", "a.asp", 4), new FormAction("frm", "b.asp", 5),
             new FormAction("frm", null, 6)],
            facts.Actions);
    }

    [Fact]
    public void Actions_ComparisonIsNotAssignment() =>
        Assert.Empty(Page(Script("if (frm.action == \"x.asp\") go();")).Actions);

    [Fact]
    public void OfScript_AjaxInJsFile()
    {
        var facts = FormFacts.OfScript("function save() {\n  $.post(\"memo_save.asp\", { memoText: t });\n}\nfrm.action = \"x.asp\";\n");
        var s = Assert.Single(facts.Senders);
        Assert.Equal(2, s.Line);
        Assert.Equal(["memoText"], s.Fields);
        Assert.Equal([new FormAction("frm", "x.asp", 4)], facts.Actions);
        Assert.Empty(facts.Reads);
    }

    [Fact]
    public void Form_Open_IncludeHelperCall()
    {
        // MakeSelect lives in an include: the form cannot know the field it writes.
        Assert.Null(OnlySender("<form action=\"s.asp\" method=\"post\">\n<%= MakeSelect(\"status\") %>\n</form>\n").Fields);
        Assert.Null(OnlySender("<form action=\"s.asp\" method=\"post\">\n<% WriteStatus \"a\" %>\n</form>\n").Fields);
        // Built-ins and declared arrays do not open it.
        Assert.Equal(["a"], OnlySender(
            "<% Dim names(2) %>\n<form action=\"s.asp\" method=\"post\">\n<input name=\"a\" value=\"<%= Server.HTMLEncode(Trim(names(0))) %>\">\n</form>\n").Fields);
    }

    [Fact]
    public void Reads_ServerVariablesIgnored()
    {
        var facts = Page("<%\nip = Request(\"REMOTE_ADDR\")\nr = Request(\"HTTP_REFERER\")\nu = Request(\"URL\")\nk = Request(\"key\")\n%>\n");
        Assert.Equal(["key"], facts.Reads.Select(r => r.Key));
    }

    [Fact]
    public void WritesAction_AliasOrAttribute()
    {
        Assert.True(Page(Script("var f = document.frm; f.action = \"x.asp\";")).WritesAction);
        Assert.True(Page("<input type=\"button\" onclick=\"frm.action='x.asp'; frm.submit();\">\n").WritesAction);
        Assert.True(FormFacts.OfScript("$(\"form[name=frm]\").attr(\"action\", u);\n").WritesAction);
        Assert.False(Page("<form method=\"post\"><input name=\"a\"></form>\n").WritesAction);
    }

    [Fact]
    public void Form_InCommentsOrJsStrings_NotSenders()
    {
        Assert.Empty(Page("<!-- <form action=\"old.asp\" method=\"post\"><input name=\"a\"></form> -->\n").Senders);
        Assert.Empty(Page("<%\n' Response.Write \"<form action=\"\"old.asp\"\">\"\n%>\n").Senders);
        Assert.Empty(Page("<script>\nvar f = $('<form action=\"x.asp\" method=\"post\"><input name=\"a\"></form>');\n</script>\n").Senders);
        // A VBScript string still counts, and so does a form after an include line.
        Assert.Single(Page("<!--#include file=\"a.inc\"-->\n<% Response.Write \"<form action=\"\"s.asp\"\">\" %>\n</form>\n").Senders);
    }

    [Fact]
    public void Empty_WhenNothing()
    {
        var facts = Page("<p>hello</p>\n");
        Assert.True(facts.IsEmpty);
        Assert.True(FileFormFacts.Empty.IsEmpty);
    }
}
