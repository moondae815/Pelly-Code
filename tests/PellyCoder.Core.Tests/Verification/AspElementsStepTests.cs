using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Patchers;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Verification;
using PellyCoder.Core.Web;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Verification;

public sealed class AspElementsStepTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\w" : "/w";
    private static readonly AspTextCodec Codec = new("cp949");

    private const string Page =
        "<%@ Language=\"VBScript\" CodePage=949 %>\n" + // 1
        "<div id=\"a\"></div>\n" +                      // 2
        "<p class=\"x\"></p>\n" +                       // 3
        "<script>\n" +                                  // 4
        "function f() { $(\"#a\").hide(); }\n" +        // 5
        "</script>\n";                                  // 6

    private static async Task<(RunContext Ctx, StepResult Result)> Edit(string rel, (string Path, string Text)[] files,
        int from, int to, string search, string replace, Lazy<WebSiteIndexResult>? siteIndex = null)
    {
        var fs = new MockFileSystem();
        foreach (var (p, t) in files) fs.AddFile(Root + "/" + p, new MockFileData(Encoding.UTF8.GetBytes(t)));
        var path = Root + "/" + rel;
        var options = new PellyOptions();
        var guard = new FileGuard(fs, TimeProvider.System, Codec);
        var request = new RunRequest(path, null, null, null, "do it", true, false, null, Kind: EditKind.Asp, Lines: new TextSpanInfo(from, to));
        var ctx = new RunContext("r1", request, options) { FilePath = path, Workspace = new Workspace(Root, Root + "/.pelly") };
        ctx.Original = guard.Read(path);
        ctx.Current = ctx.Original;
        var ct = TestContext.Current.CancellationToken;
        await new AspLocatePhase(fs, new ScriptedUserInteraction(), options.Asp, Codec).ExecuteAsync(ctx, ct);
        await new AspSlicePhase(guard, QwenTokenCounter.Shared, new BudgetOptions(), null, options.Asp).ExecuteAsync(ctx, ct);
        ctx.Blocks = [new PatchBlock(search, replace)];
        var apply = new AspApplyPhase(guard, new FeedbackBuilder(QwenTokenCounter.Shared, new BudgetOptions().Feedback),
            new ScriptedUserInteraction(), options.Asp, Codec, options.Web);
        Assert.Equal(PhaseOutcome.Ok, await apply.ExecuteAsync(ctx, ct));
        ctx.Asp!.SiteIndex = siteIndex;
        var result = await new AspElementsStep(fs, options, Codec).VerifyAsync(ctx, ct);
        Assert.True(result.Passed);
        return (ctx, result);
    }

    [Fact]
    public async Task NoScriptBlocks_NoRow()
    {
        const string page = "<%@ Language=\"VBScript\" CodePage=949 %>\n<p id=\"a\"></p>\n<p></p>\n";
        var (ctx, _) = await Edit("p.asp", [("p.asp", page)], 2, 2, "<p id=\"a\"></p>", "<p></p>");
        Assert.False(ctx.Asp!.Checks.ContainsKey(AspElementsStep.Row));
    }

    [Fact]
    public async Task HtmlOnlyEdit_RemovesUsedId_Warned()
    {
        var (ctx, _) = await Edit("p.asp", [("p.asp", Page)], 2, 2, "<div id=\"a\"></div>", "<div></div>");
        var row = ctx.Asp!.Checks[AspElementsStep.Row];
        Assert.Equal(AspCheckStatus.Warned, row.Status);
        Assert.Equal("#a(줄 5) — id 없음", row.Detail);
    }

    [Fact]
    public async Task HtmlOnlyEdit_MovesId_NoWarning()
    {
        var (ctx, _) = await Edit("p.asp", [("p.asp", Page)], 2, 3,
            "<div id=\"a\"></div>\n<p class=\"x\"></p>", "<div></div>\n<p class=\"x\" id=\"a\"></p>");
        Assert.Equal(AspCheckStatus.Passed, ctx.Asp!.Checks[AspElementsStep.Row].Status);
    }

    [Fact]
    public async Task ScriptEdit_NewBrokenRef_Warned()
    {
        var (ctx, _) = await Edit("p.asp", [("p.asp", Page)], 5, 5,
            "function f() { $(\"#a\").hide(); }", "function f() { $(\"#a\").hide(); $(\"#b\").show(); }");
        var row = ctx.Asp!.Checks[AspElementsStep.Row];
        Assert.Equal(AspCheckStatus.Warned, row.Status);
        Assert.Equal("#b(줄 5) — id 없음", row.Detail);
    }

    [Fact]
    public async Task IncFile_FieldInIncluder_NoWarning()
    {
        const string inc = "<script>\nfunction g() { return 1; }\n</script>\n";
        const string parent = "<%@ Language=\"VBScript\" CodePage=949 %>\n<!--#include file=\"inc/form.inc\"-->\n<input id=\"q\">\n";
        var (ctx, _) = await Edit("inc/form.inc", [("inc/form.inc", inc), ("a.asp", parent)], 2, 2,
            "function g() { return 1; }", "function g() { $(\"#q\").focus(); return 1; }");
        Assert.Equal(AspCheckStatus.Passed, ctx.Asp!.Checks[AspElementsStep.Row].Status);
    }

    [Fact]
    public async Task SiteIndex_SharedAcrossStepsAndAttempts()
    {
        var loads = 0;
        var index = new Lazy<WebSiteIndexResult>(() => { loads++; return new WebSiteIndexResult([], 0, false); });
        var (ctx, _) = await Edit("p.asp", [("p.asp", Page)], 2, 2, "<div id=\"a\"></div>", "<div></div>", index);
        var ct = TestContext.Current.CancellationToken;
        await new AspElementsStep(new MockFileSystem(), new PellyOptions(), Codec).VerifyAsync(ctx, ct);
        Assert.Equal(1, loads);
    }

    [Fact]
    public async Task SiteIndexUnreadable_SkippedNotThrown()
    {
        var index = new Lazy<WebSiteIndexResult>(() => throw new UnauthorizedAccessException("denied"));
        var (ctx, _) = await Edit("p.asp", [("p.asp", Page)], 2, 2, "<div id=\"a\"></div>", "<div></div>", index);
        var row = ctx.Asp!.Checks[AspElementsStep.Row];
        Assert.Equal(AspCheckStatus.Skipped, row.Status);
        Assert.Equal("사이트 색인을 읽지 못함", row.Detail);
    }
}
