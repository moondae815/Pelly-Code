using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Fsm;

public sealed class AspLocatePhaseTests
{
    private const string Root = "/w";
    private static readonly AspTextCodec Codec = new("cp949");

    private readonly MockFileSystem _fs = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly AspOptions _options = new();

    private const string TwoProcs =
        "<%\n' amount\nFunction GetAmount(id)\n  GetAmount = 0\nEnd Function\n\nSub Show(x)\n  Response.Write x\nEnd Sub\n%>\n";

    private async Task<(PhaseOutcome Outcome, RunContext Ctx)> Locate(
        string text, string file = "page.asp", string? proc = null, int? line = null, TextSpanInfo? lines = null,
        params (string Path, string Text)[] others)
    {
        var path = Root + "/" + file;
        _fs.AddFile(path, new MockFileData(Encoding.UTF8.GetBytes(text)));
        foreach (var (p, t) in others) _fs.AddFile(p, new MockFileData(Encoding.UTF8.GetBytes(t)));

        var request = new RunRequest(path, null, proc, line, "do it", true, false, null, Kind: EditKind.Asp, Lines: lines);
        var ctx = new RunContext("r1", request, new PellyOptions { Asp = _options })
        {
            FilePath = path,
            Workspace = new Workspace(Root, Root + "/.pelly"),
        };
        ctx.Original = Codec.Decode(path, _fs.File.ReadAllBytes(path));
        ctx.Current = ctx.Original;
        var outcome = await new AspLocatePhase(_fs, _ui, _options, Codec).ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        return (outcome, ctx);
    }

    [Fact]
    public async Task Proc_ByName_CaseInsensitive()
    {
        var (outcome, ctx) = await Locate(TwoProcs, proc: "getamount");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(new EditTarget(EditKind.Asp, Root + "/page.asp", "Function GetAmount", new TextSpanInfo(2, 5), null), ctx.Target);
        Assert.Equal("Asp", ctx.ProjectStyle);
        var state = Assert.IsType<AspRunState>(ctx.Asp);
        Assert.Equal(AspTargetUnit.Procedure, state.Unit);
        Assert.Equal("GetAmount", state.Procedure!.Name);
        Assert.Equal(new TextSpanInfo(2, 5), state.OriginalSpan);
        Assert.Equal(state.OriginalSpan, state.CurrentSpan);
        Assert.Equal("CP949", state.EncodingName); // pure ASCII: the asp.encoding default
        Assert.Equal(Root, state.SiteRoot);
    }

    [Fact]
    public async Task Proc_ClassMethod()
    {
        const string text = "<%\nClass Cart\n  Public Function Total()\n    Total = 0\n  End Function\nEnd Class\nFunction Total()\nEnd Function\n%>\n";

        var (outcome, ctx) = await Locate(text, proc: "cart.total");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("Function Cart.Total", ctx.Target!.SymbolDisplay);
        Assert.Equal(new TextSpanInfo(3, 5), ctx.Target.MemberSpan);
        Assert.Equal("Cart", ctx.Asp!.Procedure!.ClassName);
    }

    [Fact]
    public async Task Line_InsideFunction_Procedure()
    {
        var (outcome, ctx) = await Locate(TwoProcs, line: 8);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("Sub Show", ctx.Target!.SymbolDisplay);
        Assert.Equal(new TextSpanInfo(7, 9), ctx.Target.MemberSpan);
        Assert.Equal(AspTargetUnit.Procedure, ctx.Asp!.Unit);
    }

    [Fact]
    public async Task Line_PageCode_AsksForLines()
    {
        var (outcome, ctx) = await Locate("<%\nDim a\na = 1\n%>\n", line: 3);

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Contains("3번 줄은 Function·Sub 안이 아닙니다. --lines A-B로 범위를 주십시오", ctx.Messages);
        Assert.Equal(FailureKinds.TargetNotFound, ctx.FailureKind);
    }

