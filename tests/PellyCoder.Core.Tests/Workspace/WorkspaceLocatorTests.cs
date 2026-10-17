using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Workspace;
using static PellyCoder.Core.Tests.TestPaths;

namespace PellyCoder.Core.Tests.Workspace;

public class WorkspaceLocatorTests
{
    [Fact]
    public void Root_IsNearestAncestorWithGitSvnOrSln()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(P(@"C:\r\.git"));
        fs.AddFile(P(@"C:\r\a\b\f.cs"), new MockFileData(""));
        Assert.Equal(P(@"C:\r"), WorkspaceLocator.Resolve(fs, P(@"C:\r\a\b\f.cs")).Root);

        var sln = new MockFileSystem();
        sln.AddFile(P(@"C:\r\x.sln"), new MockFileData(""));
        sln.AddFile(P(@"C:\r\a\f.cs"), new MockFileData(""));
        Assert.Equal(P(@"C:\r"), WorkspaceLocator.Resolve(sln, P(@"C:\r\a\f.cs")).Root);

        var svn = new MockFileSystem();
        svn.AddDirectory(P(@"C:\r\.svn"));
        svn.AddFile(P(@"C:\r\a\f.cs"), new MockFileData(""));
        Assert.Equal(P(@"C:\r"), WorkspaceLocator.Resolve(svn, P(@"C:\r\a\f.cs")).Root);

        var nested = new MockFileSystem();
        nested.AddDirectory(P(@"C:\r\.git"));
        nested.AddFile(P(@"C:\r\a\inner.sln"), new MockFileData(""));
        nested.AddFile(P(@"C:\r\a\b\f.cs"), new MockFileData(""));
        var ws = WorkspaceLocator.Resolve(nested, P(@"C:\r\a\b\f.cs"));
        Assert.Equal(P(@"C:\r\a"), ws.Root);
        Assert.Equal(P(@"C:\r\a\.pelly"), ws.PellyDir);
    }

    [Fact]
    public void Root_FallsBackToFileDirectory()
    {
        var fs = new MockFileSystem();
        fs.AddFile(P(@"C:\solo\f.cs"), new MockFileData(""));
        Assert.Equal(P(@"C:\solo"), WorkspaceLocator.Resolve(fs, P(@"C:\solo\f.cs")).Root);
    }

    [Fact]
    public void EnsurePellyDir_CreatesRunsBackupAndGitignoreStar()
    {
        var fs = new MockFileSystem();
        fs.AddFile(P(@"C:\solo\f.cs"), new MockFileData(""));
        var ws = WorkspaceLocator.Resolve(fs, P(@"C:\solo\f.cs"));
        WorkspaceLocator.EnsurePellyDir(fs, ws);
        WorkspaceLocator.EnsurePellyDir(fs, ws); // idempotent

        Assert.True(fs.Directory.Exists(fs.Path.Combine(ws.PellyDir, "runs")));
        Assert.True(fs.Directory.Exists(fs.Path.Combine(ws.PellyDir, "backup")));
        Assert.Equal("*\n", fs.File.ReadAllText(fs.Path.Combine(ws.PellyDir, ".gitignore")));
    }
}
