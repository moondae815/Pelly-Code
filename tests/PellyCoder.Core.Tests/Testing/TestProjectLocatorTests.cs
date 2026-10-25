using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Config;
using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Tests.Testing;

public class TestProjectLocatorTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";

    private static string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The Modern corpus layout: Shop.Api.Tests references Shop.Api; Shop.Jobs has no tests.</summary>
    private static MockFileSystem Modern()
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("Modern.sln"), new MockFileData("""
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Shop.Api", "Shop.Api\Shop.Api.csproj", "{A}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Shop.Jobs", "Shop.Jobs\Shop.Jobs.csproj", "{B}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Shop.Api.Tests", "Shop.Api.Tests\Shop.Api.Tests.csproj", "{C}"
            EndProject
            """));
        fs.AddFile(P("Shop.Api/Shop.Api.csproj"), new MockFileData("<Project Sdk=\"Microsoft.NET.Sdk\" />"));
        fs.AddFile(P("Shop.Jobs/Shop.Jobs.csproj"), new MockFileData("<Project Sdk=\"Microsoft.NET.Sdk\" />"));
        fs.AddFile(P("Shop.Api.Tests/Shop.Api.Tests.csproj"), new MockFileData("""
            <Project Sdk="Microsoft.NET.Sdk">
              <ItemGroup><ProjectReference Include="..\Shop.Api\Shop.Api.csproj" /></ItemGroup>
            </Project>
            """));
        return fs;
    }

    [Fact]
    public void Locator_ModernApi_FindsApiTests()
    {
        var found = TestProjectLocator.Find(Modern(), Root, P("Shop.Api/Shop.Api.csproj"), new TestOptions());

        Assert.Equal([P("Shop.Api.Tests/Shop.Api.Tests.csproj")], found);
    }

    [Fact]
    public void Locator_ComparesTheTargetWithTheOsPathComparer()
    {
        // The same file on Windows and macOS (case-insensitive file systems); a different one elsewhere.
        var found = TestProjectLocator.Find(Modern(), Root, P("shop.api/SHOP.API.csproj"), new TestOptions());

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            Assert.Equal([P("Shop.Api.Tests/Shop.Api.Tests.csproj")], found);
        else
            Assert.Empty(found);
    }

    [Fact]
    public void Locator_ModernJobs_FindsNone()
    {
        Assert.Empty(TestProjectLocator.Find(Modern(), Root, P("Shop.Jobs/Shop.Jobs.csproj"), new TestOptions()));
    }

    [Fact]
    public void Locator_LegacyData_FindsShopTests()
    {
        // Legacy csproj files use backslash separators in ProjectReference and the solution.
        var fs = new MockFileSystem();
        fs.AddFile(P("Legacy.sln"), new MockFileData("""
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Shop.Data", "Shop.Data\Shop.Data.csproj", "{A}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Shop.Web", "Shop.Web\Shop.Web.csproj", "{B}"
            EndProject
            Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Shop.Tests", "Shop.Tests\Shop.Tests.csproj", "{C}"
            EndProject
            """));
        fs.AddFile(P("Shop.Data/Shop.Data.csproj"), new MockFileData("<Project xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\" />"));
        fs.AddFile(P("Shop.Web/Shop.Web.csproj"), new MockFileData("""
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup><ProjectReference Include="..\Shop.Data\Shop.Data.csproj"><Project>{A}</Project></ProjectReference></ItemGroup>
            </Project>
            """));
        fs.AddFile(P("Shop.Tests/Shop.Tests.csproj"), new MockFileData("""
            <Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003">
              <ItemGroup>
                <ProjectReference Include="..\Shop.Data\Shop.Data.csproj"><Project>{A}</Project></ProjectReference>
                <ProjectReference Include="..\Shop.Web\Shop.Web.csproj"><Project>{B}</Project></ProjectReference>
              </ItemGroup>
            </Project>
            """));

        var found = TestProjectLocator.Find(fs, Root, P("Shop.Data/Shop.Data.csproj"), new TestOptions());

        Assert.Equal([P("Shop.Tests/Shop.Tests.csproj")], found);
    }

    [Fact]
    public void Locator_OptionsOverride_RelativeToWorkspace()
    {
        var fs = Modern();
        var options = new TestOptions { Projects = { "Shop.Api.Tests/Shop.Api.Tests.csproj" } };

        var found = TestProjectLocator.Find(fs, Root, P("Shop.Jobs/Shop.Jobs.csproj"), options);
        Assert.Equal([P("Shop.Api.Tests/Shop.Api.Tests.csproj")], found);

        options.Projects.Add("Missing/Missing.Tests.csproj");
        Assert.Throws<FileNotFoundException>(() => TestProjectLocator.Find(fs, Root, P("Shop.Jobs/Shop.Jobs.csproj"), options));
    }

    [Fact]
    public void Locator_NoSln_ScansWorkspace()
    {
        var fs = Modern();
        fs.RemoveFile(P("Modern.sln"));
        // bin/obj copies must not count.
        fs.AddFile(P("Shop.Api.Tests/obj/Stale.Tests.csproj"), new MockFileData(
            "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"..\\..\\Shop.Api\\Shop.Api.csproj\" /></ItemGroup></Project>"));

        var found = TestProjectLocator.Find(fs, Root, P("Shop.Api/Shop.Api.csproj"), new TestOptions());

        Assert.Equal([P("Shop.Api.Tests/Shop.Api.Tests.csproj")], found);
    }

    private const string RefsApi =
        "<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"{0}\" /></ItemGroup></Project>";

    private static MockFileSystem NoSlnWorkspace()
    {
        var fs = Modern();
        fs.RemoveFile(P("Modern.sln"));
        return fs;
    }

    private static string StaleRef(string rel) => string.Format(RefsApi, Path.Combine(Root, "Shop.Api", "Shop.Api.csproj"));

    [Fact]
    public void Locator_NoSln_DoesNotEnterToolAndBuildFolders()
    {
        var fs = NoSlnWorkspace();
        foreach (var dir in new[] { "bin", "OBJ", ".pelly", ".git", ".vs", "node_modules", "packages", "TestResults" })
            fs.AddFile(P($"{dir}/Copy.Tests/Copy.Tests.csproj"), new MockFileData(StaleRef(dir)));

        var found = TestProjectLocator.Find(fs, Root, P("Shop.Api/Shop.Api.csproj"), new TestOptions());

        Assert.Equal([P("Shop.Api.Tests/Shop.Api.Tests.csproj")], found);
    }

    [Fact]
    public void Locator_NoSln_DoesNotFollowLinks()
    {
        var fs = NoSlnWorkspace();
        fs.AddFile("/elsewhere/Linked.Tests/Linked.Tests.csproj", new MockFileData(StaleRef("x")));
        fs.Directory.CreateSymbolicLink(P("linked"), "/elsewhere");
        Assert.NotNull(fs.DirectoryInfo.New(P("linked")).LinkTarget);
        fs.AddFile(P("junction/Junction.Tests/Junction.Tests.csproj"), new MockFileData(StaleRef("x")));
        fs.DirectoryInfo.New(P("junction")).Attributes |= FileAttributes.ReparsePoint;

        var found = TestProjectLocator.Find(fs, Root, P("Shop.Api/Shop.Api.csproj"), new TestOptions());

        Assert.Equal([P("Shop.Api.Tests/Shop.Api.Tests.csproj")], found);
    }

    [Fact]
    public void Locator_NoSln_StopsAtMaxDirectories()
    {
        var fs = NoSlnWorkspace();
        for (var i = 0; i < 2100; i++) fs.AddDirectory(P($"a{i:D4}"));
        fs.AddFile(P("zz/Late.Tests/Late.Tests.csproj"), new MockFileData(StaleRef("x")));

        var found = TestProjectLocator.Find(fs, Root, P("Shop.Api/Shop.Api.csproj"), new TestOptions());

        Assert.DoesNotContain(P("zz/Late.Tests/Late.Tests.csproj"), found);
    }

    [Fact]
    public void Locator_NoSln_UnreadableDirectoryIsSkipped()
    {
        var mock = NoSlnWorkspace();
        mock.AddFile(P("locked/Locked.Tests/Locked.Tests.csproj"), new MockFileData(StaleRef("x")));
        var fs = new UnreadableDirectoryFileSystem(mock, P("locked"));

        var found = TestProjectLocator.Find(fs, Root, P("Shop.Api/Shop.Api.csproj"), new TestOptions());

        Assert.Equal([P("Shop.Api.Tests/Shop.Api.Tests.csproj")], found);
    }

    /// <summary>A file system whose listing of one directory is denied.</summary>
    private sealed class UnreadableDirectoryFileSystem(MockFileSystem inner, string denied) : IFileSystem
    {
        private readonly IDirectory _directory = new DeniedDirectory(inner, denied);
        public IFile File => inner.File;
        public IDirectory Directory => _directory;
        public IFileInfoFactory FileInfo => inner.FileInfo;
        public IFileStreamFactory FileStream => inner.FileStream;
        public IPath Path => inner.Path;
        public IDirectoryInfoFactory DirectoryInfo => inner.DirectoryInfo;
        public IDriveInfoFactory DriveInfo => inner.DriveInfo;
        public IFileSystemWatcherFactory FileSystemWatcher => inner.FileSystemWatcher;
        public IFileVersionInfoFactory FileVersionInfo => inner.FileVersionInfo;
        public IRandomAccess RandomAccess => inner.RandomAccess;
    }

    private sealed class DeniedDirectory(MockFileSystem inner, string denied) : MockDirectory(inner, inner.File as FileBase, Root)
    {
        public override string[] GetFiles(string path, string searchPattern) =>
            path == denied ? throw new UnauthorizedAccessException("denied") : base.GetFiles(path, searchPattern);
    }
}
