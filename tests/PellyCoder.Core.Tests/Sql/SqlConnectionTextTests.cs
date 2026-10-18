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
        Assert.Contains("Server=x", SqlConnectionText.Mask("Server=x;Password=p"));
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
    [InlineData("Server=tcp:db1,1433;Database=d", "tcp:db1,1433", "db1", "d")]
    [InlineData("Data Source=localhost,14333;Initial Catalog=x", "localhost,14333", "localhost", "x")]
    [InlineData(@"Server=db1\SQLEXPRESS", @"db1\SQLEXPRESS", "db1", null)]
    public void Endpoint_NormalizesHost(string cs, string dataSource, string host, string? db)
    {
        var e = SqlConnectionText.Endpoint(cs);
        Assert.Equal(host, e.Host);
        Assert.Equal(db, e.Database);
        Assert.Equal(dataSource.StartsWith("tcp:") ? dataSource[4..].Split(',')[0] : dataSource.Split(',')[0], e.DataSource.Split(',')[0]);
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
}
