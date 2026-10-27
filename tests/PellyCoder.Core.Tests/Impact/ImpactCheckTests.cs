using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Impact;

public class ImpactCheckTests
{
    private static ChangeSpec Parse(string tasks) =>
        ChangeSpecParser.Parse("---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n- AC1: 하나\n\n## 작업\n" + tasks);

    private const string Mixed =
        "- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 바꾼다 [G1]\n" +
        "- [ ] T2 cs src/App/Repo.cs#Repo.Find 바꾼다 [G1]\n" +
        "- [ ] T3 cs src/App/Other.cs#Other.Run 바꾼다\n" +
        "- [ ] T4 sql db/a.sql 바꾼다 [G1]\n" +
        "- [x] T5 cs src/App/Done.cs#Done.Old 바꾼다 [G1] <!-- run: R-T5 -->\n";

    private static ImpactResult Semantic(params ImpactTarget[] targets) => new(ImpactMode.Semantic, null, targets, []);

    [Fact]
    public void Seeds_are_group_cs_tasks_not_done()
    {
        var spec = Parse(Mixed);

        var group = ImpactCheck.Seeds(spec, allCsTasks: false);
        Assert.Equal(["T1", "T2"], group.Select(s => s.TaskId));
        var t1 = group[0];
        Assert.Equal("src/App/OrderService.cs", t1.Path);
        Assert.Equal("OrderService", t1.ClassName);
        Assert.Equal("GetOrder", t1.Member);
        Assert.Equal(spec.Tasks[0].Line, t1.Line);
        Assert.Equal(TaskKind.CSharp, t1.Kind);

        Assert.Equal(["T1", "T2", "T3"], ImpactCheck.Seeds(spec, allCsTasks: true).Select(s => s.TaskId));
    }

    [Fact]
    public void Seed_path_uses_forward_slashes()
    {
        var spec = Parse("- [ ] T1 cs src\\App\\OrderService.cs#GetOrder 바꾼다 [G1]\n- [ ] T2 cs src/App/Repo.cs#Repo.Find 바꾼다 [G1]\n");

        var seed = ImpactCheck.Seeds(spec, false)[0];
        Assert.Equal("src/App/OrderService.cs", seed.Path);
        Assert.Null(seed.ClassName);
    }

    [Fact]
    public void Target_named_by_any_task_is_not_missing()
    {
        var spec = Parse(Mixed);
        var seeds = ImpactCheck.Seeds(spec, false);

        // Named by T2 (different separator and, where paths ignore case, different case) and by the done task T5.
        var otherCase = PellyCoder.Core.Files.PathComparers.ForOs.Equals("a", "A") ? "SRC\\App\\Repo.cs" : "src\\App\\Repo.cs";
        var covered = ImpactCheck.Issues(spec, seeds, Semantic(
            new ImpactTarget("T1", otherCase, "Find", "호출"),
            new ImpactTarget("T1", "src/App/Done.cs", "Old", "호출")));
        Assert.Empty(covered);

        var missing = ImpactCheck.Issues(spec, seeds, Semantic(new ImpactTarget("T1", "src/App/Controller.cs", "Detail", "호출")));
        var issue = Assert.Single(missing);
        Assert.Equal(new SpecIssue(spec.Tasks[0].Line, SpecIssueSeverity.Warning, "ImpactMissing",
            "작업 T1의 영향 대상이 명세에 없습니다: src/App/Controller.cs#Detail (호출)"), issue);

        // Same path, other member: still missing.
        var otherMember = ImpactCheck.Issues(spec, seeds, Semantic(new ImpactTarget("T2", "src/App/Repo.cs", "Save", "재정의")));
        Assert.Equal("작업 T2의 영향 대상이 명세에 없습니다: src/App/Repo.cs#Save (재정의)", Assert.Single(otherMember).Message);
        Assert.Equal(spec.Tasks[1].Line, otherMember[0].Line);
    }

