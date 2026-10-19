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
    /// Several self-tests at once (the Cli test classes run in parallel; a user may also start two doctors). The Cli self-test tests
    /// failed intermittently on windows-latest with exit 4 after ~6.7 s (CI 39214681652, 39238902534), failing row unknown. Concurrent
    /// opens waiting on the SqlClient pool were a hypothesis; this test passed on windows-latest (CI 39239610853), so it is not the cause
    /// as far as measured. Kept as a guard; the Cli tests now print the self-test output on failure.
    /// </summary>
    [Fact]
    public async Task SelfTest_SqlClientRow_OkWhenRunConcurrently()
    {
        var rows = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            Task.Run(() => Assert.Single(OfflineSelfTest.Run(), c => c.Name == "SqlClient"))));

        Assert.All(rows, r => Assert.True(r.Status == DoctorStatus.Ok, r.Detail));
    }
}
