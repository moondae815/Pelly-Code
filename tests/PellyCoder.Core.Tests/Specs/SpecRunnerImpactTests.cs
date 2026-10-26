using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Specs;

/// <summary>The missing-impact check at the start of <c>pelly apply</c> (group cs tasks only; warnings never block).</summary>
public sealed class SpecRunnerImpactTests : IDisposable
{
    private const string OrderService =
        "namespace App;\n\npublic class OrderService\n{\n    public int GetOrder(int id)\n    {\n        return id;\n    }\n}\n";

    private const string Repo =
        "namespace App;\n\npublic class Repo\n{\n    public int Find(int id)\n    {\n        return id;\n    }\n}\n";

    private const string Group =
        "- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 반환 형식을 바꾼다 [G1]\n" +
        "- [ ] T2 cs src/App/Repo.cs#Repo.Find 이름을 바꾼다 [G1]\n";

    private readonly string _root = Directory.CreateTempSubdirectory("pelly-impact-").FullName;
    private readonly FileSystem _fs = new();
    private readonly FileGuard _guard;
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 25, 9, 30, 0, TimeSpan.Zero));
    private readonly ScriptedUserInteraction _ui = new() { IsInteractive = false };
    private readonly Executor _exec;
    private readonly FakeFinder _finder = new();

    public SpecRunnerImpactTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _guard = new FileGuard(_fs, TimeProvider.System);
        _exec = new Executor(_guard);
        Make("App.sln", "");
        Make("src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        Make("src/App/OrderService.cs", OrderService);
        Make("src/App/Repo.cs", Repo);
        Make("db/a.sql", "CREATE PROCEDURE dbo.usp_A AS SELECT 1;\n");
        Directory.CreateDirectory(P("changes"));
    }

    public void Dispose() => Directory.Delete(_root, true);

    private string P(string rel) => Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));

    private void Make(string rel, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), text, new UTF8Encoding(false));
    }

    private string SpecPath => P("changes/x.md");

    private void Spec(string tasks) =>
        Make("changes/x.md", "---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n- AC1: 하나\n\n## 작업\n" + tasks);

    private static ApplyOptions Yes => new(false, false, false, true, false, null, false);

    private Task<int> Run(ApplyOptions? o = null) =>
        new SpecRunner(_fs, _guard, _exec, _ui, _time, 160, groups: new PassingGroups(), impact: _finder)
            .RunAsync(SpecPath, o ?? Yes, TestContext.Current.CancellationToken);

    private JsonElement ApplyJson()
    {
        var dir = Assert.Single(Directory.GetDirectories(P(".pelly/runs"), "apply-*"));
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "apply.json"))).RootElement;
    }

    [Fact]
    public async Task Apply_start_runs_impact_on_group_tasks_and_records_it()
    {
        // T3 is a second group task on the same member; the ungrouped sql task T4 is never a seed.
        Spec(Group + "- [ ] T3 cs src/App/Repo.cs#Repo.Find 한 번 더 [G1]\n- [ ] T4 sql db/a.sql 바꾼다\n");
        _finder.Result = new ImpactResult(ImpactMode.Name, new SemanticFailure(SemanticFailureKind.LowResolution, 0.5),
            [new ImpactTarget("T1", "src/App/Controller.cs", "Detail", "호출")], []);

        var code = await Run();

        Assert.Equal(0, code);
        Assert.Equal(["T1", "T2", "T3", "T4"], _exec.Calls);
        var seeds = Assert.Single(_finder.Calls);
        Assert.Equal(["T1", "T2", "T3"], seeds.Select(s => s.TaskId));
        Assert.Contains(_ui.Warnings, w => w.Contains("작업 T1의 영향 대상이 명세에 없습니다: src/App/Controller.cs#Detail (호출)"));
        Assert.Contains(_ui.Warnings, w => w.Contains("영향 탐색: 이름 기준(부정확): 의미 분석 실패 — 해석률 50.0%"));

        var json = ApplyJson();
        Assert.Equal("name", json.GetProperty("impact").GetString());
        Assert.Equal("LowResolution", json.GetProperty("impactFallback").GetString());
        var codes = json.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ToList();
        Assert.Contains("ImpactMissing", codes);
        Assert.Contains("ImpactNameBased", codes);
    }

    [Fact]
    public async Task Semantic_result_is_recorded_without_fallback()
    {
        Spec(Group);
        _finder.Result = new ImpactResult(ImpactMode.Semantic, null, [], []);

        Assert.Equal(0, await Run());

        var json = ApplyJson();
        Assert.Equal("semantic", json.GetProperty("impact").GetString());
        Assert.Equal(JsonValueKind.Null, json.GetProperty("impactFallback").ValueKind);
    }

    [Fact]
    public async Task Refusals_come_before_the_impact_search()
    {
        Spec(Group);

        // Non-interactive without --yes.
        Assert.Equal(4, await Run(new ApplyOptions(false, false, false, false, false, null, false)));
        // Non-interactive --review-at-end.
        Assert.Equal(4, await Run(new ApplyOptions(false, false, true, true, false, null, false)));
        // Another pelly run holds the workspace.
        using (WorkspaceLock.TryAcquire(_fs, new Workspace(_root, P(".pelly"))))
            Assert.Equal(4, await Run());

        Assert.Empty(_finder.Calls);
        Assert.Empty(_exec.Calls);
    }

    [Fact]
    public async Task Declined_impact_warning_releases_the_lock()
    {
        Spec(Group);
        _ui.IsInteractive = true;
        _ui.ConfirmAnswers.Enqueue(false);
        _finder.Result = new ImpactResult(ImpactMode.Semantic, null, [new ImpactTarget("T1", "src/App/Web.cs", "Detail", "호출")], []);

        Assert.Equal(2, await Run(new ApplyOptions(false, false, false, false, false, null, false)));

        Assert.Single(_finder.Calls);
        Assert.Empty(_exec.Calls);
        using var again = WorkspaceLock.TryAcquire(_fs, new Workspace(_root, P(".pelly")));
        Assert.NotNull(again);
    }

    [Fact]
    public async Task No_group_no_impact_call()
    {
        Spec("- [ ] T1 cs src/App/OrderService.cs#OrderService.GetOrder 바꾼다\n- [ ] T2 sql db/a.sql 바꾼다\n");

        Assert.Equal(0, await Run());

        Assert.Empty(_finder.Calls);
        Assert.Equal(JsonValueKind.Null, ApplyJson().GetProperty("impact").ValueKind);
    }

    [Fact]
    public async Task Impact_never_blocks_apply()
    {
        Spec(Group);
        _finder.Throw = new InvalidOperationException("터졌다");

        var code = await Run();

        Assert.Equal(0, code);
        Assert.Equal(["T1", "T2"], _exec.Calls);
        Assert.Contains(_ui.Warnings, w => w.Contains("영향 탐색을 할 수 없습니다: 터졌다"));
        var json = ApplyJson();
        var codes = json.GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ToList();
        Assert.Equal(["ImpactFailed"], codes);
        Assert.Equal(JsonValueKind.Null, json.GetProperty("impact").ValueKind);
    }

    [Fact]
    public async Task Impact_cancellation_propagates()
    {
        Spec(Group);
        _finder.Throw = new OperationCanceledException();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Run());
        Assert.Empty(_exec.Calls);
    }

    // ── SQL impact at the start of apply (V2-1d §7.1): only with a production connection ──

    private readonly List<string> _opened = [];
    private readonly FakeSqlFinder _sqlFinder = new();

    private static SqlOptions SqlConfig(string? prod) => new()
    {
        ConnectionString = "Server=dev-sql;Database=Shop;Integrated Security=true",
        AllowedHosts = ["dev-sql"],
        AllowedDatabases = ["Shop"],
        Production = new SqlProductionOptions { ConnectionString = prod, AllowedHosts = ["prod-sql"], AllowedDatabases = ["Shop"] },
    };

    private Task<int> RunSql(SqlOptions sql, ApplyOptions? o = null) =>
        new SpecRunner(_fs, _guard, _exec, _ui, _time, 160, groups: new PassingGroups(), impact: _finder,
                sqlImpact: new SqlImpactSetup(_sqlFinder, sql, cs => { _opened.Add(cs); return new NoReader(); }))
            .RunAsync(SpecPath, o ?? Yes, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Apply_without_prod_does_not_search_sql()
    {
        Spec("- [ ] T1 sql db/a.sql 바꾼다\n");
        _ui.IsInteractive = true;

        var code = await RunSql(SqlConfig(null), new ApplyOptions(false, false, false, false, false, null, false));

        Assert.Equal(0, code);
        Assert.Empty(_sqlFinder.Calls);
        Assert.Empty(_opened);
        Assert.Empty(_ui.Warnings);
        Assert.Equal(["T1"], _exec.Calls);
        var json = ApplyJson();
        Assert.False(json.TryGetProperty("sqlImpact", out _));
        Assert.Empty(json.GetProperty("warnings").EnumerateArray());
        var dir = Assert.Single(Directory.GetDirectories(P(".pelly/runs"), "apply-*"));
        Assert.False(File.Exists(Path.Combine(dir, "impact-sql.json")));
    }

    [Fact]
    public async Task Apply_with_prod_searches_sql_and_writes_list()
    {
        Spec("- [ ] T1 sql db/a.sql 바꾼다\n");
        _sqlFinder.Tables = [new SqlTableShare("T1", new SqlObjectName("dbo", "Orders"), ["Status"], 3, 1, 0, 0)];
        _sqlFinder.Targets = [new ImpactTarget("T1", "", "dbo.usp_Read", "같은 컬럼 읽음", ImpactTargetKind.SqlObject)];

        var code = await RunSql(SqlConfig("Server=prod-sql;Database=Shop;Integrated Security=true"));

        Assert.Equal(0, code);
        var call = Assert.Single(_sqlFinder.Calls);
        Assert.Equal(SqlImpactBasis.Prod, call.Basis);
        Assert.Equal(["Server=prod-sql;Database=Shop;Integrated Security=true"], _opened);
        Assert.Equal("dbo.usp_A", SqlImpactFinder.Display(Assert.Single(call.Seeds).Object));

        var dir = Assert.Single(Directory.GetDirectories(P(".pelly/runs"), "apply-*"));
        var id = Path.GetFileName(dir);
        var list = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "impact-sql.json"))).RootElement;
        Assert.Equal("dbo.Orders", list.GetProperty("tables")[0].GetProperty("table").GetString());

        var json = ApplyJson();
        var sql = json.GetProperty("sqlImpact");
        Assert.Equal("prod", sql.GetProperty("basis").GetString());
        Assert.Equal(1, sql.GetProperty("seeds").GetInt32());
        Assert.Equal(2, sql.GetProperty("warnings").GetInt32());
        Assert.Equal($".pelly/runs/{id}/impact-sql.json", sql.GetProperty("listPath").GetString());
        Assert.Contains(_ui.Warnings, w => w.EndsWith($"전체 목록: .pelly/runs/{id}/impact-sql.json"));
        Assert.Contains(_ui.Warnings, w => w.Contains("dbo.usp_Read (같은 컬럼 읽음) — pelly sql pull dbo.usp_Read"));
    }

    [Fact]
    public async Task Apply_sql_list_exists_when_the_warning_question_is_asked()
    {
        Spec("- [ ] T1 sql db/a.sql 바꾼다\n");
        _sqlFinder.Tables = [new SqlTableShare("T1", new SqlObjectName("dbo", "Orders"), ["Status"], 3, 1, 0, 0)];
        _ui.IsInteractive = true;
        _ui.ConfirmAnswers.Enqueue(false);

        var code = await RunSql(SqlConfig("Server=prod-sql;Database=Shop;Integrated Security=true"), new ApplyOptions(false, false, false, false, false, null, false));

        Assert.Equal(2, code);
        Assert.Empty(_exec.Calls);
        var dir = Assert.Single(Directory.GetDirectories(P(".pelly/runs"), "apply-*"));
        Assert.True(File.Exists(Path.Combine(dir, "impact-sql.json")));
    }

    [Fact]
    public async Task Apply_sql_failure_never_blocks()
    {
        Spec("- [ ] T1 sql db/a.sql 바꾼다\n");
        _sqlFinder.Throw = new InvalidOperationException("SQL 터졌다");

        var code = await RunSql(SqlConfig("Server=prod-sql;Database=Shop;Integrated Security=true"));

        Assert.Equal(0, code);
        Assert.Equal(["T1"], _exec.Calls);
        Assert.Contains(_ui.Warnings, w => w.Contains("SQL 터졌다"));
        var codes = ApplyJson().GetProperty("warnings").EnumerateArray().Select(w => w.GetProperty("code").GetString()).ToList();
        Assert.Equal(["ImpactFailed"], codes);
    }

    private sealed class FakeSqlFinder : ISqlImpactFinder
    {
        public List<(IReadOnlyList<SqlImpactSeed> Seeds, SqlReaderOpen Db, SqlImpactBasis Basis)> Calls { get; } = [];
        public IReadOnlyList<ImpactTarget> Targets { get; set; } = [];
        public IReadOnlyList<SqlTableShare> Tables { get; set; } = [];
        public Exception? Throw { get; set; }

        public Task<SqlImpactResult> FindAsync(Workspace ws, IReadOnlyList<SqlImpactSeed> seeds, SqlReaderOpen db, SqlImpactBasis basis, CancellationToken ct)
        {
            Calls.Add((seeds, db, basis));
            if (Throw is not null) throw Throw;
            return Task.FromResult(new SqlImpactResult(basis, null, Targets, Tables, [], false, new Dictionary<string, IReadOnlyList<SqlImpactModuleRow>>()));
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

    private sealed class FakeFinder : IImpactFinder
    {
        public List<IReadOnlyList<ImpactSeed>> Calls { get; } = [];
        public ImpactResult Result { get; set; } = new(ImpactMode.Semantic, null, [], []);
        public Exception? Throw { get; set; }

        public Task<ImpactResult> FindAsync(Workspace ws, IReadOnlyList<ImpactSeed> seeds, CancellationToken ct)
        {
            Calls.Add(seeds);
            if (Throw is not null) throw Throw;
            return Task.FromResult(Result);
        }
    }

    private sealed class PassingGroups : IGroupVerifier
    {
        public Task<GroupBuild> BuildAsync(Workspace ws, IReadOnlyList<string> projects, CancellationToken ct) =>
            Task.FromResult(new GroupBuild(true, null, null, []));
    }

    /// <summary>Appends <c>// id</c> or <c>-- id</c> to the task's file and succeeds.</summary>
    private sealed class Executor(FileGuard guard) : ITaskExecutor
    {
        public List<string> Calls { get; } = [];

        public async Task<RunResult> ExecuteAsync(SpecTask task, Workspace ws, TaskRunOptions o, CancellationToken ct)
        {
            Calls.Add(task.Id);
            var path = Path.Combine(ws.Root, task.Path.Replace('/', Path.DirectorySeparatorChar));
            var original = guard.Read(path);
            var comment = task.Kind == TaskKind.Sql ? "-- " : "// ";
            var written = await guard.WriteAsync(original, original.Text + comment + task.Id + "\n", CancellationToken.None);
            return new RunResult(0, "R-" + task.Id, path, original, written.Snapshot, null);
        }
    }
}