    [Fact]
    public void Covers_matches_path_and_member_of_cs_tasks_only()
    {
        var spec = Parse(Mixed);

        Assert.True(ImpactCheck.Covers(spec.Tasks[0], new ImpactTarget("T2", "src/App/OrderService.cs", "GetOrder", "호출")));
        Assert.False(ImpactCheck.Covers(spec.Tasks[0], new ImpactTarget("T2", "src/App/OrderService.cs", "Cancel", "호출")));
        Assert.False(ImpactCheck.Covers(spec.Tasks[3], new ImpactTarget("T1", "db/a.sql", "a", "호출")));
    }

    [Fact]
    public void Name_mode_always_warns_ImpactNameBased()
    {
        var spec = Parse(Mixed);
        var result = new ImpactResult(ImpactMode.Name, new SemanticFailure(SemanticFailureKind.LowResolution, 0.028), [], []);

        var issue = Assert.Single(ImpactCheck.Issues(spec, ImpactCheck.Seeds(spec, false), result));
        Assert.Equal(new SpecIssue(0, SpecIssueSeverity.Warning, "ImpactNameBased", "영향 탐색: 이름 기준(부정확): 의미 분석 실패 — 해석률 2.8%"), issue);
    }

    [Fact]
    public void Incomplete_result_warns()
    {
        var spec = Parse(Mixed);
        var result = new ImpactResult(ImpactMode.Semantic, null, [], [], Incomplete: true);

        var issue = Assert.Single(ImpactCheck.Issues(spec, ImpactCheck.Seeds(spec, false), result));
        Assert.Equal(new SpecIssue(0, SpecIssueSeverity.Warning, "ImpactIncomplete", "영향 탐색: 일부 폴더·파일을 읽지 못해 결과가 불완전합니다"), issue);
    }

    [Fact]
    public void Unsupported_seed_warns()
    {
        var spec = Parse(Mixed);
        var result = new ImpactResult(ImpactMode.Semantic, null, [], ["T2"]);

        var issue = Assert.Single(ImpactCheck.Issues(spec, ImpactCheck.Seeds(spec, false), result));
        Assert.Equal(new SpecIssue(spec.Tasks[1].Line, SpecIssueSeverity.Warning, "ImpactUnsupported", "작업 T2: 영향 탐색은 메서드·속성만 봅니다."), issue);
    }

    [Fact]
    public void Seed_not_found_has_its_own_warning()
    {
        var spec = Parse(Mixed);
        var result = new ImpactResult(ImpactMode.Semantic, null, [], []) { SeedsNotFound = ["T2"] };

        var issue = Assert.Single(ImpactCheck.Issues(spec, ImpactCheck.Seeds(spec, false), result));
        Assert.Equal(new SpecIssue(spec.Tasks[1].Line, SpecIssueSeverity.Warning, "ImpactSeedNotFound",
            "작업 T2: 영향 탐색 시작점을 찾지 못했습니다: src/App/Repo.cs#Repo.Find"), issue);
    }

    [Fact]
    public void Place_reached_from_several_seeds_is_warned_once_on_the_first_seed()
    {
        var spec = Parse(Mixed);
        var seeds = ImpactCheck.Seeds(spec, false);
        var result = Semantic(
            new ImpactTarget("T1", "src/App/Controller.cs", "Detail", "호출"),
            new ImpactTarget("T1", "src/App/Other2.cs", "Run", "호출"),
            new ImpactTarget("T2", "src\\App\\Controller.cs", "Detail", "기반 멤버"));

        var issues = ImpactCheck.Issues(spec, seeds, result);

        Assert.Equal(
        [
            new SpecIssue(spec.Tasks[0].Line, SpecIssueSeverity.Warning, "ImpactMissing",
                "작업 T1, T2의 영향 대상이 명세에 없습니다: src/App/Controller.cs#Detail (호출)"),
            new SpecIssue(spec.Tasks[0].Line, SpecIssueSeverity.Warning, "ImpactMissing",
                "작업 T1의 영향 대상이 명세에 없습니다: src/App/Other2.cs#Run (호출)"),
        ], issues);
    }

