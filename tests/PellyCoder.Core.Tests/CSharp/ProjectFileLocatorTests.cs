using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.CSharp;

namespace PellyCoder.Core.Tests.CSharp;

public sealed class ProjectFileLocatorTests
{
    [Fact]
    public void FindNearestCsproj_WalksUp()
    {
        var file = CorpusLocation.Get("Modern/Shop.Api/Services/OrderService.cs");

        var found = ProjectFileLocator.FindNearestCsproj(new FileSystem(), file);

        Assert.Equal(CorpusLocation.Get("Modern/Shop.Api/Shop.Api.csproj"), found);
    }

    [Fact]
    public void FindNearestCsproj_NoProject_ReturnsNull()
    {
        var fs = new MockFileSystem();
        fs.AddFile(TestPaths.P(@"C:\x\y\A.cs"), new MockFileData("class A {}"));

        Assert.Null(ProjectFileLocator.FindNearestCsproj(fs, TestPaths.P(@"C:\x\y\A.cs")));
    }

    [Fact]
    public void FindNearestCsproj_PrefersNearest()
    {
        var fs = new MockFileSystem();
        fs.AddFile(TestPaths.P(@"C:\r\Outer.csproj"), new MockFileData("<Project/>"));
        fs.AddFile(TestPaths.P(@"C:\r\sub\Inner.csproj"), new MockFileData("<Project/>"));
        fs.AddFile(TestPaths.P(@"C:\r\sub\deep\A.cs"), new MockFileData("class A {}"));

        Assert.Equal(TestPaths.P(@"C:\r\sub\Inner.csproj"),
            ProjectFileLocator.FindNearestCsproj(fs, TestPaths.P(@"C:\r\sub\deep\A.cs")));
    }
}
