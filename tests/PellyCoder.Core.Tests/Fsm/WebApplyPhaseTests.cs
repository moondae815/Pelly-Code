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
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Web;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>JS/CSS APPLY: patch, encoding, syntax, level, shape and safety are judged in memory before anything is written.</summary>
public sealed class WebApplyPhaseTests
{
    private const string Root = "/w";
    private const string JsPath = Root + "/js/order.js";
    private const string CssPath = Root + "/css/site.css";
    private static readonly WebTextCodec Codec = new("cp949");

    private readonly MockFileSystem _fs = new();
    private readonly PellyOptions _options = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly FileGuard _guard;

    public WebApplyPhaseTests() => _guard = new FileGuard(_fs, TimeProvider.System, Codec);

    private const string OrderJs =
        "function fmtPrice(n) {\n" +      // 1
        "  return n + \" won\";\n" +      // 2
        "}\n" +                           // 3
        "\n" +                            // 4
        "function saveForm(id) {\n" +     // 5
        "  var v = fmtPrice(id);\n" +     // 6
        "  return true;\n" +              // 7
        "}\n";                            // 8

    private const string SiteCss =
        ".box {\n" +          // 1
        "  color: red;\n" +   // 2
        "}\n" +               // 3
        ".card {\n" +         // 4
        "  margin: 0;\n" +    // 5
        "}\n";                // 6

    private WebApplyPhase Phase() =>
        new(_guard, new FeedbackBuilder(QwenTokenCounter.Shared, new BudgetOptions().Feedback), _ui, Codec);

    private RunContext NewContext(string path, RunRequest request)
    {
        var ctx = new RunContext("r1", request, _options)
        {
            FilePath = path,
            Workspace = new Workspace(Root, Root + "/.pelly"),
        };
        ctx.Original = _guard.Read(path);
        ctx.Current = ctx.Original;
        return ctx;
    }

    private async Task<RunContext> SlicedJs(string text, string function = "saveForm", bool dryRun = false)
    {
        _fs.AddFile(JsPath, new MockFileData(Encoding.UTF8.GetBytes(text)));
        var request = new RunRequest(JsPath, null, function, null, "do it", true, dryRun, null, Kind: EditKind.Js);
        var ctx = NewContext(JsPath, request);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(PhaseOutcome.Ok, await new JsLocatePhase(_fs, new ScriptedUserInteraction(), _options, Codec).ExecuteAsync(ctx, ct));
        Assert.Equal(PhaseOutcome.Ok, await new WebSlicePhase(_guard, QwenTokenCounter.Shared, new BudgetOptions()).ExecuteAsync(ctx, ct));
        return ctx;
    }

    private async Task<RunContext> SlicedCss(string text, int line = 5, bool dryRun = false)
    {
        _fs.AddFile(CssPath, new MockFileData(Encoding.UTF8.GetBytes(text)));
        var request = new RunRequest(CssPath, null, null, line, "do it", true, dryRun, null, Kind: EditKind.Css);
        var ctx = NewContext(CssPath, request);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(PhaseOutcome.Ok, await new CssLocatePhase(_fs, new ScriptedUserInteraction(), _options, Codec).ExecuteAsync(ctx, ct));
        Assert.Equal(PhaseOutcome.Ok, await new WebSlicePhase(_guard, QwenTokenCounter.Shared, new BudgetOptions()).ExecuteAsync(ctx, ct));
        return ctx;
    }

