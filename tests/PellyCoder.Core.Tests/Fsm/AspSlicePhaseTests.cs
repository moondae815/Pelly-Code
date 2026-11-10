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

    private async Task<RunContext> Located(string text, string? proc = null, TextSpanInfo? lines = null,
        IReadOnlyList<ChangedProcedure>? changed = null)
    {
        _fs.AddFile(PagePath, new MockFileData(Encoding.UTF8.GetBytes(text)));
        _fs.AddFile(Root + "/util.inc", new MockFileData(Encoding.UTF8.GetBytes(UtilInc)));
        var request = new RunRequest(PagePath, null, proc, null, "do it", true, false, null, Kind: EditKind.Asp, Lines: lines,
            ChangedProcedures: changed);
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
        Assert.True(ctx.Asp.OriginalSpCallsSet);
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
        Assert.Contains("x19 = 19\n... (target lines) ...\nx23 = 23", around);
        Assert.DoesNotContain("[EDITABLE", around);
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
    public async Task Slice_shows_changed_signature_with_note()
    {
        _sql.Signatures.Add(ItemTotal());
        var changed = new SqlProcedureSignature(SqlObjectName.Parse("usp_ItemTotal"),
            [new SqlParameterInfo("@ItemId", "int", false), new SqlParameterInfo("@Status", "varchar(10)", false, HasDefault: true)], null);
        var older = new SqlProcedureSignature(SqlObjectName.Parse("dbo.usp_ItemTotal"), [], null);
        var ctx = await Located(ProcPage, proc: "Total", changed: [new ChangedProcedure("T0", older), new ChangedProcedure("T1", changed)]);

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("PROCEDURE usp_ItemTotal (@ItemId int, @Status varchar(10)) -- changed by T1, not deployed yet", PartText(ctx, "sql"));
        Assert.DoesNotContain("GetProcedureSignatures", _sql.Calls); // nothing else to ask the database
    }

    [Fact]
    public async Task Slice_changed_signature_without_db()
    {
        var changed = new SqlProcedureSignature(SqlObjectName.Parse("dbo.usp_ItemTotal"), [new SqlParameterInfo("@ItemId", "int", false)], null);
        var ctx = await Located(ProcPage, proc: "Total", changed: [new ChangedProcedure("T1", changed)]);

        var outcome = await Phase(withSql: false).ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("PROCEDURE dbo.usp_ItemTotal (@ItemId int) -- changed by T1, not deployed yet", PartText(ctx, "sql"));
        Assert.DoesNotContain("SqlContext", ctx.Asp!.Checks.Keys);
    }

    [Fact]
    public async Task Standalone_pelly_asp_unchanged()
    {
        // A changed procedure the target does not call leaves the database signature as it was.
        _sql.Signatures.Add(ItemTotal());
        var other = new SqlProcedureSignature(SqlObjectName.Parse("dbo.usp_Other"), [], null);
        var ctx = await Located(ProcPage, proc: "Total", changed: [new ChangedProcedure("T1", other)]);

        Assert.Equal(PhaseOutcome.Ok, await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken));

        Assert.Equal("PROCEDURE dbo.usp_ItemTotal (@ItemId int, @Total decimal(18,2) OUTPUT)\n  RESULT (Amount decimal(18,2))",
            PartText(ctx, "sql"));
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
        // A line range with every part present: around, sql, variables, procedures and page.
        var body = string.Concat(Enumerable.Range(1, 12).Select(i => $"filler{i} = \"some longer text {i}\"\n"));
        var text = "<%@ Language=\"VBScript\" %>\n<!--#include file=\"util.inc\"-->\n<%\nDim total\ntotal = 0\n" + body
            + "Set rs = conn.Execute(\"exec dbo.usp_ItemTotal \" & CLng(total))\ntotal = Pad(rs(0))\n" + body + "%>\n";
        _sql.Signatures.Add(ItemTotal());
        var ctx = await Located(text, lines: new TextSpanInfo(18, 19));
        Assert.Equal(PhaseOutcome.Ok, await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken));
        Assert.Equal(["target", "page", "procedures", "variables", "sql", "around"], ctx.SliceResult.Slice.Included.Select(p => p.Kind));
        var tokens = ctx.SliceResult.Slice.Included.ToDictionary(p => p.Kind, p => p.Tokens);

        string[] order = ["around", "sql", "variables", "procedures", "page"];
        for (var dropped = 1; dropped <= order.Length; dropped++)
        {
            var kept = tokens.Where(t => !order.Take(dropped).Contains(t.Key)).Sum(t => t.Value);
            var outcome = await Phase(new BudgetOptions { Context = kept }).ExecuteAsync(ctx, TestContext.Current.CancellationToken);

            Assert.Equal(PhaseOutcome.Ok, outcome);
            Assert.Equal(order.Take(dropped), ctx.SliceResult.Slice.Omitted.Select(p => p.Kind));
            Assert.DoesNotContain(ctx.SliceResult.Slice.Included, p => order.Take(dropped).Contains(p.Kind));
            Assert.Equal(6 - dropped, ctx.SliceResult.Slice.Included.Count);
        }
    }

    [Fact]
    public async Task Slice_LinesTarget_LockedLineInside_SeparatorShown()
    {
        const string text = "<%\nx = 1\n%>\n<!--#include file=\"util.inc\"-->\n<%\ny = 2\n%>\n";
        var ctx = await Located(text, lines: new TextSpanInfo(2, 6));

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("x = 1\n%>\n... (locked include/directive line — not editable) ...\n<%\ny = 2", PartText(ctx, "target"));
        Assert.Equal(
            [new EditableRegion(new TextSpanInfo(2, 3), "target"), new EditableRegion(new TextSpanInfo(5, 6), "target")],
            ctx.SliceResult.Regions);
    }

    [Fact]
    public async Task Slice_LinesInsideFunction_IncludesLocalDims()
    {
        const string text = "<%\nFunction Calc(a)\n  Dim t, u\n  Const K = 3\n  t = a + K\n  Calc = t\nEnd Function\n%>\n";
        var ctx = await Located(text, lines: new TextSpanInfo(5, 5));

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.Ok, outcome);
        Assert.Equal("Function Calc(a)\n  Dim t, u\n  Const K = 3", PartText(ctx, "variables"));
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
        _fs.File.WriteAllBytes(PagePath, Encoding.UTF8.GetBytes(ProcPage.Replace("Function Total(id)", "Function Other(id)")));
        ctx.Current = _guard.Read(PagePath);

        var outcome = await Phase().ExecuteAsync(ctx, TestContext.Current.CancellationToken);

        Assert.Equal(PhaseOutcome.NotFound, outcome);
        Assert.Equal("TargetNotFound", ctx.FailureKind);
    }

    [Fact]
    public async Task Asp_prompt_v2_env_has_js_level_only_when_block_overlaps()
    {
        const string page =
            "<%@ Language=\"VBScript\" CodePage=949 %>\n" + // 1
            "<p>x</p>\n" +                                  // 2
            "<script>\n" +                                  // 3
            "var a = function () {};\n" +                   // 4
            "</script>\n";                                  // 5

        var touching = await Located(page, lines: new TextSpanInfo(3, 5));
        await Phase().ExecuteAsync(touching, TestContext.Current.CancellationToken);
        var env = PartText(touching, "env");
        Assert.Contains("Language level: ES5", env);
        Assert.Contains("jQuery", env);

        var apart = await Located(page, lines: new TextSpanInfo(2, 2));
        await Phase().ExecuteAsync(apart, TestContext.Current.CancellationToken);
        Assert.DoesNotContain(apart.SliceResult.Slice.Included, p => p.Kind == "env");
    }

    private const string ScriptPage =
        "<%@ Language=\"VBScript\" CodePage=949 %>\n" + // 1
        "<input onclick=\"fnSearch(this)\">\n" +         // 2
        "<p>x</p>\n";                                     // 3

    private async Task<RunContext> LocatedWithJs(string text, TextSpanInfo lines, IReadOnlyList<ChangedJsFunction> changed)
    {
        _fs.AddFile(PagePath, new MockFileData(Encoding.UTF8.GetBytes(text)));
        var request = new RunRequest(PagePath, null, null, null, "do it", true, false, null, Kind: EditKind.Asp, Lines: lines,
            ChangedJsFunctions: changed);
        var ctx = new RunContext("r1", request, new PellyOptions { Asp = _options })
        {
            FilePath = PagePath,
            Workspace = new Workspace(Root, Root + "/.pelly"),
        };
        ctx.Original = _guard.Read(PagePath);
        ctx.Current = ctx.Original;
        Assert.Equal(PhaseOutcome.Ok, await new AspLocatePhase(_fs, new ScriptedUserInteraction(), _options, Codec)
            .ExecuteAsync(ctx, TestContext.Current.CancellationToken));
        return ctx;
    }

    private static readonly ChangedJsFunction Changed =
        new("T1", "js/order.js", new PellyCoder.Core.Web.JsSignature("fnSearch", ["f"]), new PellyCoder.Core.Web.JsSignature("fnSearch", ["f", "page"]));

    [Fact]
    public async Task AspSlice_ChangedFunctionLine_OnlyWhenNameInRange()
    {
        var ctx = await LocatedWithJs(ScriptPage, new TextSpanInfo(2, 2), [Changed]);
        Assert.Equal(PhaseOutcome.Ok, await Phase(withSql: false).ExecuteAsync(ctx, TestContext.Current.CancellationToken));
        Assert.Equal("T1이 바꾼 함수: fnSearch(f, page) (원래 fnSearch(f))", PartText(ctx, "js"));

        var other = await LocatedWithJs(ScriptPage, new TextSpanInfo(3, 3), [Changed]);
        Assert.Equal(PhaseOutcome.Ok, await Phase(withSql: false).ExecuteAsync(other, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(other.SliceResult.Slice.Included, p => p.Kind == "js");
    }
}
