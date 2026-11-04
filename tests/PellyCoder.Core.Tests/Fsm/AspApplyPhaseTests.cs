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
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>ASP APPLY: patch, encoding, structure, shape and safety are judged in memory before anything is written.</summary>
public sealed class AspApplyPhaseTests
{
    private const string Root = "/w";
    private const string PagePath = Root + "/page.asp";
    private static readonly AspTextCodec Codec = new("cp949");

    private readonly MockFileSystem _fs = new();
    private readonly AspOptions _options = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly FileGuard _guard;

    public AspApplyPhaseTests() => _guard = new FileGuard(_fs, TimeProvider.System, Codec);

    private const string ProcPage =
        "<%@ Language=\"VBScript\" CodePage=949 %>\n" + // 1
        "<!--#include file=\"util.inc\"-->\n" +         // 2
        "<%\n" +                                        // 3
        "Dim conn\n" +                                  // 4
        "Function Total(id)\n" +                        // 5
        "  Total = id\n" +                              // 6
        "End Function\n" +                              // 7
        "%>\n";                                         // 8

    private const string SplitPage =
        "<%@ Language=\"VBScript\" CodePage=949 %>\n" + // 1
        "<%\n" +                                        // 2
        "x = 1\n" +                                     // 3
        "%>\n" +                                        // 4
        "<!--#include file=\"util.inc\"-->\n" +         // 5
        "<%\n" +                                        // 6
        "y = 2\n" +                                     // 7
        "%>\n";                                         // 8

    private const string UtilInc = "<%\nFunction Pad(v)\n  Pad = v\nEnd Function\n%>\n";

    private AspApplyPhase Phase() =>
        new(_guard, new FeedbackBuilder(QwenTokenCounter.Shared, new BudgetOptions().Feedback), _ui, _options, Codec);

    private async Task<RunContext> Sliced(string text, string? proc = null, TextSpanInfo? lines = null, bool dryRun = false)
    {
        _fs.AddFile(PagePath, new MockFileData(Encoding.UTF8.GetBytes(text)));
        _fs.AddFile(Root + "/util.inc", new MockFileData(Encoding.UTF8.GetBytes(UtilInc)));
        var request = new RunRequest(PagePath, null, proc, null, "do it", true, dryRun, null, Kind: EditKind.Asp, Lines: lines);
        var ctx = new RunContext("r1", request, new PellyOptions { Asp = _options })
        {
            FilePath = PagePath,
            Workspace = new Workspace(Root, Root + "/.pelly"),
        };
        ctx.Original = _guard.Read(PagePath);
        ctx.Current = ctx.Original;
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(PhaseOutcome.Ok, await new AspLocatePhase(_fs, new ScriptedUserInteraction(), _options, Codec).ExecuteAsync(ctx, ct));
        Assert.Equal(PhaseOutcome.Ok,
            await new AspSlicePhase(_guard, QwenTokenCounter.Shared, new BudgetOptions(), null, _options).ExecuteAsync(ctx, ct));
        return ctx;
    }

