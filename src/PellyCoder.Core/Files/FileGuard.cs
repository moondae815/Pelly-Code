using System.IO.Abstractions;
using System.Runtime.Versioning;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Files;

public enum FileWriteStatus { Written, ExternallyChanged, Locked, Unencodable }

public sealed record FileWriteResult(FileWriteStatus Status, FileSnapshot? Snapshot, string? Detail);

/// <summary>인코딩·줄바꿈을 보존하는 원자적 쓰기, 백업/복원, 외부 변경 감지 (설계 §8.8).</summary>
public sealed class FileGuard(IFileSystem fs, TimeProvider time)
{
    private const int MaxAttempts = 5;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(200);

    public FileSnapshot Read(string path) => TextFileCodec.Read(fs, path);

    /// <summary>디스크의 원본 바이트를 그대로 <c>.pelly/backup/&lt;runId&gt;/&lt;루트 기준 상대 경로&gt;</c>에 복사한다.</summary>
    public string Backup(Workspace ws, string runId, FileSnapshot original)
    {
        string rel = fs.Path.GetRelativePath(ws.Root, original.Path);
        if (fs.Path.IsPathRooted(rel) || rel == ".." || rel.StartsWith(".." + fs.Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new ArgumentException($"File is outside the workspace root: {original.Path}", nameof(original));

        string dest = fs.Path.Combine(ws.PellyDir, "backup", runId, rel);
        // 첫 백업이 이긴다: 같은 실행에서 두 번째 호출이 원본을 pelly의 수정본으로 덮으면 롤백이 원본을 잃는다.
        if (fs.File.Exists(dest)) return dest;

        byte[] bytes = fs.File.ReadAllBytes(original.Path);
        if (TextFileCodec.HashHex(bytes) != original.Sha256)
            throw new InvalidOperationException($"File changed before backup: {original.Path}");

        fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(dest)!);
        // 임시 파일 + 이동: 중단돼도 잘린 파일이 「원본」으로 남지 않는다.
        string tmp = dest + ".pelly-tmp";
        try
        {
            fs.File.WriteAllBytes(tmp, bytes);
            fs.File.Move(tmp, dest);
        }
        finally
        {
            if (fs.File.Exists(tmp)) fs.File.Delete(tmp);
        }
        return dest;
    }

    public async Task<FileWriteResult> WriteAsync(FileSnapshot current, string newTextLf, CancellationToken ct)
    {
        byte[] bytes;
        try
        {
            bytes = TextFileCodec.Encode(current, newTextLf);
        }
        catch (UnencodableTextException ex)
        {
            return new FileWriteResult(FileWriteStatus.Unencodable, null, ex.Message);
        }

        var (status, detail) = await ReplaceAsync(current.Path, current.Sha256, bytes, ct);
        if (status != FileWriteStatus.Written) return new FileWriteResult(status, null, detail);

        var written = current with
        {
            Sha256 = TextFileCodec.HashHex(bytes),
            Text = TextFileCodec.NormalizeTail(current, newTextLf),
        };
        return new FileWriteResult(FileWriteStatus.Written, written, null);
    }

    public async Task<FileWriteResult> RestoreAsync(string backupPath, FileSnapshot lastKnown, CancellationToken ct)
    {
        byte[] bytes = fs.File.ReadAllBytes(backupPath);
        var (status, detail) = await ReplaceAsync(lastKnown.Path, lastKnown.Sha256, bytes, ct);
        if (status == FileWriteStatus.ExternallyChanged)
            detail = $"{detail} Original is kept at {backupPath}.";
        if (status != FileWriteStatus.Written) return new FileWriteResult(status, null, detail);
        return new FileWriteResult(FileWriteStatus.Written, TextFileCodec.Decode(lastKnown.Path, bytes), null);
    }

    /// <summary>심볼릭 링크면 최종 대상 파일 경로, 아니면 그대로. 링크 자체는 건드리지 않는다.</summary>
    private string ResolveFinalPath(string path)
    {
        try
        {
            var target = fs.File.ResolveLinkTarget(path, returnFinalTarget: true);
            return target?.FullName ?? path;
        }
        catch (IOException) { return path; }
        catch (UnauthorizedAccessException) { return path; }
    }

    private async Task<(FileWriteStatus Status, string? Detail)> ReplaceAsync(string userPath, string expectedSha, byte[] bytes, CancellationToken ct)
    {
        // 읽기·SHA 검사·임시 파일·교체는 모두 최종 대상 파일에서 한다(링크는 그대로 둔다).
        string path = ResolveFinalPath(userPath);
        string dir = fs.Path.GetDirectoryName(path)!;
        string tmp = fs.Path.Combine(dir, "." + fs.Path.GetFileName(path) + ".pelly-tmp");
        bool changed = false;
        try
        {
            // 임시 파일 쓰기도 일시적 공유 위반이 날 수 있어 같은 재시도 안에서 처리한다.
            bool ok = await SharingViolationRetry.RunAsync(() =>
            {
                changed = !fs.File.Exists(path) || TextFileCodec.HashHex(fs.File.ReadAllBytes(path)) != expectedSha;
                if (changed) return;
                fs.File.WriteAllBytes(tmp, bytes);
                if (!OperatingSystem.IsWindows()) CopyUnixMode(path, tmp);
                if (OperatingSystem.IsWindows()) fs.File.Replace(tmp, path, null);
                else fs.File.Move(tmp, path, overwrite: true);
            }, MaxAttempts, RetryDelay, time, ct);

            if (!ok) return (FileWriteStatus.Locked, $"File is locked by another process: {path}");
            if (changed) return (FileWriteStatus.ExternallyChanged, $"File changed on disk since it was read: {path}.");
            return (FileWriteStatus.Written, null);
        }
        finally
        {
            try { if (fs.File.Exists(tmp)) fs.File.Delete(tmp); }
            catch (IOException) { /* 정리 실패는 쓰기 결과를 바꾸지 않는다 */ }
            catch (UnauthorizedAccessException) { }
        }
    }

    // Unix 전용: 교체 전에 원래 파일의 모드를 임시 파일에 옮겨 실행 비트 등이 사라지지 않게 한다.
    [UnsupportedOSPlatform("windows")]
    private void CopyUnixMode(string from, string to)
    {
        try { fs.File.SetUnixFileMode(to, fs.File.GetUnixFileMode(from)); }
        catch (PlatformNotSupportedException) { /* 모드 개념이 없는 파일 시스템: 그대로 둔다 */ }
    }
}
