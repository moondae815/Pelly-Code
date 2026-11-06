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

public sealed class CssLocatePagePhaseTests
{
    private const string Root = "/w";
    private static readonly WebTextCodec Codec = new("cp949");

    private readonly MockFileSystem _fs = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly PellyOptions _options = new();

    // lines: 1 <html>, 2 <style>, 3 .a {, 4 }, 5 </style>, 6 <p>, 7 <style>, 8 .b {, 9 }, 10 </style>
    private const string TwoBlocks =
        "<html>\n<style>\n.a {\n}\n</style>\n<p>x</p>\n<style>\n.b {\n}\n</style>\n";

    private async Task<(PhaseOutcome Outcome, RunContext Ctx)> Locate(
        string text, string file = "p/list.asp", string? selector = null, int? line = null, TextSpanInfo? lines = null)
    {
        var path = Root + "/" + file;
        _fs.AddFile(path, new MockFileData(Encoding.UTF8.GetBytes(text)));
        var request = new RunRequest(path, null, null, line, "do it", true, false, null, Kind: EditKind.Css, Lines: lines, Selector: selector);
        var ctx = new RunContext("r1", request, _options)
        {
            FilePath = path,
            Workspace = new Workspace(Root, Root + "/.pelly"),
        };
        ctx.Original = Codec.Decode(path, _fs.File.ReadAllBytes(path));
        ctx.Current = ctx.Original;
        var outcome = await new CssLocatePhase(_fs, _ui, _options, Codec).ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        return (outcome, ctx);
    }

    [Fact]
    public async Task Selector_found_across_style_blocks()
    {
        var (outcome, ctx) = await Locate(TwoBlocks, selector: ".b");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(1, ctx.Web!.Page!.BlockIndex);
        Assert.Equal(new TextSpanInfo(8, 9), ctx.Target!.MemberSpan);
        Assert.Equal(".b", ctx.Target.SymbolDisplay);
        Assert.Equal(ctx.Web.Page.OriginalView.Text, ctx.Web.OriginalText);
    }

    [Fact]
    public async Task Same_selector_in_two_blocks_is_ambiguous()
    {
        const string text = "<style>\n.a {\n}\n</style>\n<style>\n.a {\n}\n</style>\n";

        var (outcome, ctx) = await Locate(text, selector: ".a");

        Assert.Equal(PhaseOutcome.Ambiguous, outcome);
        Assert.Equal(FailureKinds.TargetAmbiguous, ctx.FailureKind);
        Assert.Contains(ctx.Messages, m => m.Contains("줄 2-3") && m.Contains("줄 6-7"));
    }

    [Fact]
    public async Task Line_outside_style_is_WebOutsideBlock()
    {
        var (outcome, ctx) = await Locate(TwoBlocks, line: 6);

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal(FailureKinds.WebOutsideBlock, ctx.FailureKind);
        Assert.Contains("6번 줄은 <style> 블록 안이 아닙니다. pelly asp를 쓰십시오", ctx.Messages);
    }

    [Fact]
    public async Task Lines_crossing_style_is_WebOutsideBlock()
    {
        var (outcome, ctx) = await Locate(TwoBlocks, lines: new TextSpanInfo(3, 8));

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal(FailureKinds.WebOutsideBlock, ctx.FailureKind);
        Assert.Contains("줄 3-8 범위가 <style> 블록 밖에 걸칩니다. pelly asp --lines를 쓰십시오", ctx.Messages);
    }

    [Fact]
    public async Task Lines_inside_one_rule_accepted()
    {
        const string text = "<style>\n.a {\n  color: red;\n}\n</style>\n";

        var (outcome, ctx) = await Locate(text, lines: new TextSpanInfo(3, 3));

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(WebTargetUnit.Lines, ctx.Web!.Unit);
        Assert.Equal(new TextSpanInfo(3, 3), ctx.Target!.MemberSpan);
    }

    [Fact]
    public async Task Lines_cutting_a_rule_are_refused()
    {
        var (outcome, ctx) = await Locate("<style>\n.a {\n  x: y;\n}\n</style>\n", lines: new TextSpanInfo(2, 3));

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Contains(ctx.Messages, m => m.Contains("규칙 중간을 자릅니다"));
    }

    [Fact]
    public async Task Page_without_style_blocks_is_not_found()
    {
        var (outcome, ctx) = await Locate("<html>\n<p>x</p>\n", selector: ".a");

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal(FailureKinds.WebOutsideBlock, ctx.FailureKind);
        Assert.Contains("이 페이지에는 고칠 수 있는 <style> 블록이 없습니다", ctx.Messages);
    }

    [Fact]
    public async Task Unclosed_style_target_is_not_found()
    {
        var (outcome, ctx) = await Locate("<html>\n<style>\n.a {\n}\n", line: 3);

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal(FailureKinds.WebOutsideBlock, ctx.FailureKind);
        Assert.Contains("줄 2의 <style>가 닫히지 않았습니다", ctx.Messages);
    }

    [Fact]
    public async Task Line_inside_style_picks_innermost_rule()
    {
        var (outcome, ctx) = await Locate(TwoBlocks, line: 8);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(".b", ctx.Target!.SymbolDisplay);
        Assert.Equal(1, ctx.Web!.Page!.BlockIndex);
    }

    [Fact]
    public async Task Output_inside_style_is_placeholder()
    {
        const string text = "<style>\n.a {\n  color:<%= c %>;\n}\n</style>\n";

        var (outcome, ctx) = await Locate(text, selector: ".a");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Contains("__asp0__", ctx.Web!.OriginalText);
        Assert.Equal(new TextSpanInfo(2, 4), ctx.Target!.MemberSpan);
    }

    [Fact]
    public async Task Css_file_runs_unchanged()
    {
        var (outcome, ctx) = await Locate(".a {\n}\n", file: "css/a.css", selector: ".a");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Null(ctx.Web!.Page);
    }

    [Fact]
    public async Task Vendor_filter_not_applied_to_pages()
    {
        var (outcome, ctx) = await Locate(TwoBlocks, file: "lib/list.asp", selector: ".a");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Null(ctx.FailureKind);
    }
}