    // ── SQL impact (V2-1d) ──

    private static readonly IReadOnlyDictionary<string, SqlObjectName> NoSqlObjects = new Dictionary<string, SqlObjectName>();

    private static ImpactTarget SqlTarget(string seed, string name, string reason = "EXEC") => new(seed, "", name, reason, ImpactTargetKind.SqlObject);

    private static SqlImpactResult SqlResult(SqlImpactBasis? basis = SqlImpactBasis.Prod, string? unavailable = null, IReadOnlyList<ImpactTarget>? targets = null,
        IReadOnlyList<SqlTableShare>? tables = null, IReadOnlyList<string>? newObjects = null, bool incomplete = false) =>
        new(basis, unavailable, targets ?? [], tables ?? [], newObjects ?? [], incomplete, new Dictionary<string, IReadOnlyList<SqlImpactModuleRow>>());

    private static (MockFileSystem Fs, Workspace Ws) SqlWorkspace()
    {
        var fs = new MockFileSystem();
        fs.AddFile("/ws/db/one.sql", "CREATE PROCEDURE dbo.usp_One AS SELECT 1;\n");
        fs.AddFile("/ws/db/two.sql", "CREATE PROCEDURE dbo.usp_One AS SELECT 1;\nGO\nCREATE PROCEDURE dbo.usp_Two AS SELECT 2;\n");
        return (fs, new Workspace(fs.Path.GetFullPath("/ws"), fs.Path.GetFullPath("/ws/.pelly")));
    }

    [Fact]
    public void Covers_sql_object_ignores_case_brackets_and_default_schema()
    {
        var spec = Parse("- [ ] T1 sql db/a.sql#[dbo].[USP_Cancel] 바꾼다\n- [ ] T2 sql db/b.sql#usp_Cancel 바꾼다\n- [ ] T3 cs src/App/A.cs#A.Run 바꾼다\n");
        var objects = new Dictionary<string, SqlObjectName>
        {
            ["T1"] = SqlObjectName.Parse("[dbo].[USP_Cancel]"),
            ["T2"] = SqlObjectName.Parse("usp_Cancel"),
        };
        var target = SqlTarget("T9", "dbo.usp_Cancel");

        Assert.True(ImpactCheck.Covers(spec.Tasks[0], target, objects));
        Assert.True(ImpactCheck.Covers(spec.Tasks[1], target, objects));
        Assert.False(ImpactCheck.Covers(spec.Tasks[2], target, objects));
        Assert.False(ImpactCheck.Covers(spec.Tasks[0], SqlTarget("T9", "sales.usp_Cancel"), objects));
    }

    [Fact]
    public void Covers_cs_target_keeps_the_cs_rule()
    {
        var spec = Parse("- [ ] T1 cs src/App/A.cs#A.Run 바꾼다\n- [ ] T2 sql db/one.sql 바꾼다\n");
        var target = new ImpactTarget("T2", "src/App/A.cs", "Run", "이름 호출(문자열)");

        Assert.True(ImpactCheck.Covers(spec.Tasks[0], target, NoSqlObjects));
        Assert.False(ImpactCheck.Covers(spec.Tasks[1], target, NoSqlObjects));
    }

    [Fact]
    public void Covers_sql_task_without_object_uses_file_single_object()
    {
        var (fs, ws) = SqlWorkspace();
        var spec = Parse("- [x] T1 sql db/one.sql 바꾼다 <!-- run: R-1 -->\n- [ ] T2 sql db/two.sql 바꾼다\n- [ ] T3 sql db/two.sql#usp_Two 바꾼다\n");

        var objects = ImpactCheck.SqlTaskObjects(fs, ws, spec, 160);

        Assert.Equal(["T1", "T3"], objects.Keys.Order());
        Assert.True(objects["T1"].SameAs(new SqlObjectName("dbo", "usp_One")));
        Assert.True(ImpactCheck.Covers(spec.Tasks[0], SqlTarget("T3", "dbo.usp_One"), objects));
        Assert.False(ImpactCheck.Covers(spec.Tasks[1], SqlTarget("T3", "dbo.usp_One"), objects));
    }

