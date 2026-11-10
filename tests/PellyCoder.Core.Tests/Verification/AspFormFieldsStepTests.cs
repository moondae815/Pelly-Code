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

public sealed class AspFormFieldsStepTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\w" : "/w";
    private static readonly AspTextCodec Codec = new("cp949");

    private const string List =
        "<%@ Language=\"VBScript\" CodePage=949 %>\n" +        // 1
        "<form name=\"frm\" action=\"save.asp\" method=\"post\">\n" + // 2
        "<input name=\"custNo\">\n" +                           // 3
        "</form>\n";                                             // 4

    private const string Save =
        "<%@ Language=\"VBScript\" CodePage=949 %>\n" + // 1
        "<%\n" +                                         // 2
        "Dim v\n" +                                      // 3
        "v = Request.Form(\"custNo\")\n" +               // 4
        "%>\n";                                          // 5

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
        var result = await new AspFormFieldsStep(fs, options, Codec).VerifyAsync(ctx, ct);
        Assert.True(result.Passed);
        return (ctx, result);
    }

    [Fact]
    public async Task NoForms_NoRow()
    {
        const string page = "<%@ Language=\"VBScript\" CodePage=949 %>\n<p id=\"a\"></p>\n<p></p>\n";
        var (ctx, _) = await Edit("p.asp", [("p.asp", page)], 2, 2, "<p id=\"a\"></p>", "<p></p>");
        Assert.False(ctx.Asp!.Checks.ContainsKey(AspFormFieldsStep.Row));
    }

    [Fact]
    public async Task RenameFieldInForm_Warned()
    {
        var (ctx, _) = await Edit("list.asp", [("list.asp", List), ("save.asp", Save)], 3, 3,
            "<input name=\"custNo\">", "<input name=\"custId\">");
        var row = ctx.Asp!.Checks[AspFormFieldsStep.Row];
        Assert.Equal(AspCheckStatus.Warned, row.Status);
        Assert.Equal("save.asp가 읽는 custNo(줄 4)를 list.asp의 폼(줄 2)이 보내지 않음, 보내는 비슷한 이름: custId", row.Detail);
    }

    [Fact]
    public async Task ReaderAddsUnsentKey_Warned()
    {
        var (ctx, _) = await Edit("save.asp", [("list.asp", List), ("save.asp", Save)], 4, 4,
            "v = Request.Form(\"custNo\")", "v = Request.Form(\"custNo\") & Request.Form(\"writer\")");
        var row = ctx.Asp!.Checks[AspFormFieldsStep.Row];
        Assert.Equal(AspCheckStatus.Warned, row.Status);
        Assert.Equal("save.asp가 읽는 writer(줄 4)를 보내는 폼·ajax가 없음(보내는 곳 1곳)", row.Detail);
    }

    [Fact]
    public async Task UnrelatedEdit_Passed()
    {
        var page = List + "<p>x</p>\n";
        var (ctx, _) = await Edit("list.asp", [("list.asp", page), ("save.asp", Save)], 5, 5, "<p>x</p>", "<p>y</p>");
        Assert.Equal(AspCheckStatus.Passed, ctx.Asp!.Checks[AspFormFieldsStep.Row].Status);
    }

    [Fact]
    public async Task IndexUnreadable_Skipped()
    {
        var index = new Lazy<WebSiteIndexResult>(() => throw new UnauthorizedAccessException("denied"));
        var (ctx, _) = await Edit("list.asp", [("list.asp", List), ("save.asp", Save)], 3, 3,
            "<input name=\"custNo\">", "<input name=\"custId\">", index);
        var row = ctx.Asp!.Checks[AspFormFieldsStep.Row];
        Assert.Equal(AspCheckStatus.Skipped, row.Status);
        Assert.Equal("사이트 색인을 읽지 못함", row.Detail);
    }

    [Fact]
    public async Task SharesSiteIndexLazy()
    {
        var loads = 0;
        var index = new Lazy<WebSiteIndexResult>(() => { loads++; return new WebSiteIndexResult([], 0, false); });
        var (ctx, _) = await Edit("list.asp", [("list.asp", List)], 3, 3, "<input name=\"custNo\">", "<input name=\"custId\">", index);
        var ct = TestContext.Current.CancellationToken;
        await new AspFormFieldsStep(new MockFileSystem(), new PellyOptions(), Codec).VerifyAsync(ctx, ct);
        await new AspElementsStep(new MockFileSystem(), new PellyOptions(), Codec).VerifyAsync(ctx, ct);
        Assert.Equal(1, loads);
        Assert.Same(index, ctx.Asp!.SiteIndex);
    }
}
