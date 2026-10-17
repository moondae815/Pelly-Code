using System.IO.Abstractions;
using PellyCoder.Core.Files;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.Files;

public sealed class FileGuardWindowsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("pelly-guard-win-").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [WindowsFact]
    public async Task Write_WhileFileOpenedWithShareNone_ReturnsLocked()
    {
        string path = Path.Combine(_dir, "locked.cs");
        byte[] original = "a\r\nb\r\n"u8.ToArray();
        File.WriteAllBytes(path, original);
        var guard = new FileGuard(new FileSystem(), TimeProvider.System);
        // 잠그기 전에 읽어 둔다 (FileShare.None이면 읽기도 막힌다).
        var snapshot = guard.Read(path);

        FileWriteResult result;
        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            result = await guard.WriteAsync(snapshot, "a\nchanged\n", TestContext.Current.CancellationToken);
        }

        Assert.Equal(FileWriteStatus.Locked, result.Status);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Empty(Directory.GetFiles(_dir, ".*.pelly-tmp"));
    }
}
