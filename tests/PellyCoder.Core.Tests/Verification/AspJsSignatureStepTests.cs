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

public sealed class AspJsSignatureStepTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\w" : "/w";
    private static readonly AspTextCodec Codec = new("cp949");

    private const string Page =
        "<%@ Language=\"VBScript\" CodePage=949 %>\n" + // 1
        "<p class=\"x\"></p>\n" +                       // 2
        "<script>\n" +                                  // 3
        "function goPage(n) { return n; }\n" +          // 4
        "</script>\n";                                  // 5

    private const string Caller = "<a href=\"javascript:goPage(1)\">x</a>\n<input onclick=\"goPage(2)\">\n";

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
        var result = await new AspJsSignatureStep(fs, options, Codec).VerifyAsync(ctx, ct);
        Assert.True(result.Passed);
        return (ctx, result);
    }

    [Fact]
    public async Task NoScriptBlock_NoRow()
    {
        var (ctx, _) = await Edit("p.asp", [("p.asp", Page)], 2, 2, "<p class=\"x\"></p>", "<p class=\"y\"></p>");
        Assert.False(ctx.Asp!.Checks.ContainsKey(AspJsSignatureStep.Row));
    }

    [Fact]
    public async Task BodyOnly_Passed()
    {
        var (ctx, _) = await Edit("p.asp", [("p.asp", Page)], 4, 4, "function goPage(n) { return n; }", "function goPage(n) { return n + 1; }");
        Assert.Equal(AspCheckStatus.Passed, ctx.Asp!.Checks[AspJsSignatureStep.Row].Status);
    }

    [Fact]
    public async Task Renamed_Warned_WithCallerCount()
    {
        var (ctx, result) = await Edit("p.asp", [("p.asp", Page), ("c.asp", Caller)], 4, 4,
            "function goPage(n) { return n; }", "function goTo(n) { return n; }");
        var row = ctx.Asp!.Checks[AspJsSignatureStep.Row];
        Assert.Equal(AspCheckStatus.Warned, row.Status);
        Assert.Equal("함수 서명 변경: goPage(n) → goTo(n), 부르는 곳 2곳", row.Detail);
        Assert.True(result.Passed);
    }

    [Fact]
    public async Task IndexUnreadable_Skipped()
    {
        var index = new Lazy<WebSiteIndexResult>(() => throw new IOException("locked"));
        var (ctx, _) = await Edit("p.asp", [("p.asp", Page)], 4, 4, "function goPage(n) { return n; }", "function goTo(n) { return n; }", index);
        var row = ctx.Asp!.Checks[AspJsSignatureStep.Row];
        Assert.Equal(AspCheckStatus.Skipped, row.Status);
        Assert.Equal("사이트 색인을 읽지 못함", row.Detail);
    }

    [Fact]
    public async Task SharesSiteIndexLazy()
    {
        var loads = 0;
        var index = new Lazy<WebSiteIndexResult>(() => { loads++; return new WebSiteIndexResult([], 0, false); });
        var (ctx, _) = await Edit("p.asp", [("p.asp", Page)], 4, 4, "function goPage(n) { return n; }", "function goTo(n) { return n; }", index);
        await new AspElementsStep(new MockFileSystem(), new PellyOptions(), Codec).VerifyAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal(1, loads);
        Assert.Equal("함수 서명 변경: goPage(n) → goTo(n), 부르는 곳 0곳", ctx.Asp!.Checks[AspJsSignatureStep.Row].Detail);
    }
}
