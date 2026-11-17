using System.Diagnostics;
using System.IO.Abstractions;
using System.Text;
using PellyCode.Core.Processes;
using PellyCode.McpServer.Baseline;
using PellyCode.McpServer.Git;

namespace PellyCode.McpServer.Tests;

public sealed class BaseMirrorTests : IDisposable
{
    private readonly string _base = Path.Combine(Path.GetTempPath(), "pelly-mirror-" + Guid.NewGuid().ToString("N"));
    private readonly string _dir;
    private readonly string _mirror;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public BaseMirrorTests()
    {
        _dir = Path.Combine(_base, "repo");
        _mirror = Path.Combine(_base, "mirror");
        Directory.CreateDirectory(_dir);
        Git("init", "-q");
        Git("config", "user.name", "t");
        Git("config", "user.email", "t@example.com");
        Git("config", "commit.gpgsign", "false");
        Git("config", "core.autocrlf", "false");
    }

    public void Dispose()
    {
        try { Directory.Delete(_base, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private void Git(params string[] args)
    {
        var psi = new ProcessStartInfo("git") { WorkingDirectory = _dir, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }

    private void Write(string rel, string text)
    {
        var full = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, Encoding.UTF8.GetBytes(text));
    }

    private void Commit() { Git("add", "-A"); Git("commit", "-q", "-m", "c"); }
    private string Mirror(string rel) => Path.Combine(_mirror, rel);
    private string ReadMirror(string rel) => File.ReadAllText(Mirror(rel));

    private async Task<MirrorResult> SyncAsync()
    {
        var git = new GitRepo(new SystemProcessRunner(TimeProvider.System), _dir);
        var changes = await git.ChangesAsync(Ct);
        return await new BaseMirror(new FileSystem(), git).SyncAsync(_dir, _mirror, changes, Ct);
    }

    [Fact]
    public async Task FirstSync_CopiesTreeWithoutExcludedFolders()
    {
        Write("a.cs", "a");
        Write("packages/p.txt", "p");
        Write("src/bin/x.dll", "x");
        Write("obj/y.txt", "y");
        Write("node_modules/n/z.js", "z");
        Write("deep/Bin/q.txt", "q");
        Commit();

        var r = await SyncAsync();

        Assert.True(r.Ok, r.FailureReason);
        Assert.True(File.Exists(Mirror("a.cs")));
        Assert.True(File.Exists(Mirror("packages/p.txt")));
        Assert.False(Directory.Exists(Mirror(".git")));
        Assert.False(Directory.Exists(Mirror("obj")));
        Assert.False(Directory.Exists(Mirror("src/bin")));
        Assert.False(Directory.Exists(Mirror("node_modules")));
        Assert.False(Directory.Exists(Mirror("deep/Bin")));
    }

    [Fact]
    public async Task Sync_RevertsModifiedToHead()
    {
        Write("m.cs", "old");
        Commit();
        Write("m.cs", "new content");

        var r = await SyncAsync();

        Assert.True(r.Ok, r.FailureReason);
        Assert.Equal("old", ReadMirror("m.cs"));
    }

    [Fact]
    public async Task Sync_RemovesAdded()
    {
        Write("keep.cs", "k");
        Commit();
        Write("new.cs", "n");

        var r = await SyncAsync();

        Assert.True(r.Ok, r.FailureReason);
        Assert.False(File.Exists(Mirror("new.cs")));
        Assert.True(File.Exists(Mirror("keep.cs")));
    }

    [Fact]
    public async Task Sync_Rename_OldPathWithHeadContent()
    {
        Write("old.cs", "content of old");
        Commit();
        Git("mv", "old.cs", "renamed.cs");

        var r = await SyncAsync();

        Assert.True(r.Ok, r.FailureReason);
        Assert.Equal("content of old", ReadMirror("old.cs"));
        Assert.False(File.Exists(Mirror("renamed.cs")));
    }

    [Fact]
    public async Task Sync_RestoresDeletedFile()
    {
        Write("gone.cs", "still in head");
        Write("other.cs", "o");
        Commit();
        File.Delete(Path.Combine(_dir, "gone.cs"));

        var r = await SyncAsync();

        Assert.True(r.Ok, r.FailureReason);
        Assert.Equal("still in head", ReadMirror("gone.cs"));
    }

    [Fact]
    public async Task SecondSync_CopiesOnlyChanged()
    {
        Write("a.cs", "a"); Write("b.cs", "b"); Write("m.cs", "m");
        Commit();
        Write("m.cs", "modified");
        var first = await SyncAsync();
        Assert.True(first.Ok, first.FailureReason);
        Assert.Equal(3, first.CopiedFiles);

        var same = await SyncAsync();
        Assert.True(same.Ok, same.FailureReason);
        Assert.Equal(0, same.CopiedFiles);

        Write("b.cs", "bb bigger");
        Git("add", "b.cs"); Git("commit", "-q", "-m", "b"); // committed, so b.cs is no longer a change and is copied from the tree
        var third = await SyncAsync();
        Assert.Equal(1, third.CopiedFiles);
        Assert.Equal("bb bigger", ReadMirror("b.cs"));
        Assert.Equal("m", ReadMirror("m.cs"));
    }

    [Fact]
    public async Task Sync_DeletesFilesGoneFromTree()
    {
        Write("a.cs", "a"); Write("sub/b.cs", "b");
        Commit();
        await SyncAsync();
        Git("rm", "-q", "-f", "sub/b.cs");
        Git("commit", "-q", "-m", "rm");

        var r = await SyncAsync();

        Assert.True(r.Ok, r.FailureReason);
        Assert.Equal(1, r.DeletedFiles);
        Assert.False(File.Exists(Mirror("sub/b.cs")));
        Assert.False(Directory.Exists(Mirror("sub")));
    }

    [Fact]
    public async Task Sync_FileBecomesRevertedAfterChange_IsRecopiedFromTree()
    {
        Write("m.cs", "old");
        Commit();
        Write("m.cs", "newer");
        await SyncAsync();
        Write("m.cs", "old"); // back to HEAD content, so git no longer lists it

        var r = await SyncAsync();

        Assert.True(r.Ok, r.FailureReason);
        Assert.Equal("old", ReadMirror("m.cs"));
    }

    [Fact]
    public async Task Sync_DoesNotFollowSymlinks()
    {
        Write("a.cs", "a");
        Commit();
        var outside = Path.Combine(_base, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "s");
        try { Directory.CreateSymbolicLink(Path.Combine(_dir, "link"), outside); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or PlatformNotSupportedException) { return; }

        var r = await SyncAsync();

        Assert.True(r.Ok, r.FailureReason);
        Assert.False(Directory.Exists(Mirror("link")));
        Assert.True(File.Exists(Mirror("a.cs")));
    }

    [Fact]
    public async Task Sync_CopyFailure_ReturnsNotOkWithReason()
    {
        Write("a.cs", "a");
        Commit();
        Directory.CreateDirectory(Mirror("a.cs")); // a folder where a file must go

        var r = await SyncAsync();

        Assert.False(r.Ok);
        Assert.False(string.IsNullOrWhiteSpace(r.FailureReason));
    }

    [Fact]
    public void MirrorPaths_OutsideRoot_StableHash()
    {
        var root = Path.Combine(_base, "Some", "Root");
        var p = MirrorPaths.For(root);

        Assert.Equal(p, MirrorPaths.For(root));
        Assert.Equal(p, MirrorPaths.For(root.ToUpperInvariant() + Path.DirectorySeparatorChar));
        Assert.NotEqual(p, MirrorPaths.For(Path.Combine(_base, "Other")));
        Assert.False(p.StartsWith(root, StringComparison.OrdinalIgnoreCase));
        Assert.Matches("^[0-9a-f]{16}$", Path.GetFileName(p));
        Assert.EndsWith(Path.Combine("PellyCode", "mcp-base", Path.GetFileName(p)), p);
    }
}