    [Fact]
    public async Task Lines_Range_Display()
    {
        var (outcome, ctx) = await Locate("<html>\n<%\nDim a\na = 1\n%>\n</html>\n", lines: new TextSpanInfo(2, 5));

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("줄 2-5", ctx.Target!.SymbolDisplay);
        Assert.Equal(new TextSpanInfo(2, 5), ctx.Target.MemberSpan);
        Assert.Equal(AspTargetUnit.Lines, ctx.Asp!.Unit);
        Assert.Null(ctx.Asp.Procedure);
    }

    [Fact]
    public async Task Lines_OutOfFile_NotFound()
    {
        var (outcome, ctx) = await Locate("<%\nDim a\n%>\n", lines: new TextSpanInfo(2, 9));

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Contains(ctx.Messages, m => m.Contains("줄 2-9"));
        Assert.Null(ctx.Target);
    }

    [Fact]
    public async Task NoOption_OneProcedure_Auto()
    {
        var (outcome, ctx) = await Locate("<%\nSub Only()\nEnd Sub\n%>\n");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("Sub Only", ctx.Target!.SymbolDisplay);
        Assert.Empty(_ui.SelectLabels);
    }

    [Fact]
    public async Task NoOption_Several_NonInteractive_Ambiguous()
    {
        var (outcome, ctx) = await Locate(TwoProcs);

        Assert.Equal(PhaseOutcome.Ambiguous, outcome);
        Assert.Equal(FailureKinds.TargetAmbiguous, ctx.FailureKind);
        Assert.Contains(ctx.Messages, m => m.Contains("--proc") && m.Contains("GetAmount") && m.Contains("Show"));
    }

    [Fact]
    public async Task NoOption_Several_Interactive_Selects()
    {
        _ui.IsInteractive = true;
        _ui.SelectIndex = _ => 1;

        var (outcome, ctx) = await Locate(TwoProcs);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("Sub Show", ctx.Target!.SymbolDisplay);
        Assert.Equal(["Function GetAmount (줄 2-5)", "Sub Show (줄 7-9)"], Assert.Single(_ui.SelectLabels));
    }

    [Fact]
    public async Task NoOption_None_AsksForLines()
    {
        var (outcome, ctx) = await Locate("<%\nDim a\n%>\n");

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Contains(ctx.Messages, m => m.Contains("--lines A-B로 범위를 주십시오"));
    }

    [Fact]
    public async Task TwoTargetOptions_EnvError()
    {
        var (outcome, ctx) = await Locate(TwoProcs, proc: "Show", lines: new TextSpanInfo(1, 2));

        Assert.Equal(PhaseOutcome.EnvError, outcome);
        Assert.Null(ctx.Target);
    }

    [Fact]
    public async Task GlobalAsa_Refused()
    {
        _options.Extensions = [".asp", ".inc", ".asa"];

        var (outcome, ctx) = await Locate("<script runat=\"server\" language=\"VBScript\">\nSub Application_OnStart\nEnd Sub\n</script>\n", file: "Global.asa");

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal(FailureKinds.AspUnsupportedFile, ctx.FailureKind);
    }

    [Fact]
    public async Task UnknownExtension_Refused()
    {
        var (outcome, ctx) = await Locate("<%\nSub A()\nEnd Sub\n%>\n", file: "page.html");

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal(FailureKinds.AspUnsupportedFile, ctx.FailureKind);
        Assert.Contains(ctx.Messages, m => m.Contains("asp.extensions"));
    }

    [Fact]
    public async Task UpperCaseExtension_Accepted()
    {
        var (outcome, _) = await Locate("<%\nSub A()\nEnd Sub\n%>\n", file: "PAGE.ASP");

        Assert.Equal(PhaseOutcome.Ok, outcome);
    }

    [Fact]
    public async Task JScriptPage_Refused()
    {
        var (outcome, ctx) = await Locate("<%@ Language=JScript %>\n<%\nvar a = 1;\n%>\n", lines: new TextSpanInfo(2, 4));

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal(FailureKinds.AspUnsupportedLanguage, ctx.FailureKind);
    }

