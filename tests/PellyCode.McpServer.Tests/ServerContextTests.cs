using System.IO.Abstractions.TestingHelpers;

namespace PellyCode.McpServer.Tests;

public sealed class ServerContextTests
{
    private static string P(string rel) => Path.GetFullPath(Path.Combine(Path.GetTempPath(), "ctxroot", rel));

    [Fact]
    public void NoGit_ReturnsError()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(P("proj"));

        var ctx = ServerContext.TryCreate(fs, P("proj"), out var error);

        Assert.Null(ctx);
        Assert.Equal($"git 저장소가 아닙니다: {P("proj")}", error);
    }

    [Fact]
    public void GitDirectory_Ok()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(P("proj/.git"));

        var ctx = ServerContext.TryCreate(fs, P("proj"), out var error);

        Assert.NotNull(ctx);
        Assert.Null(error);
        Assert.Equal(P("proj"), ctx.Root);
    }

    [Fact]
    public void GitFile_Worktree_Ok()
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("wt/.git"), new MockFileData("gitdir: /elsewhere"));

        var ctx = ServerContext.TryCreate(fs, P("wt"), out var error);

        Assert.NotNull(ctx);
        Assert.Equal(P("wt"), ctx.Root);
    }

    [Fact]
    public void Subfolder_UsesRepoFolderAsRoot()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory(P("repo/.git"));
        fs.AddDirectory(P("repo/src/deep"));

        var ctx = ServerContext.TryCreate(fs, P("repo/src/deep"), out var error);

        Assert.NotNull(ctx);
        Assert.Equal(P("repo"), ctx.Root);
        Assert.Equal(P("repo"), ctx.Workspace.Root);
    }
}
