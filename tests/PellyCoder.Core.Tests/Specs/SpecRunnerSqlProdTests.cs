using System.IO.Abstractions;
using System.Text;
using Microsoft.Extensions.Time.Testing;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Specs;

/// <summary>An apply sql task goes through the same SQL pipeline: a production change since the pull fails the task with its kind.</summary>
public sealed class SpecRunnerSqlProdTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pelly-apply-prod-").FullName;
    private readonly FileSystem _fs = new();

    public void Dispose() => Directory.Delete(_root, true);

    private sealed class Reader(string def) : ISqlDefinitionReader
    {
        public Task<SqlServerProbe> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<SqlModuleDefinition?> GetDefinitionAsync(SqlObjectName name, CancellationToken ct) =>
            Task.FromResult<SqlModuleDefinition?>(new SqlModuleDefinition(name, "P", def, true, true));
        public Task<IReadOnlyList<SqlModuleRef>> GetReferencingModulesAsync(SqlObjectName name, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SqlColumnUse>?> GetColumnUsesAsync(SqlObjectName module, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetColumnsAsync(SqlObjectName table, CancellationToken ct) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Apply_sql_task_prod_changed_fails_task()
    {
        var options = new PellyOptions
        {
            Sql = new SqlOptions { AllowedHosts = ["sql1"], AllowedDatabases = ["Shop"], ConnectionString = "Server=sql1;Database=Shop;User Id=u;Password=Secret123;" },
        };
        const string prodDef = "CREATE PROCEDURE [dbo].[usp_P]\nAS\nSELECT 2\n";
        var oldHash = SqlDefinitionText.Hash("CREATE PROCEDURE [dbo].[usp_P]\nAS\nSELECT 1\n", options.Sql.CompatLevel);
        var file = $"-- pelly pull: source=prod server=proddb01 database=Shop object=[dbo].[usp_P] sha256={oldHash} at=2026-10-26T09:12:33Z\n"
            + "CREATE PROCEDURE dbo.usp_P\nAS\nSELECT 3\n";
        File.WriteAllText(Path.Combine(_root, "App.sln"), "");
        Directory.CreateDirectory(Path.Combine(_root, "db"));
        Directory.CreateDirectory(Path.Combine(_root, "changes"));
        File.WriteAllText(Path.Combine(_root, "db", "p.sql"), file, new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(_root, "changes", "x.md"),
            "---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n- AC1: 하나\n\n## 작업\n- [ ] T1 sql db/p.sql 첫째\n");

        var backend = new ScriptedLlmBackend();
        var ui = new ScriptedUserInteraction { IsInteractive = true };
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 24, 9, 30, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var prod = new SqlReaderOpen(new Reader(prodDef), new SqlEndpoint("proddb01", "proddb01", "Shop"), null);
        var exec = new FsmTaskExecutor((r, ct) => FsmOrchestratorFactory
            .CreateSql(_fs, backend, ui, options, TimeProvider.System, new FakeSqlExecutor(), prod).RunDetailedAsync(r, ct));
        var runner = new SpecRunner(_fs, new FileGuard(_fs, TimeProvider.System), exec, ui, time, 160, null);

        var code = await runner.RunAsync(Path.Combine(_root, "changes", "x.md"),
            new ApplyOptions(false, false, false, true, false, null, false), TestContext.Current.CancellationToken);

        Assert.Equal(4, code);
        Assert.Empty(backend.Requests);
        Assert.Equal(file, File.ReadAllText(Path.Combine(_root, "db", "p.sql")));
        Assert.Contains("<!-- failed: ", File.ReadAllText(Path.Combine(_root, "changes", "x.md")));
        Assert.Contains("SqlProdChanged", File.ReadAllText(Path.Combine(_root, "changes", "x.md")));
        var applyJson = File.ReadAllText(Directory.GetFiles(Path.Combine(_root, ".pelly", "runs"), "apply.json", SearchOption.AllDirectories).Single());
        Assert.Contains("SqlProdChanged", applyJson);
    }
}
