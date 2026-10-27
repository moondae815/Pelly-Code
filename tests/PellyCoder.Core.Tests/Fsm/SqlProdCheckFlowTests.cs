using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>The production-comparison step of the SQL pipeline (design 6.3): fake model, fake SQL executor, fake production reader.</summary>
public sealed class SqlProdCheckFlowTests : IDisposable
{
    private const string Conn = "Server=sql1;Database=Shop;User Id=u;Password=Secret123;";
    private const string Search = "    SELECT Id FROM dbo.T WHERE Id = @Id;";
    private const string Good = "    SELECT Id FROM dbo.T WHERE Id = @Id AND Id > 0;";
    private const string ProdDef = "CREATE PROCEDURE [dbo].[usp_P] @Id int\nAS\nBEGIN\n    SELECT Id FROM dbo.T;\nEND\n";
    private const string OlderDef = "CREATE PROCEDURE [dbo].[usp_P] @Id int\nAS\nBEGIN\n    SELECT 0;\nEND\n";
    private static readonly SqlEndpoint Endpoint = new("proddb01", "proddb01", "Shop");
    private static readonly SqlObjectName Name = new("dbo", "usp_P");

    private static readonly string Script =
        "SET ANSI_NULLS ON\nGO\nCREATE PROCEDURE dbo.usp_P @Id int\nAS\nBEGIN\n" + Search + "\nEND\nGO\nSELECT 1\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-sql-prod-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly FakeSqlExecutor _sql = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly PellyOptions _options = new()
    {
        Sql = new SqlOptions { AllowedHosts = ["sql1"], AllowedDatabases = ["Shop"], ConnectionString = Conn },
    };

