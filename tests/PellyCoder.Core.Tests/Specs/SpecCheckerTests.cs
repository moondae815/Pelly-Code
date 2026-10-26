using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Specs;

public sealed class SpecCheckerTests
{
    private const string Cs =
        "namespace A;\npublic class OrderService\n{\n    public void Cancel() { throw new InvalidOperationException(); }\n    public void Dup() { }\n    public void Dup(int x) { }\n}\n" +
        "public enum OrderStatus { Shipped }\n";

    private const string Sql =
        "CREATE PROCEDURE dbo.usp_One AS SELECT 1;\n";

    private const string TwoSql =
        "CREATE PROCEDURE dbo.usp_One AS SELECT 1;\nGO\nCREATE PROCEDURE dbo.usp_Two AS SELECT 2;\n";

    private static MockFileSystem Fs(string tasks, string criteria = "- AC1: 하나\n")
    {
        var fs = new MockFileSystem();
        fs.AddFile("/ws/App.sln", "");
        fs.AddFile("/ws/src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        fs.AddFile("/ws/src/App/OrderService.cs", Cs);
        fs.AddFile("/ws/src/Tests/OrderTests.cs", "public class OrderTests { [Fact] public void Cancel_works() { } }\n");
        fs.AddFile("/ws/db/one.sql", Sql);
        fs.AddFile("/ws/db/two.sql", TwoSql);
        fs.AddFile("/ws/changes/x.md", "---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n" + criteria + "\n## 작업\n" + tasks);
        return fs;
    }

    private static SpecCheckResult Check(MockFileSystem fs) => SpecChecker.Check(fs, "/ws/changes/x.md", 160);

    private static string[] Codes(SpecCheckResult r) => r.Issues.Select(i => i.Code).ToArray();

    [Fact]
    public void Clean_spec_has_no_issues()
    {
        var fs = Fs("- [ ] T1 cs src/App/OrderService.cs#OrderService.Cancel InvalidOperationException을 던지게 한다 (AC1)\n" +
                    "- [ ] T2 sql db/one.sql 한 줄 추가\n");
        var r = Check(fs);
        Assert.Empty(r.Issues);
        Assert.False(r.HasErrors);
        // MockFileSystem roots "/ws" at the current drive on Windows (C:\ws).
        Assert.Equal(fs.Path.GetFullPath("/ws"), r.Workspace.Root);
    }

    [Fact]
    public void Done_task_symbols_and_names_are_not_checked()
    {
        // T1 renamed CancelOld to Cancel in an earlier session; its symbol and request now name what is gone.
        var r = Check(Fs("- [x] T1 cs src/App/OrderService.cs#OrderService.CancelOld CancelLegacy를 부른다 [G1] <!-- run: R-1 -->\n" +
                         "- [x] T2 sql db/one.sql#dbo.usp_Gone 요청 <!-- run: R-2 -->\n" +
                         "- [ ] T3 sql db/one.sql 한 줄 추가\n"));

        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Missing_file_symbol_ambiguous_symbol_and_sql_object_are_errors()
    {
        var r = Check(Fs(
            "- [ ] T1 cs src/App/Nope.cs#X 요청\n" +
            "- [ ] T2 cs src/App/OrderService.cs#Missing 요청\n" +
            "- [ ] T3 cs src/App/OrderService.cs#Dup 요청\n" +
            "- [ ] T4 sql db/one.sql#dbo.usp_Nope 요청\n" +
            "- [ ] T5 sql db/two.sql 요청\n" +
            "- [ ] T6 cs src/App/OrderService.cs 요청\n" +
            "- [ ] T7 cs db/one.sql#X 요청\n" +
            "- [ ] T8 sql src/App/OrderService.cs 요청\n"));
        Assert.True(r.HasErrors);
        Assert.Equal(["FileNotFound", "SymbolNotFound", "SymbolAmbiguous", "SqlObjectNotFound", "SqlObjectAmbiguous", "SymbolRequired", "KindMismatch", "KindMismatch"],
            r.Issues.Where(i => i.Severity == SpecIssueSeverity.Error).Select(i => i.Code).ToArray());
    }

    [Fact]
    public void Cs_task_without_symbol_is_an_error()
    {
        var r = Check(Fs("- [ ] T1 cs src/App/OrderService.cs 요청\n"));
        Assert.Equal(["SymbolRequired"], Codes(r));
        Assert.True(r.HasErrors);
    }

    [Fact]
    public void Groups_are_allowed()
    {
        var r = Check(Fs("- [ ] T1 cs src/App/OrderService.cs#Cancel 요청 [G1]\n- [ ] T2 cs src/App/OrderService.cs#OrderService.Cancel 요청 [G1]\n"));
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Path_outside_workspace_is_an_error()
    {
        var r = Check(Fs("- [ ] T1 cs ../x.cs#M 요청\n"));
        Assert.Equal(["OutsideWorkspace"], Codes(r));
    }

    [Fact]
    public void Unknown_identifier_in_request_is_a_warning()
    {
        var r = Check(Fs("- [ ] T1 sql db/one.sql CancelTime 컬럼 추가\n"));
        var issue = Assert.Single(r.Issues);
        Assert.Equal("UnknownIdentifier", issue.Code);
        Assert.Equal(SpecIssueSeverity.Warning, issue.Severity);
        Assert.Contains("CancelTime", issue.Message);
        Assert.False(r.HasErrors);
    }

    [Fact]
    public void Known_identifiers_do_not_warn()
    {
        var r = Check(Fs("- [ ] T1 cs src/App/OrderService.cs#Cancel OrderStatus가 InvalidOperationException이면 처리\n"));
        Assert.Empty(r.Issues);
    }

    private const string CsTask = "- [ ] T1 cs src/App/OrderService.cs#OrderService.Cancel 요청 (AC1)\n";
    private const string DoneCsTask = "- [x] T1 cs src/App/OrderService.cs#OrderService.Cancel 요청 (AC1) <!-- run: R-1 -->\n";

    private static string Ac(string test, string extra = "") => $"- AC1: 하나\n  test: {test}\n{extra}";

    [Fact]
    public void Ac_test_found_is_clean_and_bare_name_works()
    {
        Assert.Empty(Check(Fs(CsTask, Ac("OrderTests.Cancel_works"))).Issues);
        Assert.Empty(Check(Fs(CsTask, Ac("Cancel_works"))).Issues);
    }

    [Fact]
    public void Check_LinkedTestMissing_IsError()
    {
        var r = Check(Fs(CsTask, Ac("OrderTests.Nope")));
        var issue = Assert.Single(r.Issues);
        Assert.Equal("TestNotFound", issue.Code);
        Assert.Equal(SpecIssueSeverity.Error, issue.Severity);
        Assert.Equal("수용 기준 AC1의 테스트 OrderTests.Nope을(를) 작업 공간에서 찾을 수 없습니다 (테스트 특성이 붙은 메서드만 찾습니다).", issue.Message);
        Assert.True(r.HasErrors);
    }

    [Fact]
    public void Check_UnlinkedTestMissing_IsWarning()
    {
        var r = Check(Fs("- [ ] T1 sql db/one.sql 요청 (AC1)\n", Ac("OrderTests.Nope")));
        Assert.Equal(["TestNotFound", "TestUnverified"], Codes(r));
        Assert.All(r.Issues, i => Assert.Equal(SpecIssueSeverity.Warning, i.Severity));
        Assert.False(r.HasErrors);
    }

    [Fact]
    public void Check_LinkedTestAmbiguous_IsError()
    {
        var fs = Fs(CsTask, Ac("Cancel_works"));
        fs.AddFile("/ws/src/Tests/OtherTests.cs", "public class OtherTests { [Fact] public void Cancel_works() { } }\n");
        var r = Check(fs);
        var issue = Assert.Single(r.Issues);
        Assert.Equal("TestAmbiguous", issue.Code);
        Assert.Equal(SpecIssueSeverity.Error, issue.Severity);
        Assert.Contains("OrderTests.Cancel_works", issue.Message);
        Assert.Contains("OtherTests.Cancel_works", issue.Message);
    }

    [Fact]
    public void Check_ProductMethodSameName_NotATest()
    {
        // OrderService.Cancel is a product method without a test attribute.
        var r = Check(Fs(CsTask, Ac("OrderService.Cancel")));
        var issue = Assert.Single(r.Issues);
        Assert.Equal("TestNotFound", issue.Code);
        Assert.Equal(SpecIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void Check_TestWithoutLinkedCsTask_TestUnverified()
    {
        var r = Check(Fs("- [ ] T1 sql db/one.sql 요청 (AC1)\n", Ac("OrderTests.Cancel_works")));
        var issue = Assert.Single(r.Issues);
        Assert.Equal("TestUnverified", issue.Code);
        Assert.Equal(SpecIssueSeverity.Warning, issue.Severity);
        Assert.Equal("수용 기준 AC1은(는) 테스트로 검증되지 않습니다 (연결된 cs 작업이 없음).", issue.Message);
    }

    [Fact]
    public void Check_TaskTargetsItsTest_TestIsTarget()
    {
        var r = Check(Fs("- [ ] T1 cs src/Tests/OrderTests.cs#OrderTests.Cancel_works 요청 (AC1)\n", Ac("OrderTests.Cancel_works")));
        var issue = Assert.Single(r.Issues);
        Assert.Equal("TestIsTarget", issue.Code);
        Assert.Equal(SpecIssueSeverity.Error, issue.Severity);
        Assert.Equal("작업 T1: 수용 기준 AC1의 테스트 메서드를 고치는 작업입니다. 테스트는 사람이 씁니다.", issue.Message);
    }

    [Fact]
    public void Check_UnlinkedTaskTargetsAcTest_TestIsTarget()
    {
        // T2 is linked to no criterion, yet it would edit AC1's test between T1's RED and a later GREEN.
        var r = Check(Fs(CsTask + "- [ ] T2 cs src/Tests/OrderTests.cs#OrderTests.Cancel_works 요청\n", Ac("OrderTests.Cancel_works")));
        var issue = Assert.Single(r.Issues);
        Assert.Equal("TestIsTarget", issue.Code);
        Assert.Equal(SpecIssueSeverity.Error, issue.Severity);
        Assert.Equal("작업 T2: 수용 기준 AC1의 테스트 메서드를 고치는 작업입니다. 테스트는 사람이 씁니다.", issue.Message);
    }

    [Fact]
    public void Check_TaskOfOtherAcTargetsAcTest_TestIsTarget()
    {
        var r = Check(Fs(CsTask + "- [ ] T2 cs src/Tests/OrderTests.cs#OrderTests.Cancel_works 요청 (AC2)\n",
            Ac("OrderTests.Cancel_works") + "- AC2: 둘\n"));
        var issue = Assert.Single(r.Issues, i => i.Code == "TestIsTarget");
        Assert.Equal("작업 T2: 수용 기준 AC1의 테스트 메서드를 고치는 작업입니다. 테스트는 사람이 씁니다.", issue.Message);
    }

    [Fact]
    public void Check_LinkedBreaksMissing_IsError()
    {
        var r = Check(Fs(CsTask, Ac("OrderTests.Cancel_works", "  breaks: OrderTests.Gone\n")));
        var issue = Assert.Single(r.Issues);
        Assert.Equal("TestNotFound", issue.Code);
        Assert.Equal(SpecIssueSeverity.Error, issue.Severity);
        Assert.Contains("OrderTests.Gone", issue.Message);
    }

    [Fact]
    public void Check_AllLinkedTasksDone_OnlyWarnings()
    {
        var r = Check(Fs(DoneCsTask, Ac("OrderTests.Nope", "  breaks: OrderTests.Gone\n")));
        Assert.Equal(["TestNotFound", "TestNotFound"], Codes(r));
        Assert.All(r.Issues, i => Assert.Equal(SpecIssueSeverity.Warning, i.Severity));
        Assert.False(r.HasErrors);
    }

    [Fact]
    public void Parser_errors_are_carried_over()
    {
        var r = Check(Fs("- [ ] T1 cs src/App/OrderService.cs#Cancel 요청\n- [ ] T1 sql db/one.sql 요청\n"));
        Assert.Contains("DuplicateId", Codes(r));
    }

    // ── SQL impact search (V2-1d §7.1, §7.2) ──

    private const string DevCs = "Server=dev-sql;Database=Shop;Integrated Security=true";
    private const string ProdCs = "Server=prod-sql;Database=Shop;Integrated Security=true";

    private static SqlOptions SqlConfig(string? prod, string? dev = DevCs) => new()
    {
        ConnectionString = dev,
        AllowedHosts = ["dev-sql"],
        AllowedDatabases = ["Shop"],
        Production = new SqlProductionOptions { ConnectionString = prod, AllowedHosts = ["prod-sql"], AllowedDatabases = ["Shop"] },
    };

    private sealed class SqlHarness
    {
        public List<string> Opened { get; } = [];
        public FakeSqlFinder Finder { get; } = new();

        public SqlImpactSetup Setup(SqlOptions options) => new(Finder, options, cs => { Opened.Add(cs); return new NoReader(); });
    }

    private static Task<SpecCheckResult> CheckSql(MockFileSystem fs, SqlHarness h, SqlOptions options, bool impactFlag) =>
        SpecChecker.CheckAsync(fs, "/ws/changes/x.md", 160, null, impactFlag, TestContext.Current.CancellationToken, h.Setup(options));

    private const string SqlTasks = "- [ ] T1 sql db/one.sql 한 줄 추가\n";

    [Theory]
    [InlineData(ProdCs)]
    [InlineData(null)]
    public async Task Without_impact_flag_no_sql_search(string? prod)
    {
        var h = new SqlHarness();

        var r = await CheckSql(Fs(SqlTasks), h, SqlConfig(prod), impactFlag: false);

        Assert.Empty(h.Finder.Calls);
        Assert.Empty(h.Opened);
        Assert.Empty(r.Issues);
        Assert.Null(r.SqlImpact);
    }

    [Fact]
    public async Task Impact_flag_with_prod_uses_prod_basis()
    {
        var h = new SqlHarness();

        var r = await CheckSql(Fs(SqlTasks), h, SqlConfig(ProdCs), impactFlag: true);

        var call = Assert.Single(h.Finder.Calls);
        Assert.Equal(SqlImpactBasis.Prod, call.Basis);
        Assert.Equal([ProdCs], h.Opened);
        Assert.NotNull(call.Db.Reader);
        var seed = Assert.Single(call.Seeds);
        Assert.Equal("T1", seed.TaskId);
        Assert.True(seed.Object.SameAs(new SqlObjectName("dbo", "usp_One")));
        Assert.DoesNotContain(r.Issues, i => i.Code == "ImpactSqlDevBased");
        Assert.NotNull(r.SqlImpact);
        Assert.Equal("prod", r.SqlImpactSummary!.Basis);
    }

    [Fact]
    public async Task Impact_flag_without_prod_uses_dev_basis()
    {
        var h = new SqlHarness();
        h.Finder.Basis = SqlImpactBasis.Dev;

        var r = await CheckSql(Fs(SqlTasks), h, SqlConfig(null), impactFlag: true);

        var call = Assert.Single(h.Finder.Calls);
        Assert.Equal(SqlImpactBasis.Dev, call.Basis);
        Assert.Equal([DevCs], h.Opened);
        var dev = Assert.Single(r.Issues, i => i.Code == "ImpactSqlDevBased");
        Assert.Equal(0, dev.Line);
        Assert.Equal("dev", r.SqlImpactSummary!.Basis);
    }

    [Fact]
    public async Task Prod_configured_but_unreachable_does_not_fall_back_to_dev()
    {
        var h = new SqlHarness();
        h.Finder.Basis = null; // the finder reports the problem and runs only the string search

        // prod-sql is allowed; this host is not, so the production reader is refused with a problem.
        var r = await CheckSql(Fs(SqlTasks), h, SqlConfig("Server=elsewhere;Database=Shop;Integrated Security=true"), impactFlag: true);

        var call = Assert.Single(h.Finder.Calls);
        Assert.Equal(SqlImpactBasis.Prod, call.Basis);
        Assert.Null(call.Db.Reader);
        Assert.NotNull(call.Db.Problem);
        Assert.Empty(h.Opened);
        var issue = Assert.Single(r.Issues, i => i.Code == "ImpactSqlUnavailable");
        Assert.StartsWith("영향 탐색(SQL): 운영 DB에 접속하지 못해 SP 쪽 영향을 찾지 못했습니다: ", issue.Message);
        Assert.Contains("sql.production.allowedHosts", issue.Message);
    }

    [Fact]
    public async Task Spec_check_writes_list_under_pelly_impact()
    {
        var h = new SqlHarness();
        h.Finder.Tables = [new SqlTableShare("T1", new SqlObjectName("dbo", "Orders"), ["Status"], 2, 1, 0, 1)];
        h.Finder.FullLists = new Dictionary<string, IReadOnlyList<SqlImpactModuleRow>>
        {
            ["T1|dbo.Orders"] = [new(new SqlObjectName("dbo", "usp_Read"), ["Status"], false), new(new SqlObjectName("dbo", "usp_Broken"), [], true)],
        };
        var fs = Fs(SqlTasks);
        fs.AddFile("/ws/.pelly/impact/x-sql.json", "stale");

        var r = await CheckSql(fs, h, SqlConfig(ProdCs), impactFlag: true);

        var json = JsonDocument.Parse(fs.File.ReadAllText("/ws/.pelly/impact/x-sql.json")).RootElement;
        Assert.Equal("prod", json.GetProperty("basis").GetString());
        Assert.Equal("T1", json.GetProperty("seeds")[0].GetProperty("taskId").GetString());
        Assert.Equal("dbo.usp_One", json.GetProperty("seeds")[0].GetProperty("object").GetString());
        var table = json.GetProperty("tables")[0];
        Assert.Equal("dbo.Orders", table.GetProperty("table").GetString());
        Assert.Equal(["Status"], table.GetProperty("writtenColumns").EnumerateArray().Select(c => c.GetString()));
        var rows = table.GetProperty("rows").EnumerateArray().ToList();
        Assert.Equal(["dbo.usp_Read", "dbo.usp_Broken"], rows.Select(x => x.GetProperty("module").GetString()));
        Assert.True(rows[1].GetProperty("unverifiable").GetBoolean());

        var shared = Assert.Single(r.Issues, i => i.Code == "ImpactTableShared");
        Assert.EndsWith("전체 목록: .pelly/impact/x-sql.json", shared.Message);
        Assert.Equal(".pelly/impact/x-sql.json", r.SqlImpactSummary!.ListPath);
    }

    [Fact]
    public async Task Sql_finder_failure_is_a_warning()
    {
        var h = new SqlHarness();
        h.Finder.Throw = new InvalidOperationException("터졌다");

        var r = await CheckSql(Fs(SqlTasks), h, SqlConfig(ProdCs), impactFlag: true);

        var issue = Assert.Single(r.Issues);
        Assert.Equal("ImpactFailed", issue.Code);
        Assert.Equal(SpecIssueSeverity.Warning, issue.Severity);
        Assert.Contains("터졌다", issue.Message);
        Assert.Null(r.SqlImpact);
    }

    [Fact]
    public async Task Sql_finder_cancellation_propagates()
    {
        var h = new SqlHarness();
        h.Finder.Throw = new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CheckSql(Fs(SqlTasks), h, SqlConfig(ProdCs), impactFlag: true));
    }

    [Fact]
    public async Task Sql_target_named_by_a_task_is_not_missing()
    {
        var h = new SqlHarness();
        h.Finder.Targets =
        [
            new ImpactTarget("T1", "", "dbo.usp_Two", "EXEC", ImpactTargetKind.SqlObject),
            new ImpactTarget("T1", "", "dbo.usp_Other", "참조", ImpactTargetKind.SqlObject),
        ];

        var r = await CheckSql(Fs(SqlTasks + "- [ ] T2 sql db/two.sql#usp_Two 둘째\n"), h, SqlConfig(ProdCs), impactFlag: true);

        var missing = Assert.Single(r.Issues, i => i.Code == "ImpactMissing");
        Assert.Contains("dbo.usp_Other (참조)", missing.Message);
    }

    private sealed class FakeSqlFinder : ISqlImpactFinder
    {
        public List<(IReadOnlyList<SqlImpactSeed> Seeds, SqlReaderOpen Db, SqlImpactBasis Basis)> Calls { get; } = [];
        public SqlImpactBasis? Basis { get; set; } = SqlImpactBasis.Prod;
        public IReadOnlyList<ImpactTarget> Targets { get; set; } = [];
        public IReadOnlyList<SqlTableShare> Tables { get; set; } = [];
        public IReadOnlyDictionary<string, IReadOnlyList<SqlImpactModuleRow>> FullLists { get; set; } = new Dictionary<string, IReadOnlyList<SqlImpactModuleRow>>();
        public Exception? Throw { get; set; }

        public Task<SqlImpactResult> FindAsync(Workspace ws, IReadOnlyList<SqlImpactSeed> seeds, SqlReaderOpen db, SqlImpactBasis basis, CancellationToken ct)
        {
            Calls.Add((seeds, db, basis));
            if (Throw is not null) throw Throw;
            var unavailable = db.Problem;
            return Task.FromResult(new SqlImpactResult(unavailable is null ? Basis : null, unavailable, Targets, Tables, [], false, FullLists));
        }
    }

    private sealed class NoReader : ISqlDefinitionReader
    {
        public Task<SqlServerProbe> ProbeAsync(CancellationToken ct) => throw new InvalidOperationException("must not connect");
        public Task<SqlModuleDefinition?> GetDefinitionAsync(SqlObjectName name, CancellationToken ct) => throw new InvalidOperationException("must not connect");
        public Task<IReadOnlyList<SqlModuleRef>> GetReferencingModulesAsync(SqlObjectName name, CancellationToken ct) => throw new InvalidOperationException("must not connect");
        public Task<IReadOnlyList<SqlColumnUse>?> GetColumnUsesAsync(SqlObjectName module, CancellationToken ct) => throw new InvalidOperationException("must not connect");
        public Task<IReadOnlyList<string>> GetColumnsAsync(SqlObjectName table, CancellationToken ct) => throw new InvalidOperationException("must not connect");
    }
}
