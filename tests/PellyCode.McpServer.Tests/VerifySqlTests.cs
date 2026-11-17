using System.Runtime.CompilerServices;
using PellyCode.Core.Abstractions;
using PellyCode.Core.Sql;
using Xunit.v3;
using static PellyCode.McpServer.Tests.VerifyHarness;

namespace PellyCode.McpServer.Tests;

/// <summary>
/// pelly_verify on changed <c>.sql</c> files: each object batch compiled under NOEXEC against its HEAD batch (paired by object
/// name), only new errors reported. A fake executor stands in for the server except in the <see cref="SqlServerFactAttribute"/> test.
/// </summary>
public sealed class VerifySqlTests : IDisposable
{
    private const string Cancel = "Sql/objects/usp_CancelOrder.sql";
    private const string Connection = "Server=devsql;Database=Shop;Integrated Security=true";

    private readonly VerifyHarness _repo = new("Sql");
    private readonly FakeSql _sql = new();
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _repo.Dispose();

    private Core.Config.PellyOptions Allowed() => _repo.Options(o =>
    {
        o.Sql.ConnectionString = Connection;
        o.Sql.AllowedHosts = ["devsql"];
        o.Sql.AllowedDatabases = ["Shop"];
    });

    private Task<string> VerifyAsync(Core.Config.PellyOptions options) =>
        _repo.Service(options, cs => { _sql.ConnectionStrings.Add(cs); return _sql; }).VerifyAsync(null, Ct);

    private static string Break(string text) => text.Replace("SELECT @Status = Status", "SELECT @Status = MISSING_COL", StringComparison.Ordinal);

    [Fact]
    public async Task Sql_NewCompileError_IsError()
    {
        _repo.Edit(Cancel, Break);

        var text = await VerifyAsync(Allowed());

        var lines = Lines(text);
        Assert.Equal("결과: 새 오류 1건, 새 경고 0건", lines[0]);
        Assert.Contains($"[SQL] {Cancel} — 새 오류 1건", lines);
        Assert.Contains($"- {Cancel}:18 Msg207 Invalid column name 'MISSING_COL'.", lines);
        Assert.Equal([Connection], _sql.ConnectionStrings);
    }

