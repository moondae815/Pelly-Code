using System.IO.Abstractions.TestingHelpers;
using System.Text;
using System.Text.Json;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Config;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Models;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Impact;

/// <summary>SP → ASP impact (A2 design §4.3): which pages call a stored procedure a sql task changes, and which of them the spec covers.</summary>
public sealed class AspImpactFinderTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\w" : "/w";

    private static string W(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static SqlImpactSeed Seed(string id, string name, int line = 10) => new(id, line, SqlObjectName.Parse(name));

    private static SpecTask Asp(string id, string path, string? symbol, TextSpanInfo? lines = null, bool done = false) =>
        new(id, TaskKind.Asp, path, symbol, "바꾼다", [], null, done, null, null, 20) { Lines = lines };

    private static AspSpCallEntry Call(string path, int line, string name, int? args = 1, string? proc = null) =>
        new(path, line, SqlObjectName.Parse(name), args, proc);

    private static WebSiteIndexResult Index(params AspSpCallEntry[] calls) => new([], 0, false) { SpCalls = calls };

    private static IReadOnlyList<AspImpactHit> Find(IReadOnlyList<SqlImpactSeed> seeds, IReadOnlyList<SpecTask> tasks, WebSiteIndexResult index) =>
        AspImpactFinder.Find(seeds, tasks, index, _ => null);

    [Fact]
    public void Caller_outside_spec_warns_with_line_and_argument_count()
    {
        var seeds = new[] { Seed("T1", "dbo.usp_OrderList") };
        var hits = Find(seeds, [], Index(Call("admin/order/view.asp", 88, "usp_OrderList", 5), Call("admin/order/edit.asp", 12, "usp_OrderList", null)));

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.False(h.Covered));
        Assert.All(hits, h => Assert.Equal("T1", h.TaskIds));

        var issues = ImpactCheck.AspIssues(seeds, hits, null);
        Assert.Equal(2, issues.Count);
        Assert.All(issues, i => Assert.Equal(("ImpactMissing", SpecIssueSeverity.Warning, 10), (i.Code, i.Severity, i.Line)));
        Assert.Contains(issues, i => i.Message == "작업 T1의 영향 대상이 명세에 없습니다: admin/order/view.asp:88 (usp_OrderList 호출, 인수 5개)");
        Assert.Contains(issues, i => i.Message == "작업 T1의 영향 대상이 명세에 없습니다: admin/order/edit.asp:12 (usp_OrderList 호출, 인수 모름)");
    }

    [Fact]
    public void Caller_inside_procedure_task_is_covered()
    {
        var seeds = new[] { Seed("T1", "usp_X") };
        var tasks = new[] { Asp("T2", "a.asp", "GetList"), Asp("T3", "b.asp", "Repo.Load") };
        var hits = Find(seeds, tasks, Index(Call("a.asp", 5, "usp_X", proc: "getlist"), Call("b.asp", 7, "usp_X", proc: "Repo.Load")));

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.True(h.Covered));
        Assert.Empty(ImpactCheck.AspIssues(seeds, hits, null));
    }

    [Fact]
    public void Caller_in_other_procedure_of_same_file_is_not_covered()
    {
        var seeds = new[] { Seed("T1", "usp_X") };
        var tasks = new[] { Asp("T2", "a.asp", "GetList"), Asp("T3", "c.asp", "GetList", done: true) };
        var hits = Find(seeds, tasks, Index(
            Call("a.asp", 5, "usp_X", proc: "Other"),
            Call("a.asp", 40, "usp_X", proc: null),
            Call("b.asp", 5, "usp_X", proc: "GetList"),
            Call("c.asp", 5, "usp_X", proc: "GetList")));

        Assert.Equal(4, hits.Count);
        Assert.Equal([false, false, false, true], hits.Select(h => h.Covered));
        Assert.Equal(3, ImpactCheck.AspIssues(seeds, hits, null).Count);
    }

    [Fact]
    public void Done_asp_task_covers_its_call()
    {
        // A finished asp task already fixed its calls: apply --resume must not warn about them again.
        var seeds = new[] { Seed("T1", "usp_X") };
        var tasks = new[] { Asp("T2", "a.asp", "GetList", done: true), Asp("T3", "b.asp", null, new TextSpanInfo(1, 9), done: true) };
        var hits = Find(seeds, tasks, Index(Call("a.asp", 5, "usp_X", proc: "GetList"), Call("b.asp", 5, "usp_X")));

        Assert.Equal(2, hits.Count);
        Assert.All(hits, h => Assert.True(h.Covered));
        Assert.Empty(ImpactCheck.AspIssues(seeds, hits, null));
    }

    [Fact]
    public void Procedure_target_uses_the_page_procedure_lines_when_given()
    {
        // A bare name means the top-level procedure when there is one, not the class member of the same name.
        var page = AspPage.Parse("<%\nFunction Load()\n  x = 1\nEnd Function\nClass Repo\n  Function Load()\n    y = 1\n  End Function\nEnd Class\n%>\n");
        var seeds = new[] { Seed("T1", "usp_X") };
        var tasks = new[] { Asp("T2", "a.asp", "Load") };
        var hits = AspImpactFinder.Find(seeds, tasks, Index(Call("a.asp", 3, "usp_X", proc: "Load"), Call("a.asp", 7, "usp_X", proc: "Repo.Load")),
            _ => page);

        Assert.True(hits.Single(h => h.Call.Line == 3).Covered);
        Assert.False(hits.Single(h => h.Call.Line == 7).Covered);
    }

    [Fact]
    public void Caller_inside_lines_task_is_covered()
    {
        var seeds = new[] { Seed("T1", "usp_X") };
        var tasks = new[] { Asp("T2", "a.asp", null, new TextSpanInfo(80, 90)) };
        var hits = Find(seeds, tasks, Index(Call("a.asp", 88, "usp_X"), Call("A.ASP", 80, "usp_X"), Call("a.asp", 95, "usp_X")));

        Assert.True(hits.Single(h => h.Call.Line == 88).Covered);
        Assert.True(hits.Single(h => h.Call.Line == 80).Covered);
        Assert.False(hits.Single(h => h.Call.Line == 95).Covered);
        Assert.Single(ImpactCheck.AspIssues(seeds, hits, null));
    }

    [Fact]
    public void Inc_caller_notes_includer_count()
    {
        var seeds = new[] { Seed("T1", "usp_X") };
        var index = new WebSiteIndexResult([], 0, false)
        {
            SpCalls = [Call("inc/db.inc", 3, "usp_X", 2)],
            Includes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["a.asp"] = ["inc/db.inc"],
                ["b.asp"] = ["inc/db.inc"],
                ["c.asp"] = ["inc/db.inc"],
            },
        };
        var hit = Assert.Single(Find(seeds, [], index));

        Assert.Equal(3, hit.Includers);
        var issue = Assert.Single(ImpactCheck.AspIssues(seeds, [hit], null));
        Assert.Equal("작업 T1의 영향 대상이 명세에 없습니다: inc/db.inc:3 (usp_X 호출, 인수 2개) (include: 그 파일을 쓰는 페이지 3곳)", issue.Message);
    }

    [Fact]
    public void More_than_ten_callers_shows_rest_count()
    {
        var seeds = new[] { Seed("T1", "usp_X"), Seed("T2", "usp_Y", 11) };
        var calls = Enumerable.Range(1, 13).Select(i => Call($"p{i:00}.asp", i, "usp_X")).Append(Call("q.asp", 1, "usp_Y")).ToArray();
        var hits = Find(seeds, [], Index(calls));

        var issues = ImpactCheck.AspIssues(seeds, hits, ".pelly/impact/x-sql.json");
        var x = issues.Where(i => i.Line == 10).ToList();
        Assert.Equal(11, x.Count);
        Assert.Equal("작업 T1의 영향 대상이 명세에 없습니다: dbo.usp_X 호출 외 3곳 — 전체 목록: .pelly/impact/x-sql.json", x[^1].Message);
        Assert.Single(issues, i => i.Line == 11);
    }

    [Fact]
    public void Bracketed_and_schema_less_names_match()
    {
        var seeds = new[] { Seed("T1", "[dbo].[usp_X]"), Seed("T2", "usp_x") };
        var hits = Find(seeds, [], Index(
            Call("a.asp", 1, "usp_X"), Call("b.asp", 1, "dbo.usp_X"), Call("c.asp", 1, "USP_X"), Call("d.asp", 1, "sales.usp_X"),
            Call("e.asp", 1, "usp_XY")));

        Assert.Equal(["a.asp", "b.asp", "c.asp"], hits.Select(h => h.Call.RelativePath));
        Assert.All(hits, h => Assert.Equal("T1, T2", h.TaskIds));
    }

    // ── spec check --impact (with the real site index) ──

    private static MockFileSystem Fs(string tasks, params (string Rel, string Text)[] files)
    {
        var fs = new MockFileSystem();
        fs.AddFile(W("App.sln"), "");
        fs.AddFile(W("db/one.sql"), "CREATE PROCEDURE [dbo].[usp_One] AS SELECT 1;\n");
        fs.AddFile(W("changes/x.md"), "---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n- AC1: 하나\n\n## 작업\n" + tasks);
        foreach (var (rel, text) in files) fs.AddFile(W(rel), new MockFileData(Encoding.UTF8.GetBytes(text)));
        return fs;
    }

    private const string Caller = "<%\nrs.Open \"EXEC usp_One 1, 2\", conn\n%>\n";

    private static Task<SpecCheckResult> CheckAsync(MockFileSystem fs, bool impactFlag) =>
        SpecChecker.CheckAsync(fs, W("changes/x.md"), 160, null, impactFlag, TestContext.Current.CancellationToken, null, new AspOptions());

    [Fact]
    public async Task Seed_from_single_batch_sql_without_symbol_finds_asp_callers()
    {
        var fs = Fs("- [ ] T1 sql db/one.sql 바꾼다\n", ("web/list.asp", Caller));

        var r = await CheckAsync(fs, impactFlag: true);

        var issue = Assert.Single(r.Issues);
        Assert.Equal("ImpactMissing", issue.Code);
        Assert.Equal("작업 T1의 영향 대상이 명세에 없습니다: web/list.asp:2 (usp_One 호출, 인수 2개)", issue.Message);
    }

    [Fact]
    public async Task Asp_list_written_without_sql_search()
    {
        var fs = Fs("- [ ] T1 sql db/one.sql 바꾼다\n- [ ] T2 asp web/b.asp#L1-3 바꾼다\n", ("web/list.asp", Caller), ("web/b.asp", Caller));

        await CheckAsync(fs, impactFlag: true);

        var list = W(".pelly/impact/x-sql.json");
        Assert.True(fs.File.Exists(list));
        using var json = JsonDocument.Parse(fs.File.ReadAllText(list));
        var asp = json.RootElement.GetProperty("asp").EnumerateArray().ToList();
        Assert.Equal(2, asp.Count);
        var first = asp.Single(a => a.GetProperty("path").GetString() == "web/list.asp");
        Assert.Equal(2, first.GetProperty("line").GetInt32());
        Assert.Equal("dbo.usp_One", first.GetProperty("object").GetString());
        Assert.Equal(2, first.GetProperty("arguments").GetInt32());
        Assert.Equal(JsonValueKind.Null, first.GetProperty("procedure").ValueKind);
        Assert.False(first.GetProperty("covered").GetBoolean());
        Assert.True(asp.Single(a => a.GetProperty("path").GetString() == "web/b.asp").GetProperty("covered").GetBoolean());
    }

    [Fact]
    public async Task Spec_check_without_impact_flag_has_no_asp_warnings()
    {
        var fs = Fs("- [ ] T1 sql db/one.sql 바꾼다\n", ("web/list.asp", Caller));

        var r = await CheckAsync(fs, impactFlag: false);

        Assert.Empty(r.Issues);
        Assert.False(fs.File.Exists(W(".pelly/impact/x-sql.json")));
    }

    [Fact]
    public async Task No_asp_pages_no_asp_warnings()
    {
        var fs = Fs("- [ ] T1 sql db/one.sql 바꾼다\n");

        var r = await CheckAsync(fs, impactFlag: true);

        Assert.Empty(r.Issues);
        Assert.False(fs.File.Exists(W(".pelly/impact/x-sql.json")));
    }

    [Fact]
    public async Task Asp_list_overwrites_stale_rows_when_no_caller_is_left()
    {
        var fs = Fs("- [ ] T1 sql db/one.sql 바꾼다\n", ("web/list.asp", Caller));
        await CheckAsync(fs, impactFlag: true);
        fs.File.WriteAllText(W("web/list.asp"), "<%\nx = 1\n%>\n");

        var r = await CheckAsync(fs, impactFlag: true);

        Assert.Empty(r.Issues);
        using var json = JsonDocument.Parse(fs.File.ReadAllText(W(".pelly/impact/x-sql.json")));
        Assert.Empty(json.RootElement.GetProperty("asp").EnumerateArray());
    }
}
