using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Fsm;

public sealed class AspSlicePhaseTests
{
    private const string Root = "/w";
    private const string PagePath = Root + "/page.asp";
    private static readonly AspTextCodec Codec = new("cp949");

    private readonly MockFileSystem _fs = new();
    private readonly AspOptions _options = new();
    private readonly FakeSqlExecutor _sql = new();
    private readonly FileGuard _guard;

    public AspSlicePhaseTests() => _guard = new FileGuard(_fs, TimeProvider.System, Codec);

    private const string ProcPage =
        "<%@ Language=\"VBScript\" CodePage=949 %>\n" +   // 1
        "<!--#include file=\"util.inc\"-->\n" +           // 2
        "<%\n" +                                          // 3
        "Option Explicit\n" +                             // 4
        "Dim conn, pageSize\n" +                          // 5
        "pageSize = 20\n" +                               // 6
        "' formats a number\n" +                          // 7
        "Function Fmt(n)\n" +                             // 8
        "  Fmt = FormatNumber(n, 0)\n" +                  // 9
        "End Function\n" +                                // 10
        "' total of one item\n" +                         // 11
        "Function Total(id)\n" +                          // 12
        "  Dim rs\n" +                                    // 13
        "  Set rs = conn.Execute(\"exec dbo.usp_ItemTotal \" & CLng(id))\n" + // 14
        "  Total = Fmt(rs(0)) & Pad(pageSize)\n" +        // 15
        "End Function\n" +                                // 16
        "%>\n";                                           // 17

    private const string UtilInc = "<%\n' pads a value\nFunction Pad(v)\n  Pad = v\nEnd Function\n%>\n";

    private AspSlicePhase Phase(BudgetOptions? budget = null, bool withSql = true) =>
        new(_guard, QwenTokenCounter.Shared, budget ?? new BudgetOptions(), withSql ? _sql : null, _options);

    private async Task<RunContext> Located(string text, string? proc = null, TextSpanInfo? lines = null)
    {
        _fs.AddFile(PagePath, new MockFileData(Encoding.UTF8.GetBytes(text)));
        _fs.AddFile(Root + "/util.inc", new MockFileData(Encoding.UTF8.GetBytes(UtilInc)));
        var request = new RunRequest(PagePath, null, proc, null, "do it", true, false, null, Kind: EditKind.Asp, Lines: lines);
        var ctx = new RunContext("r1", request, new PellyOptions { Asp = _options })
        {
            FilePath = PagePath,
            Workspace = new Workspace(Root, Root + "/.pelly"),
        };
        ctx.Original = _guard.Read(PagePath);
        ctx.Current = ctx.Original;
        var outcome = await new AspLocatePhase(_fs, new ScriptedUserInteraction(), _options, Codec)
            .ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal(PhaseOutcome.Ok, outcome);
        return ctx;
    }

    private static string PartText(RunContext ctx, string kind) => ctx.SliceResult.Slice.Included.Single(p => p.Kind == kind).Text;

    private static SqlProcedureSignature ItemTotal() => new(SqlObjectName.Parse("dbo.usp_ItemTotal"),
        [new SqlParameterInfo("@ItemId", "int", false), new SqlParameterInfo("@Total", "decimal(18,2)", true)],
        [new SqlColumnInfo("Amount", "decimal(18,2)", false, false)]);

    [Fact]
    public async Task Slice_ProcedureTarget_PartsInOrder()
    {
        var ctx = await Located(ProcPage, proc: "Total");

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(["target", "page", "procedures", "variables"], ctx.SliceResult.Slice.Included.Select(p => p.Kind));
        Assert.Equal(string.Join("\n", Enumerable.Range(11, 6).Select(ctx.Asp!.OriginalPage.LineText)), PartText(ctx, "target"));
        var page = PartText(ctx, "page");
        Assert.Contains("<%@ Language=\"VBScript\" CodePage=949 %>", page);
        Assert.Contains("<!--#include file=\"util.inc\"-->", page);
        Assert.Contains("Option Explicit: yes", page);
        Assert.Contains("Encoding: CP949", page);
        var procedures = PartText(ctx, "procedures");
        Assert.Contains("' formats a number\nFunction Fmt(n)", procedures);
        Assert.Contains("' pads a value\nFunction Pad(v)", procedures);
        Assert.DoesNotContain("FormatNumber", procedures); // no bodies
        Assert.DoesNotContain("Function Total", procedures);
        var variables = PartText(ctx, "variables");
        Assert.Equal("Dim conn, pageSize\npageSize = 20", variables);
        Assert.Equal("Classic ASP (VBScript); file encoding CP949; Option Explicit: yes", ctx.SliceResult.Slice.LanguageHint);
        Assert.Equal([new EditableRegion(new TextSpanInfo(11, 16), "target")], ctx.SliceResult.Regions);
        Assert.False(ctx.SliceResult.TargetTooLarge);
        Assert.Equal("dbo.usp_ItemTotal", Assert.Single(ctx.Asp!.OriginalSpCalls).Name.Display);
    }

    [Fact]
    public async Task Slice_LinesTarget_HasAround()
    {
        var text = string.Concat(Enumerable.Range(1, 40).Select(i => i == 1 ? "<%\n" : i == 40 ? "%>\n" : $"x{i} = {i}\n"));
        var ctx = await Located(text, lines: new TextSpanInfo(20, 22));

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("x20 = 20\nx21 = 21\nx22 = 22", PartText(ctx, "target"));
        var around = PartText(ctx, "around");
        Assert.StartsWith("x10 = 10\n", around);
        Assert.Contains("x19 = 19\n", around);
        Assert.EndsWith("x32 = 32", around);
        Assert.DoesNotContain("x9 = 9", around);
        Assert.DoesNotContain("x21 = 21", around);
        Assert.DoesNotContain("x33 = 33", around);
        Assert.Equal("Classic ASP (VBScript); file encoding CP949; Option Explicit: no", ctx.SliceResult.Slice.LanguageHint);
    }

