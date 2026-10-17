using System.IO.Abstractions;

namespace PellyCoder.Core.Workspaces;

public static class WorkspaceLock
{
    /// <summary>Opens .pelly/lock exclusively. Returns null when another run already holds it.</summary>
    public static IDisposable? TryAcquire(IFileSystem fs, Workspace ws)
    {
        fs.Directory.CreateDirectory(ws.PellyDir);
        var path = fs.Path.Combine(ws.PellyDir, "lock");
        try
        {
            var stream = fs.FileStream.New(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new Handle(stream);
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return null;
        }
    }

    // Only a lock conflict means "another run is active"; disk-full, bad path or read-only volume must propagate.
    // Windows: ERROR_SHARING_VIOLATION (32) / ERROR_LOCK_VIOLATION (33).
    // Unix: .NET surfaces the failed flock as the raw errno EWOULDBLOCK (observed 0x23 = 35 on macOS;
    // 11 = EAGAIN on Linux is expected but not verified here).
    private static bool IsSharingViolation(IOException ex) =>
        OperatingSystem.IsWindows()
            ? (ex.HResult & 0xFFFF) is 32 or 33
            : ex.HResult is 11 or 35;

    private sealed class Handle(Stream stream) : IDisposable
    {
        public void Dispose() => stream.Dispose();
    }
}
