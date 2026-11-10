using System.IO.Abstractions.TestingHelpers;
using System.Text;
using System.IO.Abstractions;
using Acornima;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Verification;
using PellyCoder.Core.Web;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Verification;

public sealed class JsFormFieldsStepTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\site" : "/site";
    private static readonly string PagePath = Root + "/page.asp";
    private static readonly string JsPath = Root + "/js/a.js";
    private static readonly WebCodecRouter Codec = new(new WebTextCodec("cp949"), new AspTextCodec("cp949"), [".asp", ".inc"]);

    private static FileSnapshot Snapshot(string path, string text) => new(path, Encoding.UTF8, false, "\n", true, "sha", text);

    private static Lazy<WebSiteIndexResult> EmptyIndex() => new(() => new WebSiteIndexResult([], 0, false));

    private static RunContext Ctx(string path, string original, string current, WebRunState state)
    {
        var request = new RunRequest(path, null, null, null, "x", true, false, null, Kind: EditKind.Js);
        var ctx = new RunContext("r1", request, new PellyOptions())
        {
            FilePath = path,
            Workspace = new Workspace(Root, Root + "/.pelly"),
            Original = Snapshot(path, original),
            Current = Snapshot(path, current),
        };
        ctx.Web = state;
        return ctx;
    }

    private static WebRunState PageState(string original, Lazy<WebSiteIndexResult> index)
    {
        var page = AspPage.Parse(original);
        var views = Enumerable.Range(0, page.ClientBlocks.Count).Select(i => PageBlockView.Build(page, original, i)).ToList();
        return new WebRunState(EditKind.Js, WebTargetUnit.Lines, new TextSpanInfo(4, 4), original, "CP949", true, Root, index)
        {
            Page = new WebPageState(page, 0, views[0], views),
            OriginalLevel = EcmaVersion.ES5,
            TargetLevel = EcmaVersion.ES5,
        };
    }

    /// <summary>The site after the edit (on disk), indexed.</summary>
    private static (IFileSystem Fs, Lazy<WebSiteIndexResult> Index) Site(params (string Path, string Text)[] files)
    {
        var fs = new MockFileSystem();
        foreach (var (p, t) in files) fs.AddFile(Root + "/" + p, new MockFileData(Encoding.UTF8.GetBytes(t)));
        var index = WebSiteIndex.Load(fs, Root, new WebFileFilter(Root, []), Codec, Root + "/.pelly/cache/web-site-index.json");
        return (fs, new Lazy<WebSiteIndexResult>(() => index));
    }

    private static (StepResult Result, AspCheck? Row) Run(IFileSystem fs, RunContext ctx)
    {
        var result = new JsFormFieldsStep().VerifyAsync(ctx, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        return (result, ctx.Web!.Checks.GetValueOrDefault(JsFormFieldsStep.Row));
    }

    private static WebRunState JsState(string original, Lazy<WebSiteIndexResult> index) =>
        new(EditKind.Js, WebTargetUnit.Lines, new TextSpanInfo(1, 1), original, "UTF-8", true, Root, index);

    private const string Saver = "<%\nv = Request.Form(\"memoText\")\n%>\n";

    [Fact]
    public void JsFile_AjaxKeyRenamed_Warned()
    {
        const string original = "$.post(\"memo_save.asp\", { memoText: t });\n";
        const string current = "$.post(\"memo_save.asp\", { memo: t });\n";
        var (fs, index) = Site(("admin/memo.asp", "<script src=\"/js/a.js\"></script>\n"), ("js/a.js", current),
            ("admin/memo_save.asp", Saver));
        var (result, row) = Run(fs, Ctx(JsPath, original, current, JsState(original, index)));
        Assert.Equal(StepResult.Pass, result);
        Assert.Equal(AspCheckStatus.Warned, row!.Status);
        Assert.Equal("admin/memo_save.asp가 읽는 memoText(줄 2)를 js/a.js의 ajax(줄 1)이 보내지 않음, 보내는 비슷한 이름: memo", row.Detail);
    }

    [Fact]
    public void JsFile_NoAjax_NoRow()
    {
        const string js = "function a() { return 1; }\n";
        var (fs, index) = Site(("js/a.js", js));
        var (result, row) = Run(fs, Ctx(JsPath, js, "function a() { return 2; }\n", JsState(js, index)));
        Assert.Equal(StepResult.Pass, result);
        Assert.Null(row);
    }

    private static string ActionPage(string target) =>
        "<form name=\"frm\" method=\"post\">\n" +        // 1
        "<input name=\"memoText\">\n" +                    // 2
        "</form>\n" +                                       // 3
        "<script>\n" +                                      // 4
        "function go() { frm.action = \"" + target + "\"; frm.submit(); }\n" + // 5
        "</script>\n";

    [Fact]
    public void Page_ActionAssignmentChanged_Warned()
    {
        var original = ActionPage("a.asp");
        var current = ActionPage("b.asp");
        var (fs, index) = Site(("memo.asp", current), ("a.asp", Saver), ("b.asp", "<%\nv = Request.Form(\"memoText\")\nw = Request.Form(\"writer\")\n%>\n"));
        var (_, row) = Run(fs, Ctx(PagePath.Replace("page.asp", "memo.asp"), original, current, PageState(original, index)));
        Assert.Equal(AspCheckStatus.Warned, row!.Status);
        Assert.Equal("b.asp가 읽는 writer(줄 3)를 memo.asp의 폼(줄 1)이 보내지 않음", row.Detail);
    }

    [Fact]
    public void IndexUnreadable_Skipped()
    {
        var original = ActionPage("a.asp");
        var index = new Lazy<WebSiteIndexResult>(() => throw new IOException("busy"));
        var (_, row) = Run(new MockFileSystem(), Ctx(PagePath, original, ActionPage("b.asp"), PageState(original, index)));
        Assert.Equal(AspCheckStatus.Skipped, row!.Status);
        Assert.Equal("사이트 색인을 읽지 못함", row.Detail);
    }
}