    [Fact]
    public async Task Sql_ErrorAlreadyInHead_NotReported()
    {
        // HEAD holds the error with LF line ends; the working tree keeps it with CRLF and a new comment line before it.
        _repo.Edit(Cancel, t => Break(t).Replace("\r\n", "\n", StringComparison.Ordinal));
        _repo.Commit();
        _repo.Edit(Cancel, t => t.Replace("\n", "\r\n", StringComparison.Ordinal)
            .Replace("    SET NOCOUNT ON;", "    SET NOCOUNT ON;\r\n    -- 상태 확인", StringComparison.Ordinal));

        var text = await VerifyAsync(Allowed());

        var lines = Lines(text);
        Assert.Equal("결과: 새 문제 없음", lines[0]);
        Assert.Contains($"[SQL] {Cancel} — 통과", lines);
        Assert.Equal(2, _sql.Compiled.Count(b => b.Contains("MISSING_COL", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Sql_NoConnection_Skipped()
    {
        _repo.Edit(Cancel, Break);

        var text = await VerifyAsync(_repo.Options());

        var lines = Lines(text);
        Assert.Equal("결과: 새 문제 없음", lines[0]);
        Assert.Contains("[건너뜀] SQL 컴파일 검사 — SQL 연결이 없습니다", lines);
        Assert.Empty(_sql.ConnectionStrings);
    }

    [Fact]
    public async Task Sql_NotAllowListed_Skipped()
    {
        _repo.Edit(Cancel, Break);

        var text = await VerifyAsync(_repo.Options(o => o.Sql.ConnectionString = Connection));

        var lines = Lines(text);
        Assert.Equal("결과: 새 문제 없음", lines[0]);
        Assert.Contains("[건너뜀] SQL 컴파일 검사 — SQL 서버 'devsql'이(가) sql.allowedHosts에 없습니다 — 접속을 허용하려면 sql.allowedHosts에 추가하세요", lines);
        Assert.Empty(_sql.ConnectionStrings);
    }

    [Fact]
    public async Task Verify_RenamedSql_NoNewError()
    {
        const string Renamed = "Sql/objects/usp_CancelOrder_v2.sql";
        _repo.Edit(Cancel, Break);
        _repo.Commit();
        _repo.Git("mv", Cancel, Renamed);
        _repo.Edit(Renamed, t => t.Replace("-- 주문 취소", "-- 주문 취소 (v2)", StringComparison.Ordinal));

        var text = await VerifyAsync(Allowed());

        var lines = Lines(text);
        Assert.Equal("결과: 새 문제 없음", lines[0]);
        Assert.Contains($"[SQL] {Renamed} — 통과", lines);
        Assert.Equal(2, _sql.Compiled.Count);
    }

    [Fact]
    public async Task Sql_NewObjectInFile_AllErrorsNew()
    {
        // A batch HEAD did not have (no object of that name) has no baseline: every error is new.
        _repo.Edit(Cancel, t => t + "GO\r\nCREATE PROCEDURE dbo.usp_Extra AS SELECT MISSING_COL FROM dbo.Orders;\r\n");

        var lines = Lines(await VerifyAsync(Allowed()));

        Assert.Equal("결과: 새 오류 1건, 새 경고 0건", lines[0]);
        Assert.Contains(lines, l => l.StartsWith($"- {Cancel}:", StringComparison.Ordinal) && l.Contains("MISSING_COL", StringComparison.Ordinal));
    }

    [SqlServerFact]
    public async Task Sql_RealServer_NewCompileError_IsError()
    {
        var connection = Environment.GetEnvironmentVariable(SqlServerFactAttribute.Variable)!;
        var endpoint = SqlConnectionText.Endpoint(connection);
        _repo.Edit(Cancel, Break);
        var options = _repo.Options(o =>
        {
            o.Sql.ConnectionString = connection;
            o.Sql.AllowedHosts = [endpoint.DataSource];
            o.Sql.AllowedDatabases = [endpoint.Database!];
        });

        var lines = Lines(await _repo.Service(options, cs => new SqlServerExecutor(cs)).VerifyAsync(null, Ct));

        Assert.Equal("결과: 새 오류 1건, 새 경고 0건", lines[0]);
        Assert.Contains(lines, l => l.StartsWith($"- {Cancel}:", StringComparison.Ordinal) && l.Contains("MISSING_COL", StringComparison.Ordinal));
    }

    /// <summary>Compiles nothing: every batch holding MISSING_COL gets Msg 207 on the line where it appears (batch-relative).</summary>
    private sealed class FakeSql : ISqlExecutor
    {
        public List<string> ConnectionStrings { get; } = [];
        public List<string> Compiled { get; } = [];

        public Task<IReadOnlyList<SqlCompileError>> CompileWithNoExecAsync(string batch, SqlSessionSettings settings, CancellationToken ct)
        {
            Compiled.Add(batch);
            var lines = batch.Split('\n');
            var errors = new List<SqlCompileError>();
            for (var i = 0; i < lines.Length; i++)
                if (lines[i].Contains("MISSING_COL", StringComparison.Ordinal))
                    errors.Add(new SqlCompileError(207, i + 1, "Invalid column name 'MISSING_COL'."));
            return Task.FromResult<IReadOnlyList<SqlCompileError>>(errors);
        }

        public Task<SqlServerProbe> ProbeAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SqlObjectName>> FindMissingAsync(IReadOnlyList<SqlObjectName> names, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SqlTableSchema>> GetSchemasAsync(IReadOnlyList<SqlObjectName> names, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SqlObjectName>> GetReferencedEntitiesAsync(SqlObjectName obj, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<SqlProcedureSignature>> GetProcedureSignaturesAsync(IReadOnlyList<SqlObjectName> names, CancellationToken ct) => throw new NotSupportedException();
    }
}

/// <summary>
/// Runs only when <c>PELLY_TEST_SQL</c> holds a connection string, with the trait <c>Env=SqlServer</c> (the same rule as the
/// Integration tests' attribute of this name).
/// </summary>
public sealed class SqlServerFactAttribute : FactAttribute, ITraitAttribute
{
    public const string Variable = "PELLY_TEST_SQL";

    public SqlServerFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber) =>
        Skip = string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)) ? $"{Variable} 환경 변수 필요" : null;

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() => [new("Env", "SqlServer")];
}
