using System.IO.Abstractions;

namespace PellyCoder.Core.Workspace;

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
        catch (IOException)
        {
            return null;
        }
    }

    private sealed class Handle(Stream stream) : IDisposable
    {
        public void Dispose() => stream.Dispose();
    }
}