    [Fact]
    public async Task Slice_LinesTarget_InsideHtmlSplitIf_Passes()
    {
        const string text = "<html>\n<% If a Then %>\n<p>하나</p>\n<% Else %>\n<p>둘</p>\n<% End If %>\n</html>\n";
        var ctx = await Located(text, lines: new TextSpanInfo(3, 5));

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("<p>하나</p>\n<% Else %>\n<p>둘</p>", PartText(ctx, "target"));
        Assert.Equal([new EditableRegion(new TextSpanInfo(3, 5), "target")], ctx.SliceResult.Regions);
    }

    [Fact]
    public async Task Slice_SqlPart_FromFakeExecutor()
    {
        _sql.Signatures.Add(ItemTotal());
        var ctx = await Located(ProcPage, proc: "Total");

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(["target", "page", "procedures", "variables", "sql"], ctx.SliceResult.Slice.Included.Select(p => p.Kind));
        Assert.Equal("PROCEDURE dbo.usp_ItemTotal (@ItemId int, @Total decimal(18,2) OUTPUT)\n  RESULT (Amount decimal(18,2))",
            PartText(ctx, "sql"));
        Assert.Contains("GetProcedureSignatures", _sql.Calls);
        Assert.DoesNotContain("SqlContext", ctx.Asp!.Checks.Keys);
    }

    [Fact]
    public async Task Slice_NoSql_SkippedRecorded()
    {
        var ctx = await Located(ProcPage, proc: "Total");

        var outcome = await Phase(withSql: false).ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.DoesNotContain(ctx.SliceResult.Slice.Included, p => p.Kind == "sql");
        Assert.Equal(AspCheckStatus.Skipped, ctx.Asp!.Checks["SqlContext"].Status);
    }

    [Fact]
    public async Task Slice_SqlUnavailable_SkippedRecorded()
    {
        _sql.SignaturesFailure = new SqlUnavailableException("down");
        var ctx = await Located(ProcPage, proc: "Total");

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.DoesNotContain(ctx.SliceResult.Slice.Included, p => p.Kind == "sql");
        Assert.Equal(AspCheckStatus.Skipped, ctx.Asp!.Checks["SqlContext"].Status);
        Assert.Contains("down", ctx.Asp.Checks["SqlContext"].Detail);
    }

    [Fact]
    public async Task Slice_Budget_DropsAroundFirst()
    {
        var text = string.Concat(Enumerable.Range(1, 40).Select(i => i == 1 ? "<%\n" : i == 40 ? "%>\n" : $"value{i} = \"some longer text {i}\"\n"));
        var ctx = await Located(text, lines: new TextSpanInfo(20, 22));
        var full = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);
        Assert.Equal(PhaseOutcome.Ok, full);
        var target = ctx.SliceResult.Slice.Included.Single(p => p.Kind == "target").Tokens;
        var page = ctx.SliceResult.Slice.Included.Single(p => p.Kind == "page").Tokens;

        var outcome = await Phase(new BudgetOptions { Context = target + page }).ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(["target", "page"], ctx.SliceResult.Slice.Included.Select(p => p.Kind));
        Assert.Equal("around", Assert.Single(ctx.SliceResult.Slice.Omitted).Kind);
    }

    [Fact]
    public async Task Slice_TargetTooLarge_BudgetExceeded()
    {
        var ctx = await Located(ProcPage, proc: "Total");

        var outcome = await Phase(new BudgetOptions { Context = 5 }).ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.BudgetExceeded, outcome);
        Assert.True(ctx.SliceResult.TargetTooLarge);
        Assert.Equal(["target"], ctx.SliceResult.Slice.Included.Select(p => p.Kind));
        Assert.Contains(ctx.Messages, m => m.Contains("budget.context 5"));
    }

    [Fact]
    public async Task Slice_ExternalChange_EnvError()
    {
        var ctx = await Located(ProcPage, proc: "Total");
        _fs.File.AppendAllText(PagePath, "<!-- someone else -->\n");

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.EnvError, outcome);
        Assert.Equal("FileChangedExternally", ctx.FailureKind);
    }

    [Fact]
    public async Task Slice_ProcMovedAfterWrite_Refound()
    {
        var ctx = await Located(ProcPage, proc: "Total");
        // An earlier attempt added two lines above the target.
        var moved = ProcPage.Replace("pageSize = 20\n", "pageSize = 20\nDim extra\nextra = 1\n");
        _fs.File.WriteAllBytes(PagePath, Encoding.UTF8.GetBytes(moved));
        ctx.Current = _guard.Read(PagePath);

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal(new TextSpanInfo(13, 18), ctx.Asp!.CurrentSpan);
        Assert.Equal(new TextSpanInfo(13, 18), ctx.Target!.MemberSpan);
        Assert.Equal([new EditableRegion(new TextSpanInfo(13, 18), "target")], ctx.SliceResult.Regions);
        Assert.StartsWith("' total of one item\nFunction Total(id)", PartText(ctx, "target"));
    }

    [Fact]
    public async Task Slice_ProcRemoved_TargetNotFound()
    {
        var ctx = await Located(ProcPage, proc: "Total");
        _fs.File.WriteAllBytes(PagePath, Encoding.UTF8.GetBytes(ProcPage.Replace("Function Total(id)", "Function Other(id)").Replace("End Function\n%>", "End Function\n%>")));
        ctx.Current = _guard.Read(PagePath);

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal("TargetNotFound", ctx.FailureKind);
    }
}