    private Task<PhaseOutcome> Apply(RunContext ctx, string search, string replace)
    {
        ctx.Blocks = [new PatchBlock(search, replace)];
        return Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);
    }

    private string Disk() => Encoding.UTF8.GetString(_fs.File.ReadAllBytes(PagePath));

    [Fact]
    public async Task Apply_OutsideRegion_MatchFailed()
    {
        var ctx = await Sliced(ProcPage, proc: "Total");

        var outcome = await Apply(ctx, "Dim conn", "Dim conn, rs");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(PatchFailureKinds.OutsideRegion, ctx.FailureKind);
        Assert.Equal(ProcPage, Disk());
    }

    [Fact]
    public async Task Apply_Unencodable_AspEncodingFeedback()
    {
        var ctx = await Sliced(ProcPage, proc: "Total");

        var outcome = await Apply(ctx, "  Total = id", "  Total = \"😀\" & id");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(FailureKinds.AspEncoding, ctx.FailureKind);
        Assert.Contains("이 파일은 CP949입니다. 다음 문자를 쓸 수 없습니다:", ctx.Feedback!.Text);
        Assert.Contains("😀", ctx.Feedback.Text);
        Assert.Contains("line 6", ctx.Feedback.Text);
        Assert.Equal(AspCheckStatus.Failed, ctx.Asp!.Checks["Encoding"].Status);
        Assert.Equal(ProcPage, Disk());
    }

    [Fact]
    public async Task Apply_NewUnclosedIf_StructureFeedback()
    {
        var ctx = await Sliced(ProcPage, proc: "Total");

        var outcome = await Apply(ctx, "  Total = id", "  If id > 0 Then\n  Total = id");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(FailureKinds.AspStructureErrors, ctx.FailureKind);
        Assert.StartsWith("ASP STRUCTURE CHECK FAILED", ctx.Feedback!.Text);
        Assert.Equal(AspCheckStatus.Failed, ctx.Asp!.Checks["Structure"].Status);
        Assert.Equal(AspCheckStatus.Passed, ctx.Asp.Checks["Encoding"].Status);
        Assert.Equal(ProcPage, Disk());
    }

    [Fact]
    public async Task Apply_LockedIncludeLine_MatchFailed()
    {
        var ctx = await Sliced(SplitPage, lines: new TextSpanInfo(3, 7));
        Assert.Equal(2, ctx.SliceResult.Regions.Count);

        var outcome = await Apply(ctx, "%>\n<!--#include file=\"util.inc\"-->", "%>\n<!--#include file=\"other.inc\"-->");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(PatchFailureKinds.OutsideRegion, ctx.FailureKind);
        Assert.Equal(SplitPage, Disk());
    }

    [Fact]
    public async Task Apply_SeparatorCopiedIntoSearch_MatchFailed()
    {
        var ctx = await Sliced(SplitPage, lines: new TextSpanInfo(3, 7));

        var outcome = await Apply(ctx, "%>\n" + AspSlicePhase.LockedSeparator + "\n<%", "%>\n<%");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(PatchFailureKinds.NotFound, ctx.FailureKind);
        Assert.Equal(SplitPage, Disk());
    }

    [Fact]
    public async Task Apply_NewIncludeLine_ShapeRejected()
    {
        var ctx = await Sliced(SplitPage, lines: new TextSpanInfo(3, 7));

        var outcome = await Apply(ctx, "x = 1", "x = 1\n%>\n<!--#include file=\"other.inc\"-->\n<%");

        Assert.Equal(PhaseOutcome.ShapeRejected, outcome);
        Assert.Equal(FailureKinds.ShapeRejected, ctx.FailureKind);
        Assert.StartsWith("SHAPE REJECTED: ", ctx.Feedback!.Text);
        Assert.Contains("include", ctx.Feedback.Text);
        Assert.Equal(AspCheckStatus.Failed, ctx.Asp!.Checks["Structure"].Status);
        Assert.Equal(SplitPage, Disk());
    }

    [Fact]
    public async Task Apply_ProcRenamed_ShapeRejected()
    {
        var ctx = await Sliced(ProcPage, proc: "Total");

        var outcome = await Apply(ctx, "Function Total(id)\n  Total = id", "Function Sum(id)\n  Sum = id");

        Assert.Equal(PhaseOutcome.ShapeRejected, outcome);
        Assert.Equal(FailureKinds.ShapeRejected, ctx.FailureKind);
        Assert.Contains("Total", ctx.Feedback!.Text);
        Assert.Equal(ProcPage, Disk());
    }

    [Fact]
    public async Task Apply_NewSqlConcat_SafetyRejected()
    {
        var ctx = await Sliced(ProcPage, proc: "Total");

        var outcome = await Apply(ctx, "  Total = id",
            "  Set rs = conn.Execute(\"SELECT * FROM T WHERE id=\" & Request(\"id\"))\n  Total = id");

        Assert.Equal(PhaseOutcome.SafetyRejected, outcome);
        Assert.Equal(FailureKinds.AspSafetyRejected, ctx.FailureKind);
        Assert.StartsWith("ASP SAFETY REJECTED", ctx.Feedback!.Text);
        Assert.Contains(AspSafetyGuard.SqlConcatRequest, ctx.Feedback.Text);
        Assert.Contains("line 6", ctx.Feedback.Text);
        Assert.Equal(AspCheckStatus.Failed, ctx.Asp!.Checks["Safety"].Status);
        Assert.Equal(AspCheckStatus.Passed, ctx.Asp.Checks["Structure"].Status);
        Assert.Equal(ProcPage, Disk());
    }

    [Fact]
    public async Task Apply_Unchanged_NoChange()
    {
        var ctx = await Sliced(ProcPage, proc: "Total");

        var outcome = await Apply(ctx, "  Total = id", "  Total = id");

        Assert.Equal(PhaseOutcome.MatchFailed, outcome);
        Assert.Equal(FailureKinds.NoChange, ctx.FailureKind);
        Assert.StartsWith("NO CHANGE", ctx.Feedback!.Text);
    }

    [Fact]
    public async Task Apply_DryRun_NotWritten()
    {
        var ctx = await Sliced(ProcPage, proc: "Total", dryRun: true);

        var outcome = await Apply(ctx, "  Total = id", "  Total = id + 1");

        Assert.Equal(PhaseOutcome.DryRunCompleted, outcome);
        var diff = Assert.Single(_ui.Diffs);
        Assert.Equal(ProcPage, diff.Before);
        Assert.Contains("  Total = id + 1\n", diff.After);
        Assert.Equal(ProcPage, Disk());
        Assert.Null(ctx.BackupPath);
    }

    [Fact]
    public async Task Apply_Written_SpanShifted()
    {
        var ctx = await Sliced(ProcPage, proc: "Total");
        Assert.Equal(new TextSpanInfo(5, 7), ctx.Asp!.CurrentSpan);

        var outcome = await Apply(ctx, "  Total = id", "  Dim t\n  t = id\n  Total = t");

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(new TextSpanInfo(5, 9), ctx.Asp.CurrentSpan);
        Assert.Equal(ProcPage.Replace("  Total = id\n", "  Dim t\n  t = id\n  Total = t\n"), Disk());
        Assert.Equal(Disk(), ctx.Current.Text);
        Assert.NotNull(ctx.BackupPath);
        Assert.Equal(AspCheckStatus.Passed, ctx.Asp.Checks["Encoding"].Status);
        Assert.Equal(AspCheckStatus.Passed, ctx.Asp.Checks["Structure"].Status);
        Assert.Equal(AspCheckStatus.Passed, ctx.Asp.Checks["Safety"].Status);
    }
}
