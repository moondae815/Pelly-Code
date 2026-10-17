using System.IO.Abstractions;
using Microsoft.Win32.SafeHandles;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Workspaces;

public class WorkspaceLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-lock-" + Guid.NewGuid().ToString("N"));

    public WorkspaceLockTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void SecondAcquire_ReturnsNull_UntilFirstDisposed()
    {
        var fs = new FileSystem();
        var ws = new Workspace(_root, Path.Combine(_root, ".pelly"));

        var first = WorkspaceLock.TryAcquire(fs, ws);
        Assert.NotNull(first);
        Assert.Null(WorkspaceLock.TryAcquire(fs, ws));

        first.Dispose();
        using var third = WorkspaceLock.TryAcquire(fs, ws);
        Assert.NotNull(third);
    }

    [Fact]
    public void NonLockIoFailure_IsNotReportedAsLocked()
    {
        // The failure comes from opening the lock file (inside the try), not from creating the directory.
        var fs = new OpenFailsFileSystem(new IOException("disk full", unchecked((int)0x80070070)));
        var ws = new Workspace(_root, Path.Combine(_root, ".pelly"));

        var ex = Assert.Throws<IOException>(() => WorkspaceLock.TryAcquire(fs, ws));
        Assert.Contains("disk full", ex.Message);
    }

    private sealed class OpenFailsFileSystem(IOException failure) : FileSystem, IFileSystem
    {
        IFileStreamFactory IFileSystem.FileStream => new ThrowingFactory(failure);
    }

    private sealed class ThrowingFactory(IOException failure) : IFileStreamFactory
    {
        public IFileSystem FileSystem => throw new NotSupportedException();
        public FileSystemStream New(SafeFileHandle handle, FileAccess access) => throw failure;
        public FileSystemStream New(SafeFileHandle handle, FileAccess access, int bufferSize) => throw failure;
        public FileSystemStream New(SafeFileHandle handle, FileAccess access, int bufferSize, bool isAsync) => throw failure;
        public FileSystemStream New(string path, FileMode mode) => throw failure;
        public FileSystemStream New(string path, FileMode mode, FileAccess access) => throw failure;
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share) => throw failure;
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize) => throw failure;
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, bool useAsync) => throw failure;
        public FileSystemStream New(string path, FileMode mode, FileAccess access, FileShare share, int bufferSize, FileOptions options) => throw failure;
        public FileSystemStream New(string path, FileStreamOptions options) => throw failure;
        public FileSystemStream Wrap(FileStream fileStream) => throw failure;
    }
}
