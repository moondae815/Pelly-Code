using PellyCoder.Core.Doctor;

namespace PellyCoder.Core.Tests.Doctor;

public class OfflineSelfTestTests
{
    [Fact]
    public void SelfTest_AllOk()
    {
        var checks = OfflineSelfTest.Run();

        Assert.All(checks, c => Assert.True(c.Status == DoctorStatus.Ok, $"{c.Name}: {c.Detail}"));
    }

    [Fact]
    public void SelfTest_HasSixRowsInOrder()
    {
        var checks = OfflineSelfTest.Run();

        Assert.Equal(["토크나이저", "Roslyn", "ScriptDom", "인코딩", "리소스", "SqlClient"], checks.Select(c => c.Name));
    }

    [Fact]
    public void SelfTest_SqlClientRow_LoadsNativeLayer()
    {
        var row = Assert.Single(OfflineSelfTest.Run(), c => c.Name == "SqlClient");

        Assert.True(row.Status == DoctorStatus.Ok, row.Detail);
        if (OperatingSystem.IsWindows())
            Assert.Contains("네이티브 SNI", row.Detail);
        else
            Assert.Equal("관리 코드 SNI", row.Detail);
    }

    /// <summary>
    /// Several self-tests at once in one process (the Cli test classes run in parallel; a host may run two doctors). Root cause of
    /// the windows-latest flake (CI 39214681652, 39238902534; reproduced 7/32 full unit-suite runs in diag run 39254095710): with
    /// pooling on, the second open waited on the pool while the first was still failing against the missing pipe; under load that
    /// took longer than Connect Timeout=1 and the waiter got InvalidOperationException ("Timeout expired ... obtaining a connection
    /// from the pool"), not a SqlException, so the row was Fail. The self-test connection does not pool.
    /// </summary>
    [Fact]
    public void SelfTestConnection_DoesNotPool() =>
        Assert.False(new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(PellyCoder.Core.Sql.SqlServerExecutor.SelfTestConnectionString).Pooling);

    [Fact]
    public async Task SelfTest_SqlClientRow_OkWhenRunConcurrently()
    {
        var rows = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => Assert.Single(OfflineSelfTest.Run(), c => c.Name == "SqlClient"))));

        Assert.All(rows, r => Assert.True(r.Status == DoctorStatus.Ok, r.Detail));
    }
}