    [Fact]
    public void SqlSeeds_are_not_done_sql_tasks_and_ambiguous_ones_are_not_found()
    {
        var (fs, ws) = SqlWorkspace();
        var spec = Parse("- [x] T1 sql db/one.sql 바꾼다 <!-- run: R-1 -->\n- [ ] T2 sql db/one.sql 바꾼다\n- [ ] T3 sql db/two.sql 바꾼다\n" +
                         "- [ ] T4 sql db/two.sql#usp_Two 바꾼다\n- [ ] T5 cs src/App/A.cs#A.Run 바꾼다\n");

        var (seeds, notFound) = ImpactCheck.SqlSeeds(fs, ws, spec, 160);

        Assert.Equal(["T2", "T4"], seeds.Select(s => s.TaskId));
        Assert.Equal(spec.Tasks[1].Line, seeds[0].Line);
        Assert.True(seeds[0].Object.SameAs(new SqlObjectName("dbo", "usp_One")));
        Assert.True(seeds[1].Object.SameAs(new SqlObjectName(null, "usp_Two")));
        Assert.Equal(["T3"], notFound);
    }

    private static readonly ChangeSpec SqlSpec = Parse("- [ ] T1 cs src/App/A.cs#A.Run 바꾼다\n- [ ] T2 sql db/a.sql#dbo.usp_X 바꾼다\n");

    private static IReadOnlyList<SqlImpactSeed> SqlSpecSeeds => [new("T2", SqlSpec.Tasks[1].Line, new SqlObjectName("dbo", "usp_X"))];

    private static IReadOnlyDictionary<string, SqlObjectName> SqlSpecObjects => new Dictionary<string, SqlObjectName> { ["T2"] = new("dbo", "usp_X") };

    private static IReadOnlyList<SpecIssue> SqlIssues(SqlImpactResult result, IReadOnlyList<string>? notFound = null, string? listPath = ".pelly/impact/x-sql.json",
        SqlImpactBasis basis = SqlImpactBasis.Prod) =>
        ImpactCheck.SqlIssues(SqlSpec, SqlSpecSeeds, notFound ?? [], result, SqlSpecObjects, "db/pulled", listPath, basis);

    [Fact]
    public void SqlIssues_missing_sql_object_message()
    {
        var issues = SqlIssues(SqlResult(targets: [SqlTarget("T2", "dbo.usp_A"), SqlTarget("T2", "dbo.usp_X", "참조")]));

        Assert.Equal(
        [
            new SpecIssue(SqlSpec.Tasks[1].Line, SpecIssueSeverity.Warning, "ImpactMissing",
                "작업 T2의 영향 대상이 명세에 없습니다: dbo.usp_A (EXEC) — pelly sql pull dbo.usp_A 로 받아 작업 줄에 추가할 수 있습니다 (db/pulled/dbo.usp_A.sql)"),
        ], issues);
    }

    [Theory]
    [InlineData("TR")]
    [InlineData("U")]
    public void SqlIssues_target_pull_cannot_fetch_has_no_pull_hint(string type)
    {
        var target = new ImpactTarget("T2", "", "dbo.trg_A", "참조", ImpactTargetKind.SqlObject, new SqlObjectName("dbo", "trg_A"), type);

        var issue = Assert.Single(SqlIssues(SqlResult(targets: [target])));

        Assert.Equal("작업 T2의 영향 대상이 명세에 없습니다: dbo.trg_A (참조)", issue.Message);
    }

    [Theory]
    [InlineData("P")]
    [InlineData("FN")]
    [InlineData("IF")]
    [InlineData("TF")]
    [InlineData("V")]
    public void SqlIssues_pullable_target_keeps_pull_hint(string type)
    {
        var target = new ImpactTarget("T2", "", "dbo.usp_A", "참조", ImpactTargetKind.SqlObject, new SqlObjectName("dbo", "usp_A"), type);

        var issue = Assert.Single(SqlIssues(SqlResult(targets: [target])));

        Assert.EndsWith("— pelly sql pull dbo.usp_A 로 받아 작업 줄에 추가할 수 있습니다 (db/pulled/dbo.usp_A.sql)", issue.Message);
    }

