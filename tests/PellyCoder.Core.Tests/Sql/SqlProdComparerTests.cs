using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Sql;

public class SqlProdComparerTests
{
    private const int Compat = 160;
    private const string ProdDef = "CREATE PROCEDURE [dbo].[usp_X]\nAS\nSELECT 1\n";
    private const string OldDef = "CREATE PROCEDURE [dbo].[usp_X]\nAS\nSELECT 0\n";
    private static readonly SqlEndpoint Endpoint = new("proddb01", "proddb01", "Shop");
    private static readonly SqlObjectName Name = new("dbo", "usp_X");

    private sealed class FakeReader : ISqlDefinitionReader
    {
        public int Calls { get; private set; }
        public SqlModuleDefinition? Result { get; set; }
        public bool Unreachable { get; set; }

        public Task<SqlServerProbe> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();

        public Task<SqlModuleDefinition?> GetDefinitionAsync(SqlObjectName name, CancellationToken ct)
        {
            Calls++;
            if (Unreachable) throw new SqlUnavailableException("서버에 연결할 수 없습니다");
            return Task.FromResult(Result);
        }

        public Task<IReadOnlyList<SqlModuleRef>> GetReferencingModulesAsync(SqlObjectName name, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SqlColumnUse>?> GetColumnUsesAsync(SqlObjectName module, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetColumnsAsync(SqlObjectName table, CancellationToken ct) => throw new NotSupportedException();
    }

    private static FakeReader ProdWith(string? definition = ProdDef) => new()
    {
        Result = new SqlModuleDefinition(Name, "P", definition, true, true),
    };

    private static SqlBatch Batch(string text) => new(0, 1, 3, text, null, Name, null, []);

    private static string Hash(string d) => SqlDefinitionText.Hash(d, Compat);

    private static string Record(string hash, string source = "prod", string server = "proddb01", string db = "Shop", string obj = "[dbo].[usp_X]") =>
        $"-- pelly pull: source={source} server={server} database={db} object={obj} sha256={hash} at=2026-10-26T09:12:33Z\n";

    private static Task<ProdCheck> Run(string file, SqlBatch target, SqlReaderOpen open) =>
        SqlProdComparer.CompareAsync(file, target, Name, open, Compat, CancellationToken.None);

    private static string AlterProd => ProdDef.Replace("CREATE", "ALTER");
    private static string Edited => "ALTER PROCEDURE [dbo].[usp_X]\nAS\nSELECT 2\n";

    [Fact]
    public async Task Not_configured_returns_NotConfigured_without_calls()
    {
        var r = await Run("x", Batch(Edited), new SqlReaderOpen(null, null, null));
        Assert.Equal(ProdCheckOutcome.NotConfigured, r.Outcome);
        Assert.Null(r.FileHash);
    }

    [Fact]
    public async Task Same_as_prod_wins_over_stale_record()
    {
        var reader = ProdWith();
        var file = Record(Hash(OldDef)) + AlterProd;
        var r = await Run(file, Batch(AlterProd), new SqlReaderOpen(reader, Endpoint, null));
        Assert.Equal(ProdCheckOutcome.SameAsProd, r.Outcome);
        Assert.Equal(r.FileHash, r.ProdHash);
    }

    [Fact]
    public async Task Crlf_resaved_file_is_same_as_prod()
    {
        var text = AlterProd.Replace("\n", "  \r\n");
        var r = await Run(text, Batch(text), new SqlReaderOpen(ProdWith(), Endpoint, null));
        Assert.Equal(ProdCheckOutcome.SameAsProd, r.Outcome);
    }

    [Fact]
    public async Task Unchanged_since_pull()
    {
        var file = Record(Hash(ProdDef)) + Edited;
        var r = await Run(file, Batch(Edited), new SqlReaderOpen(ProdWith(), Endpoint, null));
        Assert.Equal(ProdCheckOutcome.UnchangedSincePull, r.Outcome);
        Assert.Equal(Hash(ProdDef), r.PulledHash);
    }

    [Fact]
    public async Task Changed_since_pull()
    {
        var file = Record(Hash(OldDef)) + Edited;
        var r = await Run(file, Batch(Edited), new SqlReaderOpen(ProdWith(), Endpoint, null));
        Assert.Equal(ProdCheckOutcome.ChangedSincePull, r.Outcome);
        Assert.Equal(
            "받은 뒤 운영에서 dbo.usp_X가 바뀌었습니다 (받은 시각 2026-10-26T09:12:33Z). 운영 변경을 덮어쓰지 않도록 다시 받으십시오: pelly sql pull dbo.usp_X --force. 이미 고친 내용이 있으면 다시 받기 전에 파일을 따로 보관하십시오.",
            r.Message);
    }

    [Theory]
    [InlineData("dev", "proddb01", "Shop", "[dbo].[usp_X]")]
    [InlineData("prod", "otherdb", "Shop", "[dbo].[usp_X]")]
    [InlineData("prod", "proddb01", "Other", "[dbo].[usp_X]")]
    [InlineData("prod", "proddb01", "Shop", "[dbo].[usp_Y]")]
    public async Task Invalid_record_is_DiffersNoRecord(string source, string server, string db, string obj)
    {
        var file = Record(Hash(ProdDef), source, server, db, obj) + Edited;
        var r = await Run(file, Batch(Edited), new SqlReaderOpen(ProdWith(), Endpoint, null));
        Assert.Equal(ProdCheckOutcome.DiffersNoRecord, r.Outcome);
        Assert.Equal("파일의 dbo.usp_X가 운영 정의와 다릅니다", r.Message);
    }

    [Fact]
    public async Task No_record_is_DiffersNoRecord()
    {
        var r = await Run(Edited, Batch(Edited), new SqlReaderOpen(ProdWith(), Endpoint, null));
        Assert.Equal(ProdCheckOutcome.DiffersNoRecord, r.Outcome);
    }

    [Fact]
    public async Task Record_server_and_object_match_ignoring_case_and_default_schema()
    {
        var file = Record(Hash(ProdDef), server: "PRODDB01", db: "shop", obj: "[DBO].[USP_X]") + Edited;
        var r = await Run(file, Batch(Edited), new SqlReaderOpen(ProdWith(), Endpoint, null));
        Assert.Equal(ProdCheckOutcome.UnchangedSincePull, r.Outcome);
    }

    [Fact]
    public async Task Missing_in_prod_is_NotInProd()
    {
        var reader = new FakeReader { Result = null };
        var r = await Run("x", Batch(Edited), new SqlReaderOpen(reader, Endpoint, null));
        Assert.Equal(ProdCheckOutcome.NotInProd, r.Outcome);
        Assert.Equal("운영에 dbo.usp_X가 없습니다 (새 객체)", r.Message);
    }

    [Fact]
    public async Task Unreachable_is_Unavailable()
    {
        var reader = new FakeReader { Unreachable = true };
        var r = await Run("x", Batch(Edited), new SqlReaderOpen(reader, Endpoint, null));
        Assert.Equal(ProdCheckOutcome.Unavailable, r.Outcome);
        Assert.StartsWith("운영 정의와 비교하지 못했습니다: ", r.Message);
        Assert.Contains("서버에 연결할 수 없습니다", r.Message);
    }

    [Fact]
    public async Task Problem_from_policy_is_Unavailable()
    {
        var reader = new FakeReader();
        var r = await Run("x", Batch(Edited), new SqlReaderOpen(null, null, "SQL 서버 'x'이(가) 없습니다"));
        Assert.Equal(ProdCheckOutcome.Unavailable, r.Outcome);
        Assert.Equal("운영 정의와 비교하지 못했습니다: SQL 서버 'x'이(가) 없습니다", r.Message);
        Assert.Equal(0, reader.Calls);
    }

    [Fact]
    public async Task Null_definition_is_Unavailable()
    {
        var r = await Run("x", Batch(Edited), new SqlReaderOpen(ProdWith(null), Endpoint, null));
        Assert.Equal(ProdCheckOutcome.Unavailable, r.Outcome);
        Assert.Contains("정의를 읽을 수 없습니다", r.Message);
    }

    [Fact]
    public async Task Cancellation_propagates()
    {
        var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        var reader = new CancelReader();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            SqlProdComparer.CompareAsync("x", Batch(Edited), Name, new SqlReaderOpen(reader, Endpoint, null), Compat, cts.Token));
    }

    private sealed class CancelReader : ISqlDefinitionReader
    {
        public Task<SqlServerProbe> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<SqlModuleDefinition?> GetDefinitionAsync(SqlObjectName name, CancellationToken ct) { ct.ThrowIfCancellationRequested(); return Task.FromResult<SqlModuleDefinition?>(null); }
        public Task<IReadOnlyList<SqlModuleRef>> GetReferencingModulesAsync(SqlObjectName name, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SqlColumnUse>?> GetColumnUsesAsync(SqlObjectName module, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> GetColumnsAsync(SqlObjectName table, CancellationToken ct) => throw new NotSupportedException();
    }
}
