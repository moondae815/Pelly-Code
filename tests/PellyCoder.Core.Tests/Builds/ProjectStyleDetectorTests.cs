using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Builds;

namespace PellyCoder.Core.Tests.Builds;

public sealed class ProjectStyleDetectorTests
{
    [Fact]
    public void ModernCorpus_IsSdk() =>
        Assert.Equal(ProjectStyle.Sdk,
            ProjectStyleDetector.Detect(new FileSystem(), CorpusLocation.Get("Modern/Shop.Api/Shop.Api.csproj")));

    [Fact]
    public void LegacyCorpus_IsLegacy() =>
        Assert.Equal(ProjectStyle.Legacy,
            ProjectStyleDetector.Detect(new FileSystem(), CorpusLocation.Get("Legacy/Shop.Data/Shop.Data.csproj")));

    [Fact]
    public void SdkElementChild_IsSdk()
    {
        var path = TestPaths.P(@"C:\p\a.csproj");
        var fs = new MockFileSystem(new Dictionary<string, MockFileData>
        {
            [path] = new("<Project><Sdk Name=\"Microsoft.NET.Sdk\" /></Project>"),
        });
        Assert.Equal(ProjectStyle.Sdk, ProjectStyleDetector.Detect(fs, path));
    }

    [Fact]
    public void NoCsproj_IsNone() =>
        Assert.Equal(ProjectStyle.None, ProjectStyleDetector.Detect(new MockFileSystem(), null));
}
