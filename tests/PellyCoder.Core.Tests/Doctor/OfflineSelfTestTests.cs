using PellyCoder.Core.Doctor;

namespace PellyCoder.Core.Tests.Doctor;

public class OfflineSelfTestTests
{
    [Fact]
    public void SelfTest_AllOk()
    {
        var checks = OfflineSelfTest.Run();

        Assert.Equal(["토크나이저", "Roslyn", "ScriptDom", "인코딩", "리소스"], checks.Select(c => c.Name));
        Assert.All(checks, c => Assert.True(c.Status == DoctorStatus.Ok, $"{c.Name}: {c.Detail}"));
    }
}
