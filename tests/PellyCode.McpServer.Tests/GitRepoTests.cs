using System.Diagnostics;
using System.Text;
using PellyCode.Core.Processes;
using PellyCode.McpServer.Git;

namespace PellyCode.McpServer.Tests;

public sealed class GitRepoTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pelly-git-" + Guid.NewGuid().ToString("N"));

    public GitRepoTests()
    {
        Directory.CreateDirectory(_dir);
        Git("init", "-q");
        Git("config", "user.name", "t");
        Git("config", "user.email", "t@example.com");
        Git("config", "commit.gpgsign", "false");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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

    private void Write(string rel, string text) => WriteBytes(rel, Encoding.UTF8.GetBytes(text));

    private void WriteBytes(string rel, byte[] bytes)
    {
        var full = Path.Combine(_dir, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
    }

    private void Commit() { Git("add", "-A"); Git("commit", "-q", "-m", "c"); }

    private GitRepo Repo() => new(new SystemProcessRunner(TimeProvider.System), _dir);

    [Fact]
    public async Task Changes_ModifiedAddedDeletedUntracked()
    {
        Write("mod.cs", "a"); Write("del.cs", "b"); Write("staged.cs", "x");
        Commit();
        Write("mod.cs", "a2");
        File.Delete(Path.Combine(_dir, "del.cs"));
        Write("added.cs", "n"); Git("add", "added.cs");
        Write("untracked.cs", "u");

        var changes = (await Repo().ChangesAsync(TestContext.Current.CancellationToken)).OrderBy(c => c.Path, StringComparer.Ordinal).ToList();

        Assert.Equal(
            [
                new GitChange("added.cs", GitChangeKind.Added, null),
                new GitChange("del.cs", GitChangeKind.Deleted, null),
                new GitChange("mod.cs", GitChangeKind.Modified, null),
                new GitChange("untracked.cs", GitChangeKind.Added, null),
            ], changes);
    }

    [Fact]
    public async Task Changes_Rename_KeepsOldPath()
    {
        Write("a.cs", "content that is long enough to be detected as a rename");
        Commit();
        Git("mv", "a.cs", "b.cs");

        var changes = await Repo().ChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal([new GitChange("b.cs", GitChangeKind.Renamed, "a.cs")], changes);
    }

    [Fact]
    public async Task Changes_PathWithSpaceAndHangul()
    {
        Commit0();
        Write("소스 폴더/주문 처리.asp", "x");

        var changes = await Repo().ChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal([new GitChange("소스 폴더/주문 처리.asp", GitChangeKind.Added, null)], changes);
    }

    private void Commit0() { Write("seed.txt", "s"); Commit(); }

    [Fact]
    public async Task Changes_IgnoredFileExcluded()
    {
        Write(".gitignore", "*.log\n"); Commit();
        Write("x.log", "l"); Write("y.cs", "y");

        var changes = await Repo().ChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal([new GitChange("y.cs", GitChangeKind.Added, null)], changes);
    }

    [Fact]
    public async Task ShowHead_ReturnsOriginalBytes_Cp949()
    {
        var bytes = new byte[] { 0xC7, 0xD1, 0xB1, 0xDB, 0x0D, 0x0A, 0x41 };
        WriteBytes("k.asp", bytes);
        Commit();
        Write("k.asp", "changed");

        var shown = await Repo().ShowHeadAsync("k.asp", TestContext.Current.CancellationToken);

        Assert.Equal(bytes, shown);
    }

    [Fact]
    public async Task ShowHead_NewFile_Null()
    {
        Commit0();
        Write("new.cs", "n");

        Assert.Null(await Repo().ShowHeadAsync("new.cs", TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Head_NoCommits_Null() => Assert.Null(await Repo().HeadAsync(TestContext.Current.CancellationToken));

    [Fact]
    public async Task Head_AfterCommit_ReturnsSha()
    {
        Commit0();
        var head = await Repo().HeadAsync(TestContext.Current.CancellationToken);
        Assert.NotNull(head);
        Assert.Equal(40, head.Length);
    }

    [Fact]
    public async Task Changes_IgnoresPellyFolder()
    {
        Write("a.cs", "a");
        Commit();
        Write(".pelly/cache/site-index.json", "{}");
        Write("b.cs", "b");

        var changes = await Repo().ChangesAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["b.cs"], changes.Select(c => c.Path));
    }
}
