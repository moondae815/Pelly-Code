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
using PellyCoder.Core.Verification;
using PellyCoder.Core.Web;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Fsm.Phases;

/// <summary>JS/CSS APPLY on a page: block boundaries and server code stay, the block is judged on its view.</summary>
public sealed class WebApplyPagePhaseTests
{
    private const string Root = "/w";
    private const string PagePath = Root + "/p/list.asp";
    private static readonly AspTextCodec Codec = new("cp949");

    private readonly MockFileSystem _fs = new();
    private readonly PellyOptions _options = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly FileGuard _guard;

    public WebApplyPagePhaseTests() => _guard = new FileGuard(_fs, TimeProvider.System, Codec);

    // 1 <html>, 2 <script>, 3 fnA, 4 return, 5 }, 6 fnB, 7 return, 8 }, 9 </script>
    private const string JsPage =
        "<html>\n<script>\nfunction fnA() {\n  return 1;\n}\nfunction fnB(a) {\n  return a;\n}\n</script>\n";

    private RunContext NewContext(byte[] bytes, EditKind kind, string? function, int? line, bool dryRun = false)
    {
        _fs.AddFile(PagePath, new MockFileData(bytes));
        var request = new RunRequest(PagePath, null, function, line, "do it", true, dryRun, null, Kind: kind);
        var ctx = new RunContext("r1", request, _options)
        {
            FilePath = PagePath,
            Workspace = new Workspace(Root, Root + "/.pelly"),
        };
        ctx.Original = _guard.Read(PagePath);
        ctx.Current = ctx.Original;
        return ctx;
    }

    private async Task<RunContext> Sliced(string text, string? function = "fnA", EditKind kind = EditKind.Js, int? line = null,
        bool dryRun = false) =>
        await Sliced(Encoding.UTF8.GetBytes(text), function, kind, line, dryRun);

    private async Task<RunContext> Sliced(byte[] bytes, string? function = "fnA", EditKind kind = EditKind.Js, int? line = null,
        bool dryRun = false)
    {
        var ctx = NewContext(bytes, kind, function, line, dryRun);
        var ct = TestContext.Current.CancellationToken;
        IPhase locate = kind == EditKind.Css
            ? new CssLocatePhase(_fs, new ScriptedUserInteraction(), _options, Codec)
            : new JsLocatePhase(_fs, new ScriptedUserInteraction(), _options, Codec);
        Assert.Equal(PhaseOutcome.Ok, await locate.ExecuteAsync(ctx, ct));
        Assert.Equal(PhaseOutcome.Ok, await new WebSlicePhase(_guard, QwenTokenCounter.Shared, new BudgetOptions()).ExecuteAsync(ctx, ct));
        return ctx;
    }

