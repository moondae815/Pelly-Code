using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Web;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Fsm.Phases;

public sealed class JsLocatePagePhaseTests
{
    private const string Root = "/w";
    private static readonly WebTextCodec Codec = new("cp949");

    private readonly MockFileSystem _fs = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly PellyOptions _options = new();

    // lines: 1 <html>, 2 <script>, 3 fnA, 4 }, 5 </script>, 6 <p>, 7 <script>, 8 fnB, 9 }, 10 </script>
    private const string TwoBlocks =
        "<html>\n<script>\nfunction fnA() {\n}\n</script>\n<p>x</p>\n<script>\nfunction fnB() {\n}\n</script>\n";

    private async Task<(PhaseOutcome Outcome, RunContext Ctx)> Locate(
        string text, string file = "p/list.asp", string? function = null, int? line = null, TextSpanInfo? lines = null)
    {
        var path = Root + "/" + file;
        _fs.AddFile(path, new MockFileData(Encoding.UTF8.GetBytes(text)));
        var request = new RunRequest(path, null, function, line, "do it", true, false, null, Kind: EditKind.Js, Lines: lines);
        var ctx = new RunContext("r1", request, _options)
        {
            FilePath = path,
            Workspace = new Workspace(Root, Root + "/.pelly"),
        };
        ctx.Original = Codec.Decode(path, _fs.File.ReadAllBytes(path));
        ctx.Current = ctx.Original;
        var outcome = await new JsLocatePhase(_fs, _ui, _options, Codec).ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        return (outcome, ctx);
    }

    [Fact]
    public async Task Function_found_across_blocks()
    {
        var (outcome, ctx) = await Locate(TwoBlocks, function: "fnB");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(1, ctx.Web!.Page!.BlockIndex);
        Assert.Equal(new TextSpanInfo(8, 9), ctx.Target!.MemberSpan);
        Assert.Equal("function fnB", ctx.Target.SymbolDisplay);
    }

    [Fact]
    public async Task Same_name_in_two_blocks_is_ambiguous_when_not_interactive()
    {
        const string text = "<script>\nfunction f() {\n}\n</script>\n<script>\nfunction f() {\n}\n</script>\n";

        var (outcome, ctx) = await Locate(text, function: "f");

        Assert.Equal(PhaseOutcome.Ambiguous, outcome);
        Assert.Equal(FailureKinds.TargetAmbiguous, ctx.FailureKind);
        Assert.Contains(ctx.Messages, m => m.Contains("줄 2-3") && m.Contains("줄 6-7"));
    }

    [Fact]
    public async Task Line_outside_block_is_WebOutsideBlock()
    {
        var (outcome, ctx) = await Locate(TwoBlocks, line: 6);

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal(FailureKinds.WebOutsideBlock, ctx.FailureKind);
        Assert.Contains("6번 줄은 <script> 블록 안이 아닙니다. pelly asp를 쓰십시오", ctx.Messages);
    }

    [Fact]
    public async Task Lines_crossing_block_is_WebOutsideBlock()
    {
        var (outcome, ctx) = await Locate(TwoBlocks, lines: new TextSpanInfo(3, 8));

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal(FailureKinds.WebOutsideBlock, ctx.FailureKind);
        Assert.Contains("줄 3-8 범위가 <script> 블록 밖에 걸칩니다. pelly asp --lines를 쓰십시오", ctx.Messages);
    }

    [Fact]
    public async Task Page_without_blocks_is_not_found()
    {
        var (outcome, ctx) = await Locate("<html>\n<p>x</p>\n", function: "fnA");

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal(FailureKinds.WebOutsideBlock, ctx.FailureKind);
        Assert.Contains("이 페이지에는 고칠 수 있는 <script> 블록이 없습니다", ctx.Messages);
    }

    [Fact]
    public async Task Unclosed_block_target_is_not_found()
    {
        var (outcome, ctx) = await Locate("<html>\n<script>\nfunction fnA() {\n}\n", line: 3);

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal(FailureKinds.WebOutsideBlock, ctx.FailureKind);
        Assert.Contains("줄 2의 <script>가 닫히지 않았습니다", ctx.Messages);
    }

    [Fact]
    public async Task Line_inside_block_picks_innermost_named_function()
    {
        const string text = "<script>\nfunction outer() {\n  function inner() {\n    return 1;\n  }\n}\n</script>\n";

        var (outcome, ctx) = await Locate(text, line: 4);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("function inner", ctx.Target!.SymbolDisplay);
        Assert.Equal(new TextSpanInfo(3, 5), ctx.Target.MemberSpan);
    }

    [Fact]
    public async Task Clean_block_is_parsed_normally_when_another_block_has_statements()
    {
        const string text = "<script>\n<% If x Then %>\nfunction fnA() {\n}\n<% End If %>\n</script>\n<script>\nfunction fnB() {\n}\n</script>\n";

        var (outcome, ctx) = await Locate(text, function: "fnB");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(1, ctx.Web!.Page!.BlockIndex);
        Assert.False(ctx.Web.Fallback);
        Assert.NotNull(ctx.Web.OriginalLevel);
    }

    [Fact]
    public async Task Statements_block_target_uses_fallback()
    {
        const string text = "<script>\n<% If x Then %>\nfunction fnA() {\n}\n<% End If %>\n</script>\n";

        var (outcome, ctx) = await Locate(text, function: "fnA");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.True(ctx.Web!.Fallback);
        Assert.Equal("서버 코드가 섞인 스크립트", ctx.Web.FallbackReason);
        Assert.Null(ctx.Web.OriginalLevel);
    }

    [Fact]
    public async Task Page_state_original_text_is_view()
    {
        var (_, ctx) = await Locate(TwoBlocks, function: "fnA");

        var page = ctx.Web!.Page!;
        Assert.Equal(page.OriginalView.Text, ctx.Web.OriginalText);
        Assert.Equal(page.OriginalView.Text, ctx.Web.ViewOf(ctx.Original.Text));
        Assert.Equal(TwoBlocks, ctx.Original.Text);
        Assert.Null(ctx.Web.ViewOf("<html>\n"));
    }

    [Fact]
    public async Task Js_file_runs_unchanged()
    {
        var (outcome, ctx) = await Locate("function fnA() {\n}\n", file: "js/a.js", function: "fnA");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Null(ctx.Web!.Page);
        Assert.Equal("input", ctx.Web.ViewOf("input"));
    }

    [Fact]
    public async Task Vendor_filter_not_applied_to_pages()
    {
        var (outcome, ctx) = await Locate(TwoBlocks, file: "lib/list.asp", function: "fnA");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Null(ctx.FailureKind);
    }
}
