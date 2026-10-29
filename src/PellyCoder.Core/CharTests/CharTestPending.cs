using System.IO.Abstractions;
using System.Text.Json;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.CharTests;

/// <summary>One file a <c>pelly test</c> run touched. <c>Role</c> is "test", "csproj" or "target".</summary>
public sealed record PendingFile(string Path, string Role, bool CreatedNew, string? BackupPath, string? OriginalSha256, bool Mutated);

/// <summary>
/// The <c>chartest.pending</c> marker under <c>.pelly/runs/&lt;runId&gt;/</c>: written before the first file is touched and deleted only
/// after every file is kept or restored. While one exists, cs, sql and apply refuse to run.
/// </summary>
public sealed class CharTestPending(IFileSystem fs)
{
    public const string FileName = "chartest.pending";

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>Writes the marker through a temp file and a move so a reader never sees a half-written one. Returns its path.</summary>
    public string Write(Workspace ws, string runId, IReadOnlyList<PendingFile> files)
    {
        var dir = fs.Path.Combine(ws.PellyDir, "runs", runId);
        fs.Directory.CreateDirectory(dir);
        var path = fs.Path.Combine(dir, FileName);
        var temp = path + ".tmp";
        fs.File.WriteAllText(temp, JsonSerializer.Serialize(files, Json));
        fs.File.Move(temp, path, overwrite: true);
        return path;
    }

    public void Delete(string path)
    {
        if (fs.File.Exists(path)) fs.File.Delete(path);
    }

    /// <summary>Every marker in the workspace. A marker that cannot be read is still reported, with no file list.</summary>
    public IReadOnlyList<(string MarkerPath, IReadOnlyList<PendingFile> Files)> FindAll(Workspace ws)
    {
        var runs = fs.Path.Combine(ws.PellyDir, "runs");
        var found = new List<(string, IReadOnlyList<PendingFile>)>();
        if (!fs.Directory.Exists(runs)) return found;
        foreach (var dir in fs.Directory.EnumerateDirectories(runs).Order(StringComparer.Ordinal))
        {
            var marker = fs.Path.Combine(dir, FileName);
            if (!fs.File.Exists(marker)) continue;
            IReadOnlyList<PendingFile> files;
            try
            {
                files = JsonSerializer.Deserialize<List<PendingFile>>(fs.File.ReadAllText(marker)) ?? [];
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                files = [];
            }
            found.Add((marker, files));
        }
        return found;
    }

    public static string Describe(IReadOnlyList<(string MarkerPath, IReadOnlyList<PendingFile> Files)> found)
    {
        var parts = found.Select(f =>
        {
            var names = f.Files.Count == 0 ? "(파일 목록을 읽을 수 없음)" : string.Join(", ", f.Files.Select(p => p.Path));
            var backups = f.Files.Where(p => p.BackupPath is not null).Select(p => p.BackupPath!).ToList();
            var backupText = backups.Count == 0 ? "없음" : string.Join(", ", backups);
            return $"{names} — 백업 {backupText}. 확인한 뒤 {f.MarkerPath}를 지우세요";
        });
        return "이전 `pelly test` 실행이 끝나지 않았습니다: " + string.Join(" / ", parts);
    }
}
