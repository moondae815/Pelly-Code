using PellyCode.Core;

namespace PellyCode.Core.Tests;

public class PellyInfoTests
{
    [Fact]
    public void Version_IsSemVerWithoutCommitHash()
    {
        Assert.Matches(@"^\d+\.\d+\.\d+(-[0-9A-Za-z.]+)?$", PellyInfo.Version);
    }
}