    [Fact]
    public async Task ServerJScriptInRange_Refused_OutsideRange_Accepted()
    {
        const string text = "<%\nDim a\n%>\n<script runat=\"server\" language=\"JScript\">\nvar b = 1;\n</script>\n";

        var (inside, ctx) = await Locate(text, lines: new TextSpanInfo(2, 5));
        Assert.Equal(PhaseOutcome.NotFound, inside);
        Assert.Equal(FailureKinds.AspUnsupportedLanguage, ctx.FailureKind);

        var (outside, _) = await Locate(text, lines: new TextSpanInfo(1, 3));
        Assert.Equal(PhaseOutcome.Ok, outside);
    }

    [Fact]
    public async Task UnclosedBlock_InRange_NotFound()
    {
        var (outcome, ctx) = await Locate("<html>\n<%\nDim a\na = 1\n", lines: new TextSpanInfo(3, 4));

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Contains(ctx.Messages, m => m.Contains("해석할 수 없습니다"));
    }

    [Fact]
    public async Task Regions_ExcludeIncludeAndDirectiveLines()
    {
        const string text = "<%@ Language=VBScript %>\n<%\nDim a\n%>\n<!-- #include file=\"lib.inc\" -->\n<%\na = 1\n%>\n";

        var (outcome, ctx) = await Locate(text, lines: new TextSpanInfo(1, 8), others: ("/w/lib.inc", "<% x = 1 %>"));

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(
            [new EditableRegion(new TextSpanInfo(2, 4), "target"), new EditableRegion(new TextSpanInfo(6, 8), "target")],
            ctx.Asp!.EditableRegions());
        Assert.Equal(["/w/lib.inc"], ctx.Asp.Includes.Files.Select(f => f.Path));
    }

    [Fact]
    public async Task MissingVirtualInclude_MessageMentionsSiteRoot()
    {
        _options.SiteRoot = "site";
        const string text = "<!-- #include virtual=\"/inc/missing.inc\" -->\n<%\nSub A()\nEnd Sub\n%>\n";

        var (outcome, ctx) = await Locate(text);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("/w/site", ctx.Asp!.SiteRoot);
        Assert.Contains("include를 찾지 못했습니다: inc/missing.inc (asp.siteRoot 확인)", ctx.Messages);
    }

    [Fact]
    public async Task Cp949File_EncodingName()
    {
        var path = Root + "/k.asp";
        _fs.AddFile(path, new MockFileData(CodePagesBytes("<%\n' 한글\nSub A()\nEnd Sub\n%>\n")));
        var request = new RunRequest(path, null, null, null, "do it", true, false, null, Kind: EditKind.Asp);
        var ctx = new RunContext("r1", request, new PellyOptions { Asp = _options })
        {
            FilePath = path,
            Workspace = new Workspace(Root, Root + "/.pelly"),
        };
        ctx.Original = Codec.Decode(path, _fs.File.ReadAllBytes(path));
        ctx.Current = ctx.Original;

        var outcome = await new AspLocatePhase(_fs, _ui, _options, Codec).ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("CP949", ctx.Asp!.EncodingName);
    }

    private static byte[] CodePagesBytes(string text)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(949).GetBytes(text);
    }

    [Fact]
    public void Record_KeepsLastStatus()
    {
        var state = new AspRunState(AspPage.Parse("<%\n%>\n"), AspTargetUnit.Lines, new TextSpanInfo(1, 2), null,
            new AspIncludeSet([], [], false), "UTF-8", Root);

        state.Record("Compile", AspCheckStatus.Skipped, "cscript 없음");
        state.Record("Compile", AspCheckStatus.Passed);

        Assert.Equal(new AspCheck("Compile", AspCheckStatus.Passed, null), state.Checks["Compile"]);
    }
}