    private Task<PhaseOutcome> Apply(RunContext ctx, string search, string replace)
    {
        ctx.Blocks = [new PatchBlock(search, replace)];
        return Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);
    }

    private string Disk(string path) => Encoding.UTF8.GetString(_fs.File.ReadAllBytes(path));

    [Fact]
    public async Task Apply_OutsideRegion_MatchFailed()
    {
        var ctx = await SlicedJs(OrderJs);

        var outcome = await Apply(ctx, "  return n + \" won\";", "  return n;");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(PatchFailureKinds.OutsideRegion, ctx.FailureKind);
        Assert.Equal(OrderJs, Disk(JsPath));
    }

    [Fact]
    public async Task Apply_Unencodable_WebEncoding()
    {
        var ctx = await SlicedJs(OrderJs);

        var outcome = await Apply(ctx, "  return true;", "  return \"😀\";");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(FailureKinds.WebEncoding, ctx.FailureKind);
        Assert.Contains("😀", ctx.Feedback!.Text);
        Assert.Contains("line 7", ctx.Feedback.Text);
        Assert.Equal(AspCheckStatus.Failed, ctx.Web!.Checks["Encoding"].Status);
        Assert.Equal(OrderJs, Disk(JsPath));
    }

    [Fact]
    public async Task Apply_JsSyntaxError_Feedback()
    {
        var ctx = await SlicedJs(OrderJs);

        var outcome = await Apply(ctx, "  return true;", "  return (true;");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(FailureKinds.JsSyntaxErrors, ctx.FailureKind);
        Assert.StartsWith("JS SYNTAX CHECK FAILED", ctx.Feedback!.Text);
        Assert.Equal(AspCheckStatus.Failed, ctx.Web!.Checks["Syntax"].Status);
        Assert.Equal(OrderJs, Disk(JsPath));
    }

    [Fact]
    public async Task Apply_ArrowInEs5_LevelFeedback_CountsRejection()
    {
        var ctx = await SlicedJs(OrderJs);

        var outcome = await Apply(ctx, "  return true;", "  var f = function () {};\n  var g = (x) => x;\n  return true;");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(FailureKinds.JsLevelExceeded, ctx.FailureKind);
        Assert.StartsWith("LANGUAGE LEVEL EXCEEDED", ctx.Feedback!.Text);
        Assert.Contains("This file is written in ES5.", ctx.Feedback.Text);
        Assert.Contains("(line 8): ", ctx.Feedback.Text);
        Assert.Contains("arrow functions", ctx.Feedback.Text);
        Assert.DoesNotContain("화살표", ctx.Feedback.Text);
        Assert.Contains("화살표", ctx.Web!.Checks["Level"].Detail);
        Assert.Contains("Rewrite it with ES5 syntax only.", ctx.Feedback.Text);
        Assert.Contains(FeedbackBuilder.KeepRequest, ctx.Feedback.Text);
        Assert.Equal(1, ctx.Web!.LevelRejections);
        Assert.Equal(AspCheckStatus.Failed, ctx.Web.Checks["Level"].Status);
        Assert.Equal(OrderJs, Disk(JsPath));
    }

    [Fact]
    public async Task Apply_FunctionRenamed_JsTargetChanged()
    {
        var ctx = await SlicedJs(OrderJs);

        var outcome = await Apply(ctx, "function saveForm(id) {", "function saveFormX(id) {");

        Assert.Equal(PhaseOutcome.ShapeRejected, outcome);
        Assert.Equal(FailureKinds.JsTargetChanged, ctx.FailureKind);
        Assert.StartsWith("SHAPE REJECTED: ", ctx.Feedback!.Text);
        Assert.Equal(OrderJs, Disk(JsPath));
    }

    [Fact]
    public async Task Apply_Eval_SafetyRejected()
    {
        var ctx = await SlicedJs(OrderJs);

        var outcome = await Apply(ctx, "  return true;", "  eval(v);\n  return true;");

        Assert.Equal(PhaseOutcome.SafetyRejected, outcome);
        Assert.Equal(FailureKinds.JsSafetyRejected, ctx.FailureKind);
        Assert.StartsWith("WEB SAFETY REJECTED (1 new finding)", ctx.Feedback!.Text);
        Assert.Contains("line 7: ", ctx.Feedback.Text);
        Assert.Contains("eval()", ctx.Feedback.Text);
        Assert.Equal(AspCheckStatus.Failed, ctx.Web!.Checks["Safety"].Status);
        Assert.Equal(OrderJs, Disk(JsPath));
    }

    [Fact]
    public async Task Apply_Fallback_SyntaxSkippedRecorded()
    {
        var ctx = await SlicedJs(OrderJs + "var broken = 1 @ 2;\n");
        Assert.True(ctx.Web!.Fallback);

        var outcome = await Apply(ctx, "  return true;", "  return false;");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(AspCheckStatus.Skipped, ctx.Web.Checks["Syntax"].Status);
        Assert.Equal(ctx.Web.FallbackReason, ctx.Web.Checks["Syntax"].Detail);
        Assert.Equal(AspCheckStatus.Skipped, ctx.Web.Checks["Level"].Status);
        Assert.Contains("return false;", Disk(JsPath));
    }

    [Fact]
    public async Task Apply_CssUnclosed_StructureFeedback()
    {
        var ctx = await SlicedCss(SiteCss);

        var outcome = await Apply(ctx, "  margin: 0;", "  margin: 0;\n  .x {");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(FailureKinds.CssStructureErrors, ctx.FailureKind);
        Assert.StartsWith("CSS STRUCTURE CHECK FAILED", ctx.Feedback!.Text);
        Assert.Equal(SiteCss, Disk(CssPath));
    }

    [Fact]
    public async Task Apply_CssSelectorChanged_TargetChanged()
    {
        var ctx = await SlicedCss(SiteCss);

        var outcome = await Apply(ctx, ".card {", ".cardx {");

        Assert.Equal(PhaseOutcome.ShapeRejected, outcome);
        Assert.Equal(FailureKinds.CssTargetChanged, ctx.FailureKind);
        Assert.StartsWith("SHAPE REJECTED: ", ctx.Feedback!.Text);
        Assert.Equal(SiteCss, Disk(CssPath));
    }

    [Fact]
    public async Task Apply_CssExpression_SafetyRejected()
    {
        var ctx = await SlicedCss(SiteCss);

        var outcome = await Apply(ctx, "  margin: 0;", "  width: expression(document.body.clientWidth);");

        Assert.Equal(PhaseOutcome.SafetyRejected, outcome);
        Assert.Equal(FailureKinds.CssSafetyRejected, ctx.FailureKind);
        Assert.StartsWith("WEB SAFETY REJECTED", ctx.Feedback!.Text);
        Assert.Contains("expression", ctx.Feedback.Text);
        Assert.Equal(SiteCss, Disk(CssPath));
    }

    [Fact]
    public async Task Apply_Unchanged_NoChange()
    {
        var ctx = await SlicedJs(OrderJs);

        var outcome = await Apply(ctx, "  return true;", "  return true;");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(FailureKinds.NoChange, ctx.FailureKind);
        Assert.Equal(OrderJs, Disk(JsPath));
    }

    [Fact]
    public async Task Apply_DryRun_NotWritten()
    {
        var ctx = await SlicedJs(OrderJs, dryRun: true);

        var outcome = await Apply(ctx, "  return true;", "  return false;");

        Assert.Equal(PhaseOutcome.DryRunCompleted, outcome);
        Assert.Equal(OrderJs, Disk(JsPath));
    }

    [Fact]
    public async Task Apply_Written_SpanShifted()
    {
        var ctx = await SlicedJs(OrderJs);
        var end = ctx.Web!.CurrentSpan.EndLine;

        var outcome = await Apply(ctx, "  return true;", "  var w = 1;\n  return true;");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(end + 1, ctx.Web.CurrentSpan.EndLine);
        Assert.Contains("var w = 1;", Disk(JsPath));
        Assert.Equal(AspCheckStatus.Passed, ctx.Web.Checks["Safety"].Status);
    }

    private async Task<RunContext> SlicedJsSignature(string text, string function = "saveForm")
    {
        _fs.AddFile(JsPath, new MockFileData(Encoding.UTF8.GetBytes(text)));
        var request = new RunRequest(JsPath, null, function, null, "do it", true, false, null, Kind: EditKind.Js, AllowSignatureChange: true);
        var ctx = NewContext(JsPath, request);
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(PhaseOutcome.Ok, await new JsLocatePhase(_fs, new ScriptedUserInteraction(), _options, Codec).ExecuteAsync(ctx, ct));
        Assert.True(ctx.Web!.AllowSignatureChange);
        Assert.Equal(PhaseOutcome.Ok, await new WebSlicePhase(_guard, QwenTokenCounter.Shared, new BudgetOptions()).ExecuteAsync(ctx, ct));
        return ctx;
    }

    [Fact]
    public async Task Apply_RenameAllowed_RecordsSignatureChange()
    {
        var ctx = await SlicedJsSignature(OrderJs);
        Assert.Equal(PhaseOutcome.Ok, await Apply(ctx, "function saveForm(id) {", "function saveAll(id, all) {"));
        Assert.Equal("saveForm(id)", ctx.Web!.SignatureChange!.Value.Before.Display);
        Assert.Equal("saveAll(id, all)", ctx.Web.SignatureChange!.Value.After.Display);
        Assert.Contains("function saveAll(id, all) {", Disk(JsPath));
    }

    [Fact]
    public async Task Apply_RenameNotAllowed_Refused()
    {
        var ctx = await SlicedJs(OrderJs);
        Assert.Equal(PhaseOutcome.ShapeRejected, await Apply(ctx, "function saveForm(id) {", "function saveAll(id) {"));
    }

    [Fact]
    public async Task Slice_Retry_FindsRenamedTarget()
    {
        var ctx = await SlicedJsSignature(OrderJs);
        Assert.Equal(PhaseOutcome.Ok, await Apply(ctx, "function saveForm(id) {", "function saveAll(id) {"));
        var again = await new WebSlicePhase(_guard, QwenTokenCounter.Shared, new BudgetOptions()).ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal(PhaseOutcome.Ok, again);
        Assert.Equal(new TextSpanInfo(5, 8), ctx.Web!.CurrentSpan);
    }
}
