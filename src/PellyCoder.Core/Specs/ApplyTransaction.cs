using PellyCoder.Core.Files;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Specs;

/// <summary>
/// Files an apply has touched: the first original of each (backed up once, first backup wins) and the last content
/// pelly wrote. Rollback restores through <see cref="FileGuard"/>, whose SHA check refuses a file changed outside pelly.
/// </summary>
public sealed class ApplyTransaction(FileGuard guard, Workspace ws, string applyId)
{
    private sealed class Entry(FileSnapshot original, string backupPath)
    {
        public FileSnapshot Original { get; } = original;
        public string BackupPath { get; } = backupPath;
        public FileSnapshot LastKnown { get; set; } = original;
    }

    private readonly Dictionary<string, Entry> _files = new(StringComparer.Ordinal);

    public void Track(FileSnapshot original)
    {
        if (_files.ContainsKey(original.Path)) return;
        string backup = guard.Backup(ws, applyId, original);
        _files[original.Path] = new Entry(original, backup);
    }

    public void Update(FileSnapshot lastWritten)
    {
        if (_files.TryGetValue(lastWritten.Path, out var entry)) entry.LastKnown = lastWritten;
    }

    /// <summary>Restores every changed file; returns the paths that could not be restored (changed outside pelly, locked).</summary>
    public async Task<IReadOnlyList<string>> RollbackAsync(CancellationToken ct)
    {
        var failed = new List<string>();
        foreach (var (path, entry) in _files)
        {
            if (entry.LastKnown.Sha256 == entry.Original.Sha256) continue;
            var result = await guard.RestoreAsync(entry.BackupPath, entry.LastKnown, ct);
            if (result.Status != FileWriteStatus.Written) failed.Add(path);
            else entry.LastKnown = entry.Original;
        }
        return failed;
    }

    /// <summary>Files whose last written content differs from the original.</summary>
    public IReadOnlyList<(string Path, string Before, string After)> Changes() =>
        _files.Values.Where(e => e.LastKnown.Sha256 != e.Original.Sha256)
            .Select(e => (e.Original.Path, e.Original.Text, e.LastKnown.Text)).ToList();
}