    private Task<PhaseOutcome> Apply(RunContext ctx, string search, string replace)
    {
        ctx.Blocks = [new PatchBlock(search, replace)];
        var phase = new WebApplyPhase(_guard, new FeedbackBuilder(QwenTokenCounter.Shared, new BudgetOptions().Feedback), _ui, Codec);
        return phase.ExecuteAsync(ctx, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Page_edit_keeps_bytes_outside_the_block_cp949_crlf()
    {
        var text = "<html>\r\n<p>한글</p>\r\n<script>\r\nfunction fnA() {\r\n  return 1;\r\n}\r\n</script>\r\n<p>끝</p>\r\n";
        var original = Codec.Encode(Codec.Decode(PagePath, Encoding.GetEncoding(949).GetBytes(text)), text.Replace("\r\n", "\n"));
        Assert.Equal(Encoding.GetEncoding(949).GetBytes(text), original);
        var ctx = await Sliced(original);

        var outcome = await Apply(ctx, "  return 1;", "  return 2;");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        var expected = Encoding.GetEncoding(949).GetBytes(text.Replace("return 1;", "return 2;"));
        Assert.Equal(expected, _fs.File.ReadAllBytes(PagePath));
    }

    [Fact]
    public async Task Closing_tag_in_string_is_WebBlockChanged_with_hint()
    {
        var ctx = await Sliced(JsPage);

        var outcome = await Apply(ctx, "  return 1;", "  return \"</script>\";");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(FailureKinds.WebBlockChanged, ctx.FailureKind);
        Assert.Contains("<\\/script>", ctx.Feedback!.Text);
        Assert.Equal(JsPage, Encoding.UTF8.GetString(_fs.File.ReadAllBytes(PagePath)));
    }

    private const string ServerPage =
        "<html>\n<script>\nfunction fnA() {\n  var x = <%= a %>;\n  return x;\n}\n</script>\n";

    [Fact]
    public async Task Changing_output_expression_is_WebServerCodeChanged()
    {
        var ctx = await Sliced(ServerPage);

        var outcome = await Apply(ctx, "  var x = <%= a %>;", "  var x = <%= b %>;");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(FailureKinds.WebServerCodeChanged, ctx.FailureKind);
        Assert.Contains("<% … %>", ctx.Feedback!.Text);
    }

    [Fact]
    public async Task Adding_statement_block_is_WebServerCodeChanged()
    {
        var ctx = await Sliced(ServerPage);

        var outcome = await Apply(ctx, "  return x;", "  <% If k Then %>\n  return x;\n  <% End If %>");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(FailureKinds.WebServerCodeChanged, ctx.FailureKind);
    }

    [Fact]
    public async Task Unchanged_server_code_passes_and_keeps_it()
    {
        var ctx = await Sliced(ServerPage);

        var outcome = await Apply(ctx, "  return x;", "  return x + 1;");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Contains("<%= a %>", Encoding.UTF8.GetString(_fs.File.ReadAllBytes(PagePath)));
    }

    [Fact]
    public async Task Arrow_function_in_es5_block_is_level_rejected()
    {
        var ctx = await Sliced(JsPage);

        var outcome = await Apply(ctx, "  return 1;", "  var g = (x) => x;\n  return 1;");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(FailureKinds.JsLevelExceeded, ctx.FailureKind);
        Assert.Contains("(line 4): ", ctx.Feedback!.Text);
    }

    [Fact]
    public async Task New_eval_in_block_is_safety_rejected()
    {
        var ctx = await Sliced(JsPage);

        var outcome = await Apply(ctx, "  return 1;", "  return eval(\"1\");");

        Assert.Equal(PhaseOutcome.SafetyRejected, outcome);
        Assert.Equal(FailureKinds.JsSafetyRejected, ctx.FailureKind);
    }

    [Fact]
    public async Task Original_error_in_other_block_does_not_block()
    {
        const string text = "<html>\n<script>\nfunction broken( {\n</script>\n<script>\nfunction fnB(a) {\n  return a;\n}\n</script>\n";
        var ctx = await Sliced(text, "fnB");

        var outcome = await Apply(ctx, "  return a;", "  return a + 1;");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Contains("return a + 1;", Encoding.UTF8.GetString(_fs.File.ReadAllBytes(PagePath)));
    }

    [Fact]
    public async Task Css_block_structure_error_rejected()
    {
        const string text = "<html>\n<style>\n.box {\n  color: red;\n}\n</style>\n";
        var ctx = await Sliced(text, null, EditKind.Css, line: 4);

        var outcome = await Apply(ctx, "  color: red;\n}", "  color: red;");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(FailureKinds.CssStructureErrors, ctx.FailureKind);
    }

    [Fact]
    public async Task Names_step_uses_view()
    {
        var ctx = await Sliced(ServerPage);
        Assert.Equal(PhaseOutcome.Ok, await Apply(ctx, "  return x;", "  return x + missingName;"));

        var result = await new JsNamesStep([]).VerifyAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(StepResult.Pass, result);
        var check = ctx.Web!.Checks["Names"];
        Assert.Equal(AspCheckStatus.Warned, check.Status);
        Assert.Contains("missingName", check.Detail);
        Assert.DoesNotContain("__asp", check.Detail);
    }

    [Fact]
    public async Task Review_shows_block_location()
    {
        var ctx = await Sliced(JsPage);
        Assert.Equal(PhaseOutcome.Ok, await Apply(ctx, "  return 1;", "  return 2;"));

        await new ReviewPhase(_ui).ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Contains("p/list.asp 2–9줄 <script> 블록", _ui.Warnings);
    }
}
