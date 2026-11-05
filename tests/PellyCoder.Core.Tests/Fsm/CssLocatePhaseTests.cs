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

namespace PellyCoder.Core.Tests.Fsm;

public sealed class CssLocatePhaseTests
{
    private const string Root = "/w";
    private static readonly WebTextCodec Codec = new("cp949");

    private readonly MockFileSystem _fs = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly PellyOptions _options = new();

    private const string Sheet =
        ".a {\n  color: red;\n}\n@media print {\n  .a {\n    color: black;\n  }\n}\n.b {\n  margin: 0;\n}\n";

    private async Task<(PhaseOutcome Outcome, RunContext Ctx)> Locate(
        string text, string file = "css/admin.css", string? selector = null, int? line = null, TextSpanInfo? lines = null)
    {
        var path = Root + "/" + file;
        _fs.AddFile(path, new MockFileData(Encoding.UTF8.GetBytes(text)));

        var request = new RunRequest(path, null, null, line, "do it", true, false, null, Kind: EditKind.Css, Lines: lines,
            Selector: selector);
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
    public async Task Selector_Found()
    {
        var (outcome, ctx) = await Locate(Sheet, selector: ".b");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(new EditTarget(EditKind.Css, Root + "/css/admin.css", ".b", new TextSpanInfo(9, 11), null), ctx.Target);
        Assert.Equal("Web", ctx.ProjectStyle);
        var state = Assert.IsType<WebRunState>(ctx.Web);
        Assert.Equal(EditKind.Css, state.Kind);
        Assert.Equal(WebTargetUnit.Rule, state.Unit);
        Assert.Equal(".b", state.Rule!.Selector);
        Assert.NotNull(state.OriginalCss);
        Assert.Equal(Sheet, state.OriginalText);
        Assert.Equal(Root, state.SiteRoot);
    }

    [Fact]
    public async Task Selector_InMediaAndTop_NonInteractive_Ambiguous()
    {
        var (outcome, ctx) = await Locate(Sheet, selector: ".a");

        Assert.Equal(PhaseOutcome.Ambiguous, outcome);
        Assert.Equal(FailureKinds.TargetAmbiguous, ctx.FailureKind);
        Assert.Contains(ctx.Messages, m => m.Contains("@media print > .a"));
        Assert.Null(ctx.Target);

        _ui.IsInteractive = true;
        _ui.SelectIndex = _ => 1;
        var (picked, ctx2) = await Locate(Sheet, selector: ".a");

        Assert.Equal(PhaseOutcome.Ok, picked);
        Assert.Equal("@media print > .a", ctx2.Target!.SymbolDisplay);
        Assert.Equal(new TextSpanInfo(5, 7), ctx2.Target.MemberSpan);
    }

    [Fact]
    public async Task Line_InnermostRule()
    {
        var (outcome, ctx) = await Locate(Sheet, line: 6);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("@media print > .a", ctx.Target!.SymbolDisplay);
        Assert.Equal(new TextSpanInfo(5, 7), ctx.Target.MemberSpan);
        Assert.Equal(WebTargetUnit.Rule, ctx.Web!.Unit);
    }

    [Fact]
    public async Task Line_OnMediaHeader_AtRule()
    {
        var (outcome, ctx) = await Locate(Sheet, line: 4);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(new TextSpanInfo(4, 8), ctx.Target!.MemberSpan);
        Assert.True(ctx.Web!.Rule!.IsAtRule);
        Assert.StartsWith("@media", ctx.Target.SymbolDisplay);
    }

    [Fact]
    public async Task Lines_CutsRule_NotFound()
    {
        var (outcome, ctx) = await Locate(Sheet, lines: new TextSpanInfo(2, 5));

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Contains(ctx.Messages, m => m.StartsWith("줄 2-5가 규칙 중간을 자릅니다"));
        Assert.Null(ctx.Target);

        var (ok, ctx2) = await Locate(Sheet, lines: new TextSpanInfo(4, 11));
        Assert.Equal(PhaseOutcome.Ok, ok);
        Assert.Equal("줄 4-11", ctx2.Target!.SymbolDisplay);
        Assert.Equal(WebTargetUnit.Lines, ctx2.Web!.Unit);
    }

    [Fact]
    public async Task NotCss_Refused()
    {
        var (outcome, ctx) = await Locate(".a { color: red; }\n", file: "css/admin.scss");

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal(FailureKinds.WebUnsupportedFile, ctx.FailureKind);
        Assert.Contains(ctx.Messages, m => m.Contains("pelly css는 .css 파일만 고칩니다"));
    }

    [Fact]
    public async Task OriginalProblems_NotBlocking()
    {
        const string text = ".a {\n  color: red\n  margin: 0;\n}\n.b {\n  color: blue;\n";

        var (outcome, ctx) = await Locate(text, selector: ".a");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.NotEmpty(ctx.Web!.OriginalCss!.Problems);
        Assert.Equal(".a", ctx.Target!.SymbolDisplay);
    }
}
