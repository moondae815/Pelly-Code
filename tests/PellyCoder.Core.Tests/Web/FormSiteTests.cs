using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public sealed class FormSiteTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\site" : "/site";
    private const string Cache = "/ws/.pelly/cache/web-site-index.json";

    /// <summary>The site after the edit (on disk).</summary>
    private static WebSiteIndexResult Site(params (string Path, string Text)[] files) => Site(2000, files);

    private static WebSiteIndexResult Site(int maxFiles, params (string Path, string Text)[] files)
    {
        var fs = new MockFileSystem();
        foreach (var (p, t) in files) fs.AddFile(Root + "/" + p, new MockFileData(Encoding.UTF8.GetBytes(t)));
        return WebSiteIndex.Load(fs, Root, new WebFileFilter(Root, []), new WebTextCodec("cp949"), Cache, maxFiles);
    }

    private static FileFormFacts Facts(string rel, string text) =>
        rel.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ? FormFacts.OfScript(text) : FormFacts.OfPage(AspPage.Parse(text), text);

    /// <summary>New mismatches when <paramref name="rel"/> was <paramref name="original"/> before the edit.</summary>
    private static IReadOnlyList<FormMismatch> Check(WebSiteIndexResult index, string rel, string original)
    {
        var before = FormSite.Build(index, new Dictionary<string, FileFormFacts> { [rel] = Facts(rel, original) });
        var after = FormSite.Build(index);
        return FormSite.NewMismatches(before, after, [rel]);
    }

    private const string List = "admin/order/list.asp";
    private const string Save = "admin/order/save.asp";

    private static string Form(string action, params string[] fields) =>
        $"<form name=\"frm\" action=\"{action}\" method=\"post\">\n" +
        string.Concat(fields.Select(f => $"<input name=\"{f}\">\n")) + "</form>\n";

    private static string Reader(params string[] keys) =>
        "<%\n" + string.Concat(keys.Select(k => $"v = Request.Form(\"{k}\")\n")) + "%>\n";

    [Fact]
    public void FormRenamesField_NotSent_WithSimilarName()
    {
        var index = Site((List, Form("save.asp", "custId", "memo")), (Save, Reader("custNo", "memo")));
        var m = Assert.Single(Check(index, List, Form("save.asp", "custNo", "memo")));
        Assert.Equal(FormMismatchKind.NotSent, m.Kind);
        Assert.Equal("admin/order/save.asp가 읽는 custNo(줄 2)를 admin/order/list.asp의 폼(줄 1)이 보내지 않음, 보내는 비슷한 이름: custId",
            m.Message);
    }

    [Fact]
    public void ReaderAddsKey_NobodySends()
    {
        var index = Site((List, Form("save.asp", "custNo")), ("admin/order/edit.asp", Form("save.asp", "custNo")),
            (Save, Reader("custNo", "writer")));
        var m = Assert.Single(Check(index, Save, Reader("custNo")));
        Assert.Equal(FormMismatchKind.NobodySends, m.Kind);
        Assert.Equal("admin/order/save.asp가 읽는 writer(줄 3)를 보내는 폼·ajax가 없음(보내는 곳 2곳)", m.Message);
    }

    [Fact]
    public void ReaderAddsKey_SomeSenderSends_NoWarning()
    {
        var index = Site((List, Form("save.asp", "custNo")), ("admin/order/edit.asp", Form("save.asp", "custNo", "writer")),
            (Save, Reader("custNo", "writer")));
        Assert.Empty(Check(index, Save, Reader("custNo")));
    }

    [Fact]
    public void ReadKeyCaseDiffers_NoWarning()
    {
        var index = Site((List, Form("save.asp", "CustNo")), (Save, Reader("custno")));
        Assert.Empty(Check(index, List, Form("save.asp", "custno")));
    }

    [Fact]
    public void ExistingMismatch_NotReported()
    {
        var index = Site((List, Form("save.asp", "memo", "extra")), (Save, Reader("custNo", "memo")));
        Assert.Empty(Check(index, List, Form("save.asp", "memo")));
    }

    [Fact]
    public void TwoFormsSameTarget_OneDropsKey_NoWarning()
    {
        var original = Form("save.asp", "a", "b") + Form("save.asp", "b");
        var edited = Form("save.asp", "a") + Form("save.asp", "b");
        var index = Site((List, edited), (Save, Reader("a", "b")));
        Assert.Empty(Check(index, List, original));
    }

    [Fact]
    public void OpenSender_NoWarning()
    {
        var edited = "<script>\n$.ajax({ url: \"save.asp\", type: \"POST\", data: $(\"#f\").serialize() });\n</script>\n";
        var original = "<script>\n$.ajax({ url: \"save.asp\", type: \"POST\", data: { custNo: 1 } });\n</script>\n";
        var index = Site((List, edited), (Save, Reader("custNo")));
        Assert.Empty(Check(index, List, original));
    }

    [Fact]
    public void AllRead_NoWarning()
    {
        var save = "<%\nFor Each k In Request.Form\nNext\nv = Request.Form(\"custNo\")\n%>\n";
        var index = Site((List, Form("save.asp", "custId")), (Save, save));
        Assert.Empty(Check(index, List, Form("save.asp", "custNo")));
    }

    [Fact]
    public void Reader_IncludeWithVariableKey_AllRead()
    {
        var save = "<!--#include virtual=\"/inc/func.inc\"-->\n" + Reader("custNo");
        var func = "<%\nFunction GetValue(name)\nGetValue = Request(name)\nEnd Function\n%>\n";
        var index = Site((List, Form("save.asp", "custId")), (Save, save), ("inc/func.inc", func));
        Assert.Empty(Check(index, List, Form("save.asp", "custNo")));
        Assert.True(FormSite.Build(index).AllRead(Save));
    }

    [Fact]
    public void Reader_ReadInInclude_NamesIncludeFile()
    {
        var save = "<!--#include virtual=\"/inc/read.inc\"-->\n<p>x</p>\n";
        var index = Site((List, Form("save.asp", "custId")), (Save, save), ("inc/read.inc", Reader("custNo")));
        var m = Assert.Single(Check(index, List, Form("save.asp", "custNo")));
        Assert.Equal("admin/order/save.asp가 읽는 custNo(inc/read.inc 줄 2)를 admin/order/list.asp의 폼(줄 1)이 보내지 않음, 보내는 비슷한 이름: custId",
            m.Message);
    }

    [Fact]
    public void NoSenders_NoWarning()
    {
        var index = Site((Save, Reader("custNo", "writer")));
        Assert.Empty(Check(index, Save, Reader("custNo")));
    }

    [Fact]
    public void NoAction_PostsToSelf()
    {
        const string page = "admin/order/edit.asp";
        string Text(string field) => "<form method=\"post\">\n<input name=\"" + field + "\">\n</form>\n" + Reader("a");
        var index = Site((page, Text("b")));
        var m = Assert.Single(Check(index, page, Text("a")));
        Assert.Equal(FormMismatchKind.NotSent, m.Kind);
        Assert.Equal(page, m.Target);
        Assert.Equal("a", m.Key);
    }

    private static string ActionPage(string assignment, params string[] fields) =>
        "<form name=\"frm\" method=\"post\">\n" + string.Concat(fields.Select(f => $"<input name=\"{f}\">\n")) + "</form>\n" +
        "<script>\nfunction go() { " + assignment + " frm.submit(); }\n</script>\n";

    [Fact]
    public void JsActionAssignment_Resolved()
    {
        const string memo = "admin/order/memo.asp";
        const string save = "admin/order/memo_save.asp";
        var index = Site((memo, ActionPage("frm.action = \"memo_save.asp\";", "memo")), (save, Reader("memoText")));
        var m = Assert.Single(Check(index, memo, ActionPage("frm.action = \"memo_save.asp\";", "memoText")));
        Assert.Equal(save, m.Target);

        // The assignment lives in a .js file the page loads.
        var page = "<script src=\"/js/memo.js\"></script>\n<form name=\"frm\" method=\"post\">\n<input name=\"memo\">\n</form>\n";
        var original = page.Replace("\"memo\"", "\"memoText\"", StringComparison.Ordinal);
        index = Site((memo, page), ("js/memo.js", "function go() { frm.action = \"memo_save.asp\"; frm.submit(); }\n"),
            (save, Reader("memoText")));
        Assert.Equal(save, Assert.Single(Check(index, memo, original)).Target);
    }

    [Fact]
    public void JsActionUnknown_NotPaired()
    {
        const string memo = "admin/order/memo.asp";
        var page = ActionPage("frm.action = url;", "memo") + Reader("memoText");
        var original = ActionPage("frm.action = url;", "memoText") + Reader("memoText");
        var index = Site((memo, page));
        Assert.Empty(Check(index, memo, original));
    }

    [Fact]
    public void IncForm_ResolvedFromIncluderFolder()
    {
        var list = "<!--#include virtual=\"/inc/form.inc\"-->\n";
        var index = Site((List, list), ("inc/form.inc", Form("save.asp", "custId")), (Save, Reader("custNo")));
        var m = Assert.Single(Check(index, "inc/form.inc", Form("save.asp", "custNo")));
        Assert.Equal(Save, m.Target);
        Assert.Equal("inc/form.inc", m.SenderFile);
    }

    [Fact]
    public void AspUsedAsInclude_ResolvedFromIncluderFolder()
    {
        var list = "<!--#include virtual=\"/inc/top.asp\"-->\n";
        var index = Site((List, list), ("inc/top.asp", Form("save.asp", "custId")), (Save, Reader("custNo")));
        var m = Assert.Single(Check(index, "inc/top.asp", Form("save.asp", "custNo")));
        Assert.Equal(Save, m.Target);

        // A form without action in it posts to the including page (and to inc/top.asp when that is opened directly).
        var site = FormSite.Build(Site((List, list), ("inc/top.asp", "<form method=\"post\"><input name=\"a\"></form>\n" + Reader("a"))));
        Assert.Single(site.SendersTo("inc/top.asp"));
        Assert.Single(site.SendersTo(List));
    }

    [Fact]
    public void JsAjax_ResolvedFromLoadingPageFolder()
    {
        const string js = "js/memo.js";
        var index = Site(("admin/order/memo.asp", "<script src=\"/js/memo.js\"></script>\n"),
            (js, "$.post(\"memo_save.asp\", { memo: t });\n"), ("admin/order/memo_save.asp", Reader("memoText")));
        var m = Assert.Single(Check(index, js, "$.post(\"memo_save.asp\", { memoText: t });\n"));
        Assert.Equal("admin/order/memo_save.asp", m.Target);
        Assert.Equal(FormSenderKind.Ajax, m.SenderKind);
        Assert.Equal("admin/order/memo_save.asp가 읽는 memoText(줄 2)를 js/memo.js의 ajax(줄 1)이 보내지 않음, 보내는 비슷한 이름: memo",
            m.Message);
    }

    [Fact]
    public void QueryKeyInUrl_SatisfiesAnyRead_NotFormRead()
    {
        var list = Form("save.asp?mode=del", "a");
        var anyRead = "<%\nv = Request.Form(\"a\")\nm = Request(\"mode\")\n%>\n";
        var index = Site((List, list), (Save, anyRead));
        Assert.Empty(Check(index, Save, Reader("a")));

        var formRead = Reader("a", "mode");
        index = Site((List, list), (Save, formRead));
        var m = Assert.Single(Check(index, Save, Reader("a")));
        Assert.Equal(FormMismatchKind.NobodySends, m.Kind);
        Assert.Equal("mode", m.Key);
    }

    [Fact]
    public void GetForm_FormRead_NobodySends()
    {
        var list = "<form action=\"save.asp\">\n<input name=\"k\">\n</form>\n";
        var index = Site((List, list), (Save, Reader("k")));
        var m = Assert.Single(Check(index, Save, "<p>x</p>\n"));
        Assert.Equal(FormMismatchKind.NobodySends, m.Kind);

        // Request("k") reads a GET form's field.
        index = Site((List, list), (Save, "<%\nv = Request(\"k\")\n%>\n"));
        Assert.Empty(Check(index, Save, "<p>x</p>\n"));
    }

    [Fact]
    public void External_ServerCode_IncTarget_MissingFile_NotPaired()
    {
        foreach (var action in new[] { "http://other/admin/order/save.asp", "//other/save.asp", "<%= url %>", "../../inc/read.inc", "missing.asp" })
        {
            var index = Site((List, Form(action, "custId")), (Save, Reader("custNo")), ("inc/read.inc", Reader("custNo")),
                ("admin/order/missing_reader.asp", Reader("custNo")));
            Assert.Empty(Check(index, List, Form(action, "custNo")));
        }
    }

    [Fact]
    public void Scope_UneditedPairsIgnored()
    {
        const string other = "admin/order/other.asp";
        var index = Site((List, Form("save.asp", "custNo")), (other, Form("save.asp", "x")), (Save, Reader("custNo")));
        // other.asp changed too (its old version sent custNo), but only list.asp is in the edited set.
        var before = FormSite.Build(index, new Dictionary<string, FileFormFacts>
        {
            [List] = Facts(List, Form("save.asp", "custNo")),
            [other] = Facts(other, Form("save.asp", "custNo")),
        });
        Assert.Empty(FormSite.NewMismatches(before, FormSite.Build(index), [List]));
        Assert.Single(FormSite.NewMismatches(before, FormSite.Build(index), [other]));
    }

    [Fact]
    public void SendersTo_GroupsPerFile()
    {
        var index = Site((List, Form("save.asp", "a") + Form("save.asp", "b")), (Save, Reader("a")));
        var g = Assert.Single(FormSite.Build(index).SendersTo(Save));
        Assert.Equal(List, g.File);
        Assert.Equal(["a", "b"], g.Fields);
        Assert.True(g.AnyPost);
        Assert.False(g.AnyGet);
        Assert.Equal(1, g.Line);
    }

    [Fact]
    public void QueryKeysOfJsAction_CountAsSent()
    {
        // The hidden mode input moved into the JS action's query (literal, and literal + value).
        string Page(string assignment, string hidden) => "<form name=\"frm\" method=\"post\">\n<input name=\"a\">\n" + hidden + "</form>\n" +
            "<script>\nfunction go(m) { " + assignment + " frm.submit(); }\n</script>\n";
        const string hidden = "<input type=\"hidden\" name=\"mode\">\n";
        var anyRead = "<%\nv = Request.Form(\"a\")\nm = Request(\"mode\")\n%>\n";
        foreach (var assignment in new[] { "frm.action = \"save.asp?mode=del\";", "frm.action = \"save.asp?mode=\" + m;" })
        {
            var index = Site((List, Page(assignment, "")), (Save, anyRead));
            Assert.Empty(Check(index, List, Page("frm.action = \"save.asp\";", hidden)));
        }
    }

    [Fact]
    public void JsActions_SameTarget_FieldsMerged_ConcatenatedQueryOpen()
    {
        string Page(string hidden) => "<form name=\"frm\" method=\"post\">\n<input name=\"a\">\n" + hidden + "</form>\n<script>\n" +
            "function fnSave() { frm.action = \"save.asp\"; frm.submit(); }\n" +
            "function fnDel(i) { frm.action = \"save.asp?mode=del&idx=\" + i; frm.submit(); }\n</script>\n";
        var anyRead = "<%\nv = Request.Form(\"a\")\nm = Request(\"mode\")\nx = Request(\"idx\")\n%>\n";
        var index = Site((List, Page("")), (Save, anyRead));
        Assert.Empty(Check(index, List, Page("<input type=\"hidden\" name=\"mode\">\n<input type=\"hidden\" name=\"idx\">\n")));

        // A tail built by + may carry any key.
        string Tail(string hidden) => "<form name=\"frm\" method=\"post\">\n<input name=\"a\">\n" + hidden + "</form>\n<script>\n" +
            "function go(m, i) { frm.action = \"save.asp?mode=\" + m + \"&idx=\" + i; frm.submit(); }\n</script>\n";
        index = Site((List, Tail("")), (Save, anyRead));
        Assert.Empty(Check(index, List, Tail("<input type=\"hidden\" name=\"idx\">\n")));
    }

    [Fact]
    public void IncludedAsp_AlsoRequestedDirectly_KeepsOwnFolderAndSelfPost()
    {
        const string board = "admin/board/view.asp";
        const string boardSave = "admin/board/save.asp";
        var m = "<!--#include virtual=\"/admin/board/view.asp\"-->\n";
        var index = Site(("m/view.asp", m), (board, Form("save.asp", "idx2")), (boardSave, Reader("idx")));
        var found = Assert.Single(Check(index, board, Form("save.asp", "idx")));
        Assert.Equal(boardSave, found.Target);

        var site = FormSite.Build(Site(("m/view.asp", m), (board, "<form method=\"post\"><input name=\"a\"></form>\n")));
        Assert.Single(site.SendersTo(board));
        Assert.Single(site.SendersTo("m/view.asp"));
    }

    // ---- review fixes ----

    [Fact]
    public void NewAjax_KeysNeverSent_NotWarned()
    {
        // A delete button added to the list page feeds a save page that handles several modes.
        var edited = "<script>\n$.post(\"save.asp\", { mode: \"del\", idx: i });\n</script>\n";
        var index = Site((List, edited), (Save, Reader("mode", "idx", "title", "content")),
            ("admin/order/write.asp", Form("save.asp", "mode", "idx", "title", "content")));
        Assert.Empty(Check(index, List, "<p>x</p>\n"));
    }

    [Fact]
    public void ChangedTarget_KeysNeverSentThere_NotWarned()
    {
        var index = Site((List, Form("save2.asp", "a")), (Save, Reader("a")), ("admin/order/save2.asp", Reader("a", "b")),
            ("admin/order/w.asp", Form("save2.asp", "a", "b")));
        Assert.Empty(Check(index, List, Form("save.asp", "a")));
    }

    [Fact]
    public void OpenBecomesClosed_NotWarned()
    {
        var original = "<form action=\"save.asp\" method=\"post\">\n<input name=\"a\">\n<% Call WriteMore() %>\n</form>\n";
        var index = Site((List, Form("save.asp", "a")), (Save, Reader("a", "b")));
        Assert.Empty(Check(index, List, original));
    }

    [Fact]
    public void ReaderAndSenderBothEdited_KeyNeverSentBySender_NotWarned()
    {
        // The apply end: save.asp now reads memo (write.asp sends it); list.asp's delete form never sent memo.
        const string write = "admin/order/write.asp";
        var index = Site((List, Form("save.asp", "idx", "mode")), (Save, Reader("idx", "mode", "memo")),
            (write, Form("save.asp", "idx", "mode", "memo")));
        var before = FormSite.Build(index, new Dictionary<string, FileFormFacts>
        {
            [List] = Facts(List, Form("save.asp", "idx")),
            [Save] = Facts(Save, Reader("idx", "mode")),
        });
        Assert.Empty(FormSite.NewMismatches(before, FormSite.Build(index), [List, Save]));
    }

    [Fact]
    public void AliasActionAssignment_NotSelfPost()
    {
        const string page = "admin/board/write.asp";
        string Text(string key) => "<%\nv = Request.Form(\"" + key + "\")\n%>\n<form name=\"frm\" method=\"post\">\n<input name=\"no\">\n</form>\n" +
            "<script>\nfunction go() { var f = document.frm; f.action = \"write_ok.asp\"; f.submit(); }\n</script>\n";
        var index = Site((page, Text("seq")));
        Assert.Empty(Check(index, page, Text("no")));
    }

    [Fact]
    public void InlineOnclickAction_NotSelfPost()
    {
        const string page = "admin/board/write.asp";
        string Text(string key) => "<%\nv = Request.Form(\"" + key + "\")\n%>\n<form name=\"frm\" method=\"post\">\n<input name=\"no\">\n" +
            "<input type=\"button\" onclick=\"frm.action='write_ok.asp'; frm.submit();\">\n</form>\n";
        var index = Site((page, Text("seq")));
        Assert.Empty(Check(index, page, Text("no")));
    }

    [Fact]
    public void AnyRead_NoNobodySends()
    {
        // Request("k") can come from a link or a redirect, which are not modelled.
        var index = Site((List, Form("save.asp", "custNo")), (Save, "<%\nv = Request.Form(\"custNo\")\nw = Request(\"page\")\n%>\n"));
        Assert.Empty(Check(index, Save, Reader("custNo")));
    }

    [Fact]
    public void TruncatedIndex_NoNobodySends()
    {
        // With maxFiles 1 only one page is indexed: a sender outside the index may send the key.
        var index = Site(2, (List, Form("save.asp", "custNo")), (Save, Reader("custNo", "writer")), ("z/other.asp", "<p>x</p>\n"));
        Assert.True(index.Truncated);
        Assert.Empty(Check(index, Save, Reader("custNo")));
    }
}
