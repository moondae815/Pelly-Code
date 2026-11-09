using System.IO.Abstractions.TestingHelpers;
using System.Text;
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

public sealed class JsElementsStepTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\site" : "/site";
    private static readonly string PagePath = Root + "/page.asp";
    private static readonly string JsPath = Root + "/js/a.js";
    private static readonly WebCodecRouter Codec = new(new WebTextCodec("cp949"), new AspTextCodec("cp949"), [".asp", ".inc"]);

    private const string Head =
        "<input name=\"custId\">\n" + // 1
        "<script>\n" +                // 2
        "function go(f) {\n";         // 3

    private const string Original = Head + "  f.custId.focus();\n}\n</script>\n";

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

    private static WebRunState PageState(string original)
    {
        var page = AspPage.Parse(original);
        var views = Enumerable.Range(0, page.ClientBlocks.Count).Select(i => PageBlockView.Build(page, original, i)).ToList();
        return new WebRunState(EditKind.Js, WebTargetUnit.Lines, new TextSpanInfo(4, 4), original, "CP949", true, Root, EmptyIndex())
        {
            Page = new WebPageState(page, 0, views[0], views),
            OriginalLevel = EcmaVersion.ES5,
            TargetLevel = EcmaVersion.ES5,
        };
    }

    private static (StepResult Result, AspCheck? Row) Run(RunContext ctx)
    {
        var result = new JsElementsStep(new MockFileSystem(), Codec).VerifyAsync(ctx, TestContext.Current.CancellationToken).GetAwaiter().GetResult();
        return (result, ctx.Web!.Checks.GetValueOrDefault(JsElementsStep.Row));
    }

    [Fact]
    public void JsFile_Skipped()
    {
        const string js = "function a() { $(\"#x\").hide(); }\n";
        var state = new WebRunState(EditKind.Js, WebTargetUnit.Lines, new TextSpanInfo(1, 1), js, "UTF-8", true, Root, EmptyIndex());
        var (result, row) = Run(Ctx(JsPath, js, js, state));
        Assert.Equal(StepResult.Pass, result);
        Assert.Equal(AspCheckStatus.Skipped, row!.Status);
        Assert.Equal("페이지가 아님", row.Detail);
    }

    [Fact]
    public void Page_NewIdWithOnlyName_Warned()
    {
        var patched = Head + "  $(\"#custId\").focus();\n}\n</script>\n";
        var (_, row) = Run(Ctx(PagePath, Original, patched, PageState(Original)));
        Assert.Equal(AspCheckStatus.Warned, row!.Status);
        Assert.Equal("#custId(줄 4) — id 없음, name=\"custId\"는 있음(줄 1)", row.Detail);
    }

    [Fact]
    public void Page_NoNewBroken_Passed()
    {
        var patched = Head + "  f.custId.select();\n}\n</script>\n";
        var (_, row) = Run(Ctx(PagePath, Original, patched, PageState(Original)));
        Assert.Equal(AspCheckStatus.Passed, row!.Status);
    }

    [Fact]
    public void Page_AlwaysPasses()
    {
        var patched = Head + "  $(\"#nothing\").focus();\n}\n</script>\n";
        var (result, row) = Run(Ctx(PagePath, Original, patched, PageState(Original)));
        Assert.Equal(StepResult.Pass, result);
        Assert.Equal(AspCheckStatus.Warned, row!.Status);
    }

    [Fact]
    public void Page_SiteIndexUnreadable_SkippedNotThrown()
    {
        var page = AspPage.Parse(Original);
        var views = Enumerable.Range(0, page.ClientBlocks.Count).Select(i => PageBlockView.Build(page, Original, i)).ToList();
        var state = new WebRunState(EditKind.Js, WebTargetUnit.Lines, new TextSpanInfo(4, 4), Original, "CP949", true, Root,
            new Lazy<WebSiteIndexResult>(() => throw new IOException("locked")))
        {
            Page = new WebPageState(page, 0, views[0], views),
        };
        var (result, row) = Run(Ctx(PagePath, Original, Original, state));
        Assert.Equal(StepResult.Pass, result);
        Assert.Equal(AspCheckStatus.Skipped, row!.Status);
        Assert.Equal("사이트 색인을 읽지 못함", row.Detail);
    }
}
