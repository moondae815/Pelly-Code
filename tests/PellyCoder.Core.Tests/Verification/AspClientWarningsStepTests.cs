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
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Verification;

public sealed class AspClientWarningsStepTests
{
    private const string Root = "/w";
    private const string PagePath = Root + "/page.asp";
    private static readonly AspTextCodec Codec = new("cp949");

    private const string Page =
        "<%@ Language=\"VBScript\" CodePage=949 %>\n" + // 1
        "<script>\n" +                                  // 2
        "function a() { return 1; }\n" +                // 3
        "</script>\n" +                                 // 4
        "<p>x</p>\n";                                   // 5

    private async Task<(RunContext Ctx, AspClientWarningsStep Step)> Edited(string replacement, int fromLine = 3, int toLine = 3)
    {
        var fs = new MockFileSystem();
        var options = new PellyOptions();
        var guard = new FileGuard(fs, TimeProvider.System, Codec);
        fs.AddFile(PagePath, new MockFileData(Encoding.UTF8.GetBytes(Page)));
        var request = new RunRequest(PagePath, null, null, null, "do it", true, false, null, Kind: EditKind.Asp, Lines: new TextSpanInfo(fromLine, toLine));
        var ctx = new RunContext("r1", request, options) { FilePath = PagePath, Workspace = new Workspace(Root, Root + "/.pelly") };
        ctx.Original = guard.Read(PagePath);
        ctx.Current = ctx.Original;
        var ct = TestContext.Current.CancellationToken;
        await new AspLocatePhase(fs, new ScriptedUserInteraction(), options.Asp, Codec).ExecuteAsync(ctx, ct);
        await new AspSlicePhase(guard, QwenTokenCounter.Shared, new BudgetOptions(), null, options.Asp).ExecuteAsync(ctx, ct);
        ctx.Blocks = [new PatchBlock("function a() { return 1; }", replacement)];
        var apply = new AspApplyPhase(guard, new FeedbackBuilder(QwenTokenCounter.Shared, new BudgetOptions().Feedback),
            new ScriptedUserInteraction(), options.Asp, Codec, options.Web);
        Assert.Equal(PhaseOutcome.Ok, await apply.ExecuteAsync(ctx, ct));
        return (ctx, new AspClientWarningsStep(fs, options, Codec));
    }

    [Fact]
    public async Task Name_warning_row_for_unknown_function()
    {
        var (ctx, step) = await Edited("function a() { return missingHelper(1); }");

        var result = await step.VerifyAsync(ctx, TestContext.Current.CancellationToken);

        Assert.True(result.Passed);
        Assert.Equal(AspCheckStatus.Warned, ctx.Asp!.Checks["JsNames"].Status);
        Assert.Contains("missingHelper", ctx.Asp.Checks["JsNames"].Detail);
        Assert.Equal(AspCheckStatus.Passed, ctx.Asp.Checks["JsApi"].Status);
    }

    [Fact]
    public async Task Api_warning_row_for_es_api_above_level()
    {
        var (ctx, step) = await Edited("function a() { return [1].includes(1); }");

        await step.VerifyAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(AspCheckStatus.Passed, ctx.Asp!.Checks["JsNames"].Status);
        Assert.Equal(AspCheckStatus.Warned, ctx.Asp.Checks["JsApi"].Status);
    }
}
