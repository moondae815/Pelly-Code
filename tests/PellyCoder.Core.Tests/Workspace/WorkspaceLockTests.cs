using System.IO.Abstractions;
using PellyCoder.Core.Workspace;

namespace PellyCoder.Core.Tests.Workspace;

public class WorkspaceLockTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-lock-" + Guid.NewGuid().ToString("N"));

    public WorkspaceLockTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void SecondAcquire_ReturnsNull_UntilFirstDisposed()
    {
        var fs = new FileSystem();
        var ws = new PellyCoder.Core.Workspace.Workspace(_root, Path.Combine(_root, ".pelly"));

        var first = WorkspaceLock.TryAcquire(fs, ws);
        Assert.NotNull(first);
        Assert.Null(WorkspaceLock.TryAcquire(fs, ws));

        first.Dispose();
        using var third = WorkspaceLock.TryAcquire(fs, ws);
        Assert.NotNull(third);
    }
}
