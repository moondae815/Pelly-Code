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

    private static string Record(string hash, string at = "2026-10-26T09:12:33Z", string source = "prod") =>
        $"-- pelly pull: source={source} server=proddb01 database=Shop object=[dbo].[usp_P] sha256={hash} at={at}\n";

    /// <summary>The time the refreshed record is stamped with; every other clock use (run id, timings) sees it too.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 1, 2, 3, TimeSpan.Zero);

    private sealed class FixedNow(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    /// <summary>The user's deploy of the first edit: production now holds the file's target batch.</summary>
    private const string Deployed = "CREATE PROCEDURE dbo.usp_P @Id int\nAS\nBEGIN\n" + Search + "\nEND\n";

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

    private async Task<RunResult> Run(SqlReaderOpen? prod, bool dryRun = false, bool yes = true)
    {
        var request = new RunRequest(PathA, null, null, null, "add a filter", yes, dryRun, null, null, EditKind.Sql);
        return await FsmOrchestratorFactory.CreateSql(_fs, _backend, _ui, _options, new FixedNow(Now), _sql, prod)
            .RunDetailedAsync(request, TestContext.Current.CancellationToken);
    }

    private const string RefreshNotice = "받은 기록을 운영 정의로 갱신합니다 (이 파일은 이미 운영에 반영되어 있습니다)";

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
    public async Task Same_as_prod_with_stale_record_refreshes_the_record_in_the_written_file()
    {
        // pull → edit → deploy in SSMS → edit again: the file is production now, the record is older.
        Prepare(Record(Hash(OlderDef)) + Script);
        _backend.Enqueue(Block(Search, Good));

        var result = await Run(Open(Deployed));

        Assert.Equal(0, result.ExitCode);
        Assert.Single(_backend.Requests);
        Assert.Contains(_ui.Warnings, w => w == RefreshNotice);
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("다시 받으십시오"));
        var written = File.ReadAllText(PathA);
        Assert.Equal(Record(Hash(Deployed), "2026-10-27T01:02:03Z") + Script.Replace(Search, Good), written);
        var p = Summary().GetProperty("sqlProd");
        Assert.Equal("SameAsProd", p.GetProperty("outcome").GetString());
        Assert.True(p.GetProperty("recordRefreshed").GetBoolean());

        // The next edit of this file is no longer a false "changed since pull".
        var again = written.Replace(Good, "    SELECT Id FROM dbo.T WHERE Id = @Id AND Id > 1;");
        var target = SqlBatchSplitter.Split(again, _options.Sql.CompatLevel).ObjectBatches[0];
        var next = await SqlProdComparer.CompareAsync(again, target, Name, Open(Deployed), _options.Sql.CompatLevel, TestContext.Current.CancellationToken);
        Assert.Equal(ProdCheckOutcome.UnchangedSincePull, next.Outcome);
    }

    [Fact]
    public async Task Refresh_keeps_the_dev_differs_line()
    {
        const string devLine = "-- pelly pull: dev differs (sha256=1234)\n";
        Prepare(Record(Hash(OlderDef)) + devLine + Script);
        _backend.Enqueue(Block(Search, Good));

        var result = await Run(Open(Deployed));

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Record(Hash(Deployed), "2026-10-27T01:02:03Z") + devLine + Script.Replace(Search, Good), File.ReadAllText(PathA));
    }

    [Fact]
    public async Task Refresh_keeps_crlf_and_bom()
    {
        var text = (Record(Hash(OlderDef)) + Script).Replace("\n", "\r\n");
        File.WriteAllText(PathA, text, new UTF8Encoding(true));
        _backend.Enqueue(Block(Search, Good));

        var result = await Run(Open(Deployed));

        Assert.Equal(0, result.ExitCode);
        var bytes = File.ReadAllBytes(PathA);
        Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes[..3]);
        var expected = (Record(Hash(Deployed), "2026-10-27T01:02:03Z") + Script.Replace(Search, Good)).Replace("\n", "\r\n");
        Assert.Equal(expected, new UTF8Encoding(false).GetString(bytes[3..]));
    }

    [Fact]
    public async Task Refresh_in_dry_run_writes_nothing()
    {
        var text = Prepare(Record(Hash(OlderDef)) + Script);
        _backend.Enqueue(Block(Search, Good));

        var result = await Run(Open(Deployed), dryRun: true);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(text, File.ReadAllText(PathA));
        // The diff shows what a real run would write, the refreshed record included.
        Assert.StartsWith(Record(Hash(Deployed), "2026-10-27T01:02:03Z"), Assert.Single(_ui.Diffs).After);
        Assert.False(Summary().GetProperty("sqlProd").GetProperty("recordRefreshed").GetBoolean());
    }

    [Fact]
    public async Task Rejected_review_restores_the_old_record()
    {
        var text = Prepare(Record(Hash(OlderDef)) + Script);
        _backend.Enqueue(Block(Search, Good));

        var result = await Run(Open(Deployed), yes: false);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(text, File.ReadAllText(PathA));
        Assert.False(Summary().GetProperty("sqlProd").GetProperty("recordRefreshed").GetBoolean());
    }

    [Theory]
    [InlineData("current")]
    [InlineData("dev")]
    [InlineData("absent")]
    public async Task Same_as_prod_without_a_stale_valid_record_keeps_the_first_line(string record)
    {
        var first = record switch
        {
            "current" => Record(Hash(Deployed)),
            "dev" => Record(Hash(OlderDef), source: "dev"),
            _ => "",
        };
        Prepare(first + Script);
        _backend.Enqueue(Block(Search, Good));

        var result = await Run(Open(Deployed));

        Assert.Equal(0, result.ExitCode);
        Assert.Empty(_ui.Warnings);
        Assert.Equal(first + Script.Replace(Search, Good), File.ReadAllText(PathA));
        var p = Summary().GetProperty("sqlProd");
        Assert.Equal("SameAsProd", p.GetProperty("outcome").GetString());
        Assert.False(p.GetProperty("recordRefreshed").GetBoolean());
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