    [Theory]
    [InlineData("My Proc", "\"[dbo].[My Proc]\"")]
    [InlineData("usp.Dotted", "\"[dbo].[usp.Dotted]\"")]
    [InlineData("usp]X", "\"[dbo].[usp]]X]\"")]
    public void SqlIssues_pull_hint_brackets_names_that_need_it(string name, string argument)
    {
        var target = new ImpactTarget("T2", "", $"dbo.{name}", "EXEC", ImpactTargetKind.SqlObject, new SqlObjectName("dbo", name), "P");

        var issue = Assert.Single(SqlIssues(SqlResult(targets: [target])));

        Assert.Equal($"작업 T2의 영향 대상이 명세에 없습니다: dbo.{name} (EXEC) — pelly sql pull {argument} 로 받아 작업 줄에 추가할 수 있습니다 (db/pulled/dbo.{name}.sql)",
            issue.Message);
    }

    [Theory]
    [InlineData("My Proc")]
    [InlineData("usp.Dotted")]
    [InlineData("usp]X")]
    public void Covers_sql_target_compares_names_not_display_text(string name)
    {
        var spec = Parse($"- [ ] T1 cs src/App/A.cs#A.Run 바꾼다\n- [ ] T2 sql db/a.sql 바꾼다\n");
        var objects = new Dictionary<string, SqlObjectName> { ["T2"] = new(null, name.ToUpperInvariant()) };
        var target = new ImpactTarget("T1", "", $"dbo.{name}", "EXEC", ImpactTargetKind.SqlObject, new SqlObjectName("dbo", name), "P");

        Assert.True(ImpactCheck.Covers(spec.Tasks[1], target, objects));
        Assert.Empty(ImpactCheck.SqlIssues(spec, [], [], SqlResult(targets: [target]), objects, "db/pulled", null, SqlImpactBasis.Prod));
    }

    [Fact]
    public void SqlIssues_pull_directory_uses_forward_slashes()
    {
        var issue = Assert.Single(ImpactCheck.SqlIssues(SqlSpec, SqlSpecSeeds, [], SqlResult(targets: [SqlTarget("T2", "dbo.usp_A")]), SqlSpecObjects,
            "db\\pulled\\", null, SqlImpactBasis.Prod));
        Assert.EndsWith("(db/pulled/dbo.usp_A.sql)", issue.Message);
    }

    [Fact]
    public void SqlIssues_cs_string_target_uses_cs_format_and_rule()
    {
        var issues = SqlIssues(SqlResult(targets:
        [
            new ImpactTarget("T2", "src/App/Repo.cs", "Load", "이름 호출(문자열)"),
            new ImpactTarget("T2", "src/App/A.cs", "Run", "이름 호출(문자열)"),
        ]));

        Assert.Equal(
        [
            new SpecIssue(SqlSpec.Tasks[1].Line, SpecIssueSeverity.Warning, "ImpactMissing",
                "작업 T2의 영향 대상이 명세에 없습니다: src/App/Repo.cs#Load (이름 호출(문자열))"),
        ], issues);
    }

    [Fact]
    public void SqlIssues_table_summary_line()
    {
        var table = new SqlTableShare("T2", new SqlObjectName(null, "Orders"), ["Status", "UpdatedAt"], 50, 7, 0, 2);

        var issue = Assert.Single(SqlIssues(SqlResult(tables: [table]), listPath: ".pelly/runs/apply-1/impact-sql.json"));

        Assert.Equal(new SpecIssue(SqlSpec.Tasks[1].Line, SpecIssueSeverity.Warning, "ImpactTableShared",
            "작업 T2: dbo.Orders를 함께 쓰는 모듈 50개 — 쓰는 컬럼(Status, UpdatedAt)을 읽는 모듈 7개 경고, 외 0개 생략, 컬럼 확인 불가 2개. 전체 목록: .pelly/runs/apply-1/impact-sql.json"),
            issue);
    }

