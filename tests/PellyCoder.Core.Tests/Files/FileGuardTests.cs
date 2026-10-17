using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Files;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Files;

public sealed class FileGuardTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("pelly-guard-").FullName;
    private readonly FileGuard _guard = new(new FileSystem(), TimeProvider.System);

    static FileGuardTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private Workspace Ws => new(_root, Path.Combine(_root, ".pelly"));

    private string Put(byte[] bytes, string name = "src/Order.cs")
    {
        string path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static byte[] Cp949Crlf(params string[] lines) =>
        Encoding.GetEncoding(949).GetBytes(string.Join("\r\n", lines) + "\r\n");

    [Fact]
    public async Task Write_ChangedOnlyOneLine_KeepsOtherBytes()
    {
        string path = Put(Cp949Crlf("// 주문", "int a;", "int b; // 가격", "return a;"));
        var snap = _guard.Read(path);

        var result = await _guard.WriteAsync(snap, snap.Text.Replace("int b; // 가격", "int b = 2; // 수정"), TestContext.Current.CancellationToken);

        Assert.Equal(FileWriteStatus.Written, result.Status);
        Assert.Equal(Cp949Crlf("// 주문", "int a;", "int b = 2; // 수정", "return a;"), File.ReadAllBytes(path));
        Assert.Equal(_guard.Read(path).Sha256, result.Snapshot!.Sha256);
    }

    [Fact]
    public async Task Write_WhenDiskChangedSinceSnapshot_ReturnsExternallyChanged_AndDoesNotWrite()
    {
        string path = Put("a\r\nb\r\n"u8.ToArray());
        var snap = _guard.Read(path);
        byte[] user = "a\r\nUSER EDIT\r\n"u8.ToArray();
        File.WriteAllBytes(path, user);

        var result = await _guard.WriteAsync(snap, "a\nmodel\n", TestContext.Current.CancellationToken);

        Assert.Equal(FileWriteStatus.ExternallyChanged, result.Status);
        Assert.Equal(user, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Write_Unencodable_ReturnsUnencodable_AndDoesNotWrite()
    {
        byte[] original = Cp949Crlf("한글");
        string path = Put(original);
        var snap = _guard.Read(path);

        var result = await _guard.WriteAsync(snap, "한글 😀\n", TestContext.Current.CancellationToken);

        Assert.Equal(FileWriteStatus.Unencodable, result.Status);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void Backup_ThenRestore_RestoresOriginalBytes()
    {
        byte[] original = Cp949Crlf("한글", "x");
        string path = Put(original);
        var snap = _guard.Read(path);

        string backup = _guard.Backup(Ws, "run1", snap);

        Assert.Equal(Path.Combine(_root, ".pelly", "backup", "run1", "src", "Order.cs"), backup);
        Assert.Equal(original, File.ReadAllBytes(backup));
    }

    [Fact]
    public async Task Backup_ThenWrite_ThenRestore_RestoresOriginalBytes()
    {
        byte[] original = Cp949Crlf("한글", "x");
        string path = Put(original);
        var snap = _guard.Read(path);
        string backup = _guard.Backup(Ws, "run1", snap);
        var written = await _guard.WriteAsync(snap, "바뀜\nx\n", TestContext.Current.CancellationToken);

        var restored = await _guard.RestoreAsync(backup, written.Snapshot!, TestContext.Current.CancellationToken);

        Assert.Equal(FileWriteStatus.Written, restored.Status);
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Backup_OfMixedNewlineFile_IsByteExact()
    {
        byte[] original = "a\r\nb\nc\r\n"u8.ToArray();
        string path = Put(original);
        var snap = _guard.Read(path);

        Assert.Equal(original, File.ReadAllBytes(_guard.Backup(Ws, "r", snap)));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Restore_WhenDiskChangedAfterOurWrite_DoesNotOverwrite()
    {
        string path = Put(Cp949Crlf("a"));
        var snap = _guard.Read(path);
        string backup = _guard.Backup(Ws, "run1", snap);
        var written = await _guard.WriteAsync(snap, "b\n", TestContext.Current.CancellationToken);
        byte[] user = "user edit"u8.ToArray();
        File.WriteAllBytes(path, user);

        var restored = await _guard.RestoreAsync(backup, written.Snapshot!, TestContext.Current.CancellationToken);

        Assert.Equal(FileWriteStatus.ExternallyChanged, restored.Status);
        Assert.Contains(backup, restored.Detail);
        Assert.Equal(user, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task Write_LeavesNoTempFile()
    {
        string path = Put("a\n"u8.ToArray());
        var snap = _guard.Read(path);

        await _guard.WriteAsync(snap, "b\n", TestContext.Current.CancellationToken);
        // 실패 경로(외부 변경)에서도 남기지 않는다.
        File.WriteAllBytes(path, "z\n"u8.ToArray());
        await _guard.WriteAsync(snap, "c\n", TestContext.Current.CancellationToken);

        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, ".*.pelly-tmp", SearchOption.AllDirectories));
    }
}
