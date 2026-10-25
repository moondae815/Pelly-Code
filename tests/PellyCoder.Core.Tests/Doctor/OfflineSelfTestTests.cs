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
    public void SelfTest_HasSevenRowsInOrder()
    {
        var checks = OfflineSelfTest.Run();

        Assert.Equal(["토크나이저", "Roslyn", "ScriptDom", "인코딩", "리소스", "SqlClient", "의미 분석 보조 프로세스"], checks.Select(c => c.Name));
    }

    [Fact]
    public void BuildHost_row_fails_when_a_folder_is_missing()
    {
        var baseDir = Directory.CreateTempSubdirectory("pelly-buildhost-").FullName;
        try
        {
            Assert.ThrowsAny<Exception>(() => OfflineSelfTest.CheckBuildHost(baseDir));

            Directory.CreateDirectory(Path.Combine(baseDir, "BuildHost-net472"));
            File.WriteAllText(Path.Combine(baseDir, "BuildHost-net472", "a.dll"), "x");
            var ex = Assert.ThrowsAny<Exception>(() => OfflineSelfTest.CheckBuildHost(baseDir));
            Assert.Contains("BuildHost-netcore", ex.Message, StringComparison.Ordinal);

            Directory.CreateDirectory(Path.Combine(baseDir, "BuildHost-netcore"));
            Assert.ThrowsAny<Exception>(() => OfflineSelfTest.CheckBuildHost(baseDir)); // empty folder

            File.WriteAllText(Path.Combine(baseDir, "BuildHost-netcore", "b.dll"), "x");
            Assert.False(string.IsNullOrWhiteSpace(OfflineSelfTest.CheckBuildHost(baseDir)));
        }
        finally
        {
            Directory.Delete(baseDir, recursive: true);
        }
    }

    [Fact]
    public void BuildHost_row_failure_tells_how_to_fix()
    {
        var row = OfflineSelfTest.BuildHostRow(Path.Combine(Path.GetTempPath(), "pelly-no-such-dir-" + Guid.NewGuid().ToString("N")));

        Assert.Equal("의미 분석 보조 프로세스", row.Name);
        Assert.Equal(DoctorStatus.Fail, row.Status);
        Assert.Equal("반입 zip을 다시 풀어 pelly.exe 옆에 BuildHost-net472, BuildHost-netcore 폴더를 두세요", row.Action);
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
