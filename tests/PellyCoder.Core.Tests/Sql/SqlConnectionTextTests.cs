using PellyCoder.Core.Config;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Tests.Sql;

public class SqlConnectionTextTests
{
    [Fact]
    public void Mask_HidesPassword()
    {
        foreach (var cs in new[]
        {
            "Server=x;Database=d;User Id=sa;Password=Secr3t!",
            "Server=x;Database=d;User Id=sa;pwd=Secr3t!",
            "Server=x;Database=d;User Id=sa;Password=a;b",
            "Server=x;Database=d;User Id=sa;Password=\"a;b\"",
        })
        {
            var masked = SqlConnectionText.Mask(cs);
            Assert.DoesNotContain("Secr3t", masked);
            Assert.DoesNotContain("a;b", masked);
            Assert.DoesNotContain("=a;", masked);
        }
        Assert.Contains("server=x", SqlConnectionText.Mask("Server=x;Password=p"));
        Assert.Contains("***", SqlConnectionText.Mask("Server=x;Password=p"));
    }

    [Fact]
    public void HasPassword_DetectsKeysCaseInsensitively()
    {
        Assert.True(SqlConnectionText.HasPassword("Server=x;PWD=p"));
        Assert.True(SqlConnectionText.HasPassword("Server=x;password=p"));
        Assert.False(SqlConnectionText.HasPassword("Server=x;Integrated Security=true"));
    }

    [Theory]
    [InlineData("Server=tcp:db1,1433;Database=d", "db1", "db1", "d")]
    [InlineData("Data Source=localhost,14333;Initial Catalog=x", "localhost", "localhost", "x")]
    [InlineData(@"Server=db1\SQLEXPRESS", @"db1\SQLEXPRESS", "db1", null)]
    public void Endpoint_NormalizesHost(string cs, string expectedDs, string host, string? db)
    {
        var e = SqlConnectionText.Endpoint(cs);
        Assert.Equal(host, e.Host);
        Assert.Equal(db, e.Database);
        Assert.Equal(expectedDs, e.DataSource);
    }

    [Fact]
    public void Endpoint_InstanceKeptInDataSource()
    {
        var e = SqlConnectionText.Endpoint(@"Server=db1\SQLEXPRESS;Database=d");
        Assert.Equal(@"db1\SQLEXPRESS", e.DataSource);
        Assert.Equal("db1", e.Host);
    }

    [Fact]
    public void Policy_EmptyLists_RejectWithHint()
    {
        var c = SqlTargetPolicy.Check(new SqlEndpoint("db1", "db1", "d"), new SqlOptions());
        Assert.False(c.Allowed);
        Assert.Equal("SQL 서버 'db1'이(가) sql.allowedHosts에 없습니다 — 접속을 허용하려면 sql.allowedHosts에 추가하세요", c.Message);
    }

    [Fact]
    public void Policy_HostMatchesByHostOrDataSource_IgnoresCase()
    {
        var ep = new SqlEndpoint(@"DB1\INST", "DB1", "Dev");
        var byHost = new SqlOptions { AllowedHosts = { "db1" }, AllowedDatabases = { "dev" } };
        var byDs = new SqlOptions { AllowedHosts = { @"db1\inst" }, AllowedDatabases = { "DEV" } };
        Assert.True(SqlTargetPolicy.Check(ep, byHost).Allowed);
        Assert.True(SqlTargetPolicy.Check(ep, byDs).Allowed);
    }

    [Fact]
    public void Policy_DatabaseNotAllowed_Rejects()
    {
        var o = new SqlOptions { AllowedHosts = { "db1" }, AllowedDatabases = { "dev" } };
        var c = SqlTargetPolicy.Check(new SqlEndpoint("db1", "db1", "prod"), o);
        Assert.False(c.Allowed);
        Assert.Equal("데이터베이스 'prod'이(가) sql.allowedDatabases에 없습니다 — 접속을 허용하려면 sql.allowedDatabases에 추가하세요", c.Message);
    }

    private static readonly SqlOptions Open = new() { AllowedHosts = { "allowed" }, AllowedDatabases = { "d" } };
    private const string Multi = "연결 문자열에 서버(또는 데이터베이스)가 여러 번 지정되어 있습니다 — 하나만 지정하세요";

    [Theory]
    [InlineData("Server=allowed;Data Source=evil;Database=d")]
    [InlineData("Data Source=evil;Server=allowed;Database=d")]
    [InlineData("Addr=allowed;Network Address=evil;Database=d")]
    [InlineData("Server=allowed;Database=d;Initial Catalog=evil")]
    [InlineData("Initial Catalog=evil;Server=allowed;Database=d")]
    public void Policy_SynonymKeysTwice_Rejects(string cs)
    {
        var c = SqlTargetPolicy.Check(SqlConnectionText.Endpoint(cs), Open);
        Assert.False(c.Allowed);
        Assert.Equal(Multi, c.Message);
    }

    [Fact]
    public void Policy_FailoverPartner_Rejects()
    {
        var c = SqlTargetPolicy.Check(SqlConnectionText.Endpoint("Server=allowed;Database=d;Failover Partner=evil"), Open);
        Assert.False(c.Allowed);
        Assert.Equal("연결 문자열의 Failover Partner는 허용하지 않습니다 — 허용 목록 밖 서버로 바뀔 수 있습니다", c.Message);
    }

    [Fact]
    public void Policy_SingleSynonyms_StillAllowed()
    {
        Assert.True(SqlTargetPolicy.Check(SqlConnectionText.Endpoint("Data Source=allowed;Initial Catalog=d"), Open).Allowed);
    }

    [Fact]
    public void Policy_EmptyAllowlistEntries_NeverMatch()
    {
        var o = new SqlOptions { AllowedHosts = { "", "  " }, AllowedDatabases = { "" } };
        Assert.False(SqlTargetPolicy.Check(SqlConnectionText.Endpoint("Server=x;Password=a;b"), o).Allowed);
        Assert.False(SqlTargetPolicy.Check(new SqlEndpoint("", "", null), o).Allowed);
        var o2 = new SqlOptions { AllowedHosts = { "x" }, AllowedDatabases = { "" } };
        Assert.False(SqlTargetPolicy.Check(new SqlEndpoint("x", "x", ""), o2).Allowed);
    }

    [Theory]
    [InlineData("Server=x;Password=a;b")]
    [InlineData("Server=x;PWD =a;b")]
    [InlineData("password = a;b")]
    public void HasPassword_UnparsableString_FallsBackToText(string cs)
    {
        Assert.True(SqlConnectionText.HasPassword(cs));
    }
}
