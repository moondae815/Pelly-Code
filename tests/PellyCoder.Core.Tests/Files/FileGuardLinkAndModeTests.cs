using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Files;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Files;

public sealed class FileGuardLinkAndModeTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pelly-link-").FullName;
    private readonly FileGuard _guard = new(new FileSystem(), TimeProvider.System);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Workspace Ws => new(_root, Path.Combine(_root, ".pelly"));

    private static void UnixOnly() => Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix only (Windows 미확인)");

    private string Put(string name, string text)
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes(text));
        return path;
    }

    private string Link(string linkName, string target)
    {
        string link = Path.Combine(_root, linkName);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        File.CreateSymbolicLink(link, target);
        return link;
    }

#pragma warning disable CA1416 // guarded by UnixOnly()
    [Fact]
    public async Task Write_PreservesExecutableBit()
    {
        UnixOnly();
        string path = Put("a.cs", "int a;\n");
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        var snap = _guard.Read(path);

        var r = await _guard.WriteAsync(snap, "int b;\n", TestContext.Current.CancellationToken);

        Assert.Equal(FileWriteStatus.Written, r.Status);
        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task Restore_PreservesMode()
    {
        UnixOnly();
        string path = Put("a.cs", "int a;\n");
        File.SetUnixFileMode(path, (UnixFileMode)0b111_101_101);
        var snap = _guard.Read(path);
        string backup = _guard.Backup(Ws, "r1", snap);
        var w = await _guard.WriteAsync(snap, "int b;\n", TestContext.Current.CancellationToken);

        var r = await _guard.RestoreAsync(backup, w.Snapshot!, TestContext.Current.CancellationToken);

        Assert.Equal(FileWriteStatus.Written, r.Status);
        Assert.Equal("int a;\n", File.ReadAllText(path));
        Assert.Equal((UnixFileMode)0b111_101_101, File.GetUnixFileMode(path));
    }

    [Fact]
    public async Task Write_ThroughSymlink_KeepsLink_UpdatesTarget()
    {
        UnixOnly();
        string target = Put("real/t.cs", "int a;\n");
        string link = Link("link/t.cs", target);
        var snap = _guard.Read(link);

        var r = await _guard.WriteAsync(snap, "int b;\n", TestContext.Current.CancellationToken);

        Assert.Equal(FileWriteStatus.Written, r.Status);
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("int b;\n", File.ReadAllText(target));
        Assert.Equal(link, r.Snapshot!.Path);
        Assert.False(File.Exists(Path.Combine(_root, "link", ".t.cs.pelly-tmp")));
    }

    [Fact]
    public async Task Restore_ThroughSymlink_KeepsLink()
    {
        UnixOnly();
        string target = Put("real/t.cs", "int a;\n");
        string link = Link("link/t.cs", target);
        var snap = _guard.Read(link);
        string backup = _guard.Backup(Ws, "r1", snap);
        var w = await _guard.WriteAsync(snap, "int b;\n", TestContext.Current.CancellationToken);

        var r = await _guard.RestoreAsync(backup, w.Snapshot!, TestContext.Current.CancellationToken);

        Assert.Equal(FileWriteStatus.Written, r.Status);
        Assert.NotNull(new FileInfo(link).LinkTarget);
        Assert.Equal("int a;\n", File.ReadAllText(target));
    }

    [Fact]
    public async Task ExternalChangeCheck_UsesTargetBytes()
    {
        UnixOnly();
        string target = Put("real/t.cs", "int a;\n");
        string link = Link("link/t.cs", target);
        var snap = _guard.Read(link);
        File.WriteAllText(target, "int external;\n");

        var r = await _guard.WriteAsync(snap, "int b;\n", TestContext.Current.CancellationToken);

        Assert.Equal(FileWriteStatus.ExternallyChanged, r.Status);
        Assert.Equal("int external;\n", File.ReadAllText(target));
        Assert.NotNull(new FileInfo(link).LinkTarget);
    }
    [Fact]
    public async Task Write_TempFileNeverBroaderThanOriginalMode()
    {
        UnixOnly();
        string path = Put("secret.cs", "int a;\n");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); // 0600
        var spy = new TempModeSpyFileSystem();
        var guard = new FileGuard(spy, TimeProvider.System);
        var snap = guard.Read(path);

        var r = await guard.WriteAsync(snap, "int b;\n", TestContext.Current.CancellationToken);

        Assert.Equal(FileWriteStatus.Written, r.Status);
        Assert.NotEmpty(spy.ObservedTempModes);
        Assert.All(spy.ObservedTempModes, m => Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, m));
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }

    /// <summary>Records the temp file's mode whenever FileGuard touches it after creating it (mode change, move).</summary>
    private sealed class TempModeSpyFileSystem : FileSystem
    {
        public List<UnixFileMode> ObservedTempModes { get; } = [];

        public TempModeSpyFileSystem() => File = new SpyFile(this);

        public override IFile File { get; }

        private sealed class SpyFile(TempModeSpyFileSystem owner) : FileWrapper(owner)
        {
            private void Observe(string path)
            {
                if (path.EndsWith(".pelly-tmp", StringComparison.Ordinal) && System.IO.File.Exists(path))
                    owner.ObservedTempModes.Add(System.IO.File.GetUnixFileMode(path));
            }

            public override void SetUnixFileMode(string path, UnixFileMode mode)
            {
                Observe(path);
                base.SetUnixFileMode(path, mode);
            }

            public override void Move(string sourceFileName, string destFileName, bool overwrite)
            {
                Observe(sourceFileName);
                base.Move(sourceFileName, destFileName, overwrite);
            }
        }
    }
#pragma warning restore CA1416
}
