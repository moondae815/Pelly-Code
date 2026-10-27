using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Sql;

public class SqlReadersTests
{
    private sealed class FakeReader(string cs) : ISqlDefinitionReader
    {
        public string ConnectionString { get; } = cs;
        public Task<SqlServerProbe> ProbeAsync(CancellationToken ct) => throw new InvalidOperationException("must not connect");
        public Task<SqlModuleDefinition?> GetDefinitionAsync(SqlObjectName name, CancellationToken ct) => throw new InvalidOperationException("must not connect");
        public Task<IReadOnlyList<SqlModuleRef>> GetReferencingModulesAsync(SqlObjectName name, CancellationToken ct) => throw new InvalidOperationException("must not connect");
        public Task<IReadOnlyList<SqlColumnUse>?> GetColumnUsesAsync(SqlObjectName module, CancellationToken ct) => throw new InvalidOperationException("must not connect");
        public Task<IReadOnlyList<string>> GetColumnsAsync(SqlObjectName table, CancellationToken ct) => throw new InvalidOperationException("must not connect");
    }

    private static SqlOptions Options(string? prodCs, string[]? hosts = null, string[]? dbs = null) => new()
    {
        Production = new SqlProductionOptions
        {
            ConnectionString = prodCs,
            AllowedHosts = (hosts ?? ["prod-sql"]).ToList(),
            AllowedDatabases = (dbs ?? ["Shop"]).ToList(),
        },
    };

    [Fact]
    public void Production_not_configured_is_all_null()
    {
        var open = SqlReaders.Production(new SqlOptions(), cs => new FakeReader(cs));
        Assert.Null(open.Reader);
        Assert.Null(open.Endpoint);
        Assert.Null(open.Problem);
    }

    [Fact]
    public void Production_outside_allow_list_has_problem()
    {
        var open = SqlReaders.Production(Options("Server=other;Database=Shop;Integrated Security=true"), cs => new FakeReader(cs));
        Assert.Null(open.Reader);
        Assert.Contains("sql.production.allowedHosts", open.Problem);
    }

    [Fact]
    public void Production_without_database_has_problem()
    {
        var open = SqlReaders.Production(Options("Server=prod-sql;Integrated Security=true"), cs => new FakeReader(cs));
        Assert.Null(open.Reader);
        Assert.NotNull(open.Problem);
        Assert.Contains("Database", open.Problem);
    }

    [Fact]
    public void Production_allowed_creates_reader_without_connecting()
    {
        const string cs = "Server=prod-sql;Database=Shop;Integrated Security=true";
        var open = SqlReaders.Production(Options(cs), c => new FakeReader(c));
        Assert.Null(open.Problem);
        Assert.Equal(cs, Assert.IsType<FakeReader>(open.Reader).ConnectionString);
        Assert.Equal("Shop", open.Endpoint!.Database);
    }

    [Fact]
    public void Development_uses_dev_allow_list_and_reports_missing_connection()
    {
        var o = new SqlOptions { AllowedHosts = ["dev-sql"], AllowedDatabases = ["Shop"] };
        Assert.NotNull(SqlReaders.Development(o, cs => new FakeReader(cs)).Problem);

        o.ConnectionString = "Server=dev-sql;Database=Shop;Integrated Security=true";
        Assert.NotNull(SqlReaders.Development(o, cs => new FakeReader(cs)).Reader);

        o.ConnectionString = "Server=elsewhere;Database=Shop;Integrated Security=true";
        Assert.Contains("sql.allowedHosts", SqlReaders.Development(o, cs => new FakeReader(cs)).Problem);
    }
}
