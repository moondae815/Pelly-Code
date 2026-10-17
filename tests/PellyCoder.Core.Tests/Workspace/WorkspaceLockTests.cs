using System.IO.Abstractions;
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
        var fs = new FileSystem();
        var blocker = Path.Combine(_root, "blocker");
        File.WriteAllText(blocker, "x");
        // PellyDir lies under an existing file, so creating it fails for a reason other than a lock.
        var ws = new Workspace(_root, Path.Combine(blocker, ".pelly"));

        Assert.ThrowsAny<IOException>(() => WorkspaceLock.TryAcquire(fs, ws));
    }
}