    public SqlProdCheckFlowTests()
    {
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), "");
        _sql.Schemas.Add(new SqlTableSchema(SqlObjectName.Parse("dbo.T"), "TABLE", [new("Id", "int", false, true)], []));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string PathA => Path.Combine(_root, "a.sql");

    private string Hash(string d) => SqlDefinitionText.Hash(d, _options.Sql.CompatLevel);

    private static string Block(string search, string replace) => search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    private static string Record(string hash) =>
        $"-- pelly pull: source=prod server=proddb01 database=Shop object=[dbo].[usp_P] sha256={hash} at=2026-10-26T09:12:33Z\n";

    private string Prepare(string text)
    {
        File.WriteAllText(PathA, text, new UTF8Encoding(false));
        return File.ReadAllText(PathA);
    }

    private sealed class FakeReader(SqlModuleDefinition? result, bool unreachable = false) : ISqlDefinitionReader
    {
        public int Calls { get; private set; }

        public Task<SqlServerProbe> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<SqlModuleDefinition?> GetDefinitionAsync(SqlObjectName name, CancellationToken ct)
        {
            Calls++;
            if (unreachable) throw new SqlUnavailableException("서버에 연결할 수 없습니다");
            return Task.FromResult(result);
        }

        public Task<IReadOnlyList<SqlModuleRef>> GetReferencingModulesAsync(SqlObjectName name, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SqlColumnUse>?> GetColumnUsesAsync(SqlObjectName module, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetColumnsAsync(SqlObjectName table, CancellationToken ct) => throw new NotSupportedException();
    }

    private static SqlReaderOpen Open(string def = ProdDef, bool unreachable = false) =>
        new(new FakeReader(new SqlModuleDefinition(Name, "P", def, true, true), unreachable), Endpoint, null);

    private async Task<RunResult> Run(SqlReaderOpen? prod)
    {
        var request = new RunRequest(PathA, null, null, null, "add a filter", true, false, null, null, EditKind.Sql);
        return await FsmOrchestratorFactory.CreateSql(_fs, _backend, _ui, _options, TimeProvider.System, _sql, prod)
            .RunDetailedAsync(request, TestContext.Current.CancellationToken);
    }

    private JsonElement Summary()
    {
        var runDir = Directory.GetDirectories(Path.Combine(_root, ".pelly", "runs")).Single();
        return JsonDocument.Parse(File.ReadAllText(Path.Combine(runDir, "summary.json"))).RootElement;
    }

    [Fact]
    public async Task Changed_since_pull_stops_before_model()
    {
        var text = Prepare(Record(Hash(OlderDef)) + Script);
        _backend.Enqueue(Block(Search, Good));

        var result = await Run(Open());

        Assert.Equal(4, result.ExitCode);
        Assert.Equal("SqlProdChanged", Summary().GetProperty("failureKind").GetString());
        Assert.Empty(_backend.Requests);
        Assert.Equal(text, File.ReadAllText(PathA));
        Assert.Contains(_ui.Warnings, w => w.Contains("다시 받으십시오") && w.Contains("따로 보관"));
        Assert.Equal("ChangedSincePull", Summary().GetProperty("sqlProd").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Differs_without_record_warns_and_continues()
    {
        Prepare(Script);
        _backend.Enqueue(Block(Search, Good));

        var result = await Run(Open());

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(_ui.Warnings, w => w.Contains("파일의 dbo.usp_P가 운영 정의와 다릅니다"));
        Assert.Equal("DiffersNoRecord", Summary().GetProperty("sqlProd").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Unavailable_warns_and_continues()
    {
        Prepare(Script);
        _backend.Enqueue(Block(Search, Good));

        var result = await Run(Open(unreachable: true));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(_ui.Warnings, w => w.Contains("운영 정의와 비교하지 못했습니다"));
        Assert.Equal("Unavailable", Summary().GetProperty("sqlProd").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Not_in_prod_notes_and_continues()
    {
        Prepare(Script);
        _backend.Enqueue(Block(Search, Good));

        var result = await Run(new SqlReaderOpen(new FakeReader(null), Endpoint, null));

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(_ui.Warnings, w => w.Contains("운영에 dbo.usp_P가 없습니다"));
    }

    [Fact]
    public async Task Same_as_prod_with_stale_record_notes_pull_and_continues()
    {
        // pull → edit → deploy in SSMS → edit again: the file is production now, the record is older.
        var deployed = "CREATE PROCEDURE dbo.usp_P @Id int\nAS\nBEGIN\n" + Search + "\nEND\n";
        Prepare(Record(Hash(OlderDef)) + Script);
        _backend.Enqueue(Block(Search, Good));

        var result = await Run(Open(deployed));

        Assert.Equal(0, result.ExitCode);
        Assert.Single(_backend.Requests);
        Assert.Contains(_ui.Warnings, w => w == "받은 기록이 운영보다 오래되었습니다 — 고치기 전에 pelly sql pull dbo.usp_P --force로 다시 받으십시오");
        Assert.Equal("SameAsProd", Summary().GetProperty("sqlProd").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Same_as_prod_with_current_record_is_silent()
    {
        var deployed = "CREATE PROCEDURE dbo.usp_P @Id int\nAS\nBEGIN\n" + Search + "\nEND\n";
        Prepare(Record(Hash(deployed)) + Script);
        _backend.Enqueue(Block(Search, Good));

        var result = await Run(Open(deployed));

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(_ui.Warnings);
        Assert.Equal("SameAsProd", Summary().GetProperty("sqlProd").GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Not_configured_is_silent_and_records_nothing()
    {
        Prepare(Script);
        _backend.Enqueue(Block(Search, Good));

        var result = await Run(null);

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(_ui.Warnings);
        Assert.False(Summary().TryGetProperty("sqlProd", out var p) && p.ValueKind != JsonValueKind.Null);
    }

    [Fact]
    public async Task Summary_records_outcome_and_hashes()
    {
        Prepare(Record(Hash(ProdDef)) + Script);
        _backend.Enqueue(Block(Search, Good));

        var result = await Run(Open());

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(_ui.Warnings);
        var p = Summary().GetProperty("sqlProd");
        Assert.Equal("UnchangedSincePull", p.GetProperty("outcome").GetString());
        Assert.Equal(Hash(ProdDef), p.GetProperty("prodHash").GetString());
        Assert.Equal(Hash(ProdDef), p.GetProperty("pulledHash").GetString());
        Assert.False(string.IsNullOrEmpty(p.GetProperty("fileHash").GetString()));
    }
}