    [Fact]
    public void SqlIssues_table_summary_when_list_not_saved()
    {
        var table = new SqlTableShare("T2", new SqlObjectName("dbo", "Orders"), ["Status"], 3, 1, 0, 0);

        var issue = Assert.Single(SqlIssues(SqlResult(tables: [table]), listPath: null));

        Assert.EndsWith("전체 목록: (저장 실패)", issue.Message);
    }

    [Fact]
    public void SqlIssues_dev_basis_warns_once()
    {
        var issues = SqlIssues(SqlResult(SqlImpactBasis.Dev, targets: [SqlTarget("T2", "dbo.usp_A"), SqlTarget("T2", "dbo.usp_B")]), basis: SqlImpactBasis.Dev);

        var dev = Assert.Single(issues, i => i.Code == "ImpactSqlDevBased");
        Assert.Equal(new SpecIssue(0, SpecIssueSeverity.Warning, "ImpactSqlDevBased", "영향 탐색: 운영 연결이 설정되지 않아 개발 DB 기준으로 찾았습니다"), dev);
        Assert.Equal(2, issues.Count(i => i.Code == "ImpactMissing"));
    }

    [Fact]
    public void SqlIssues_unavailable_names_db()
    {
        var prod = Assert.Single(SqlIssues(SqlResult(null, "로그인 실패")));
        Assert.Equal(new SpecIssue(0, SpecIssueSeverity.Warning, "ImpactSqlUnavailable",
            "영향 탐색(SQL): 운영 DB에 접속하지 못해 SP 쪽 영향을 찾지 못했습니다: 로그인 실패"), prod);

        var dev = Assert.Single(SqlIssues(SqlResult(null, "연결 문자열 없음"), basis: SqlImpactBasis.Dev));
        Assert.Equal("영향 탐색(SQL): 개발 DB에 접속하지 못해 SP 쪽 영향을 찾지 못했습니다: 연결 문자열 없음", dev.Message);
    }

    [Fact]
    public void SqlIssues_new_object_seed()
    {
        var spec = Parse("- [ ] T1 cs src/App/A.cs#A.Run 바꾼다\n- [ ] T2 sql db/new.sql 만든다\n");
        var seeds = new List<SqlImpactSeed> { new("T2", spec.Tasks[1].Line, new SqlObjectName(null, "usp_New")) };

        var issue = Assert.Single(ImpactCheck.SqlIssues(spec, seeds, [], SqlResult(newObjects: ["T2"]), NoSqlObjects, "db/pulled", null, SqlImpactBasis.Prod));

        Assert.Equal(new SpecIssue(spec.Tasks[1].Line, SpecIssueSeverity.Warning, "ImpactSeedNotFound",
            "작업 T2: dbo.usp_New가 서버에 없어(새 객체) SP 쪽 영향을 찾지 않았습니다"), issue);
    }

    [Fact]
    public void SqlIssues_ambiguous_seed_not_found()
    {
        var spec = Parse("- [ ] T1 sql db/two.sql 바꾼다\n");

        var issue = Assert.Single(ImpactCheck.SqlIssues(spec, [], ["T1"], SqlResult(), NoSqlObjects, "db/pulled", null, SqlImpactBasis.Prod));

        Assert.Equal(new SpecIssue(spec.Tasks[0].Line, SpecIssueSeverity.Warning, "ImpactSeedNotFound",
            "작업 T1: 영향 탐색 시작점을 찾지 못했습니다: db/two.sql (#객체를 적으세요)"), issue);
    }

    [Fact]
    public void SqlIssues_incomplete_string_walk()
    {
        var issue = Assert.Single(SqlIssues(SqlResult(incomplete: true)));
        Assert.Equal("ImpactIncomplete", issue.Code);
    }
}
