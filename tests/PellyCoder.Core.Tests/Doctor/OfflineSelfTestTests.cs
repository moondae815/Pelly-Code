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
}
