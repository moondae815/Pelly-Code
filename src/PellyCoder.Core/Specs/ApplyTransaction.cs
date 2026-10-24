using System.IO.Abstractions;
using PellyCoder.Core.Files;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Specs;

/// <summary>
/// Files an apply has touched: the first original of each (backed up once, first backup wins) and the last content
/// pelly wrote. Rollback restores through <see cref="FileGuard"/>, whose SHA check refuses a file changed outside pelly.
/// A task that reads content other than what pelly last knew means the file was edited outside pelly in between; the
/// SHA check can no longer see that edit (pelly wrote over it), so the file is marked and never restored.
/// </summary>
/// <param name="pathComparer">Key comparer for file paths; defaults to <see cref="SpecTaskPaths.Comparer"/>.</param>
public sealed class ApplyTransaction(FileGuard guard, Workspace ws, string applyId, StringComparer? pathComparer = null)
{
    private sealed class Entry(FileSnapshot original, string? backupPath)
    {
        public FileSnapshot Original { get; } = original;

        /// <summary>Null when the original could not be backed up; such a file is never restored.</summary>
        public string? BackupPath { get; } = backupPath;

        public FileSnapshot LastKnown { get; set; } = original;

        public bool ChangedOutside { get; set; } = backupPath is null;
    }

    private readonly Dictionary<string, Entry> _files = new(pathComparer ?? SpecTaskPaths.Comparer);

    /// <summary>
    /// Before a task runs: backs the file up the first time it is seen (throws when the backup fails). For a file
    /// already tracked, an <paramref name="original"/> other than the last known content marks it changed outside pelly.
    /// </summary>
    public void Track(FileSnapshot original)
    {
        if (_files.TryGetValue(original.Path, out var entry))
        {
            Observe(entry, original);
            return;
        }
        string backup = guard.Backup(ws, applyId, original);
        _files[original.Path] = new Entry(original, backup);
    }

    /// <summary>
    /// After a task ran: <paramref name="original"/> is what the task read, <paramref name="final"/> what it left.
    /// Never throws: a file not tracked before the run (its pre-read failed) usually cannot be backed up any more once
    /// the task has written, and is then recorded as not restorable.
    /// </summary>
    public void Record(FileSnapshot original, FileSnapshot? final)
    {
        if (_files.TryGetValue(original.Path, out var entry))
        {
            Observe(entry, original);
        }
        else
        {
            string? backup;
            try { backup = guard.Backup(ws, applyId, original); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
            {
                backup = null;
            }
            entry = new Entry(original, backup);
            _files[original.Path] = entry;
        }
        // The entry keeps one spelling of the path, so a task path differing only in case restores the same file.
        if (final is not null) entry.LastKnown = final with { Path = entry.Original.Path };
    }

    private static void Observe(Entry entry, FileSnapshot read)
    {
        if (read.Sha256 != entry.LastKnown.Sha256) entry.ChangedOutside = true;
    }

    /// <summary>
    /// Restores every changed file; returns the paths that could not be restored (changed outside pelly, locked,
    /// backup missing). One failure never stops the others.
    /// </summary>
    public async Task<IReadOnlyList<string>> RollbackAsync(CancellationToken ct)
    {
        var failed = new List<string>();
        foreach (var (path, entry) in _files)
        {
            if (entry.LastKnown.Sha256 == entry.Original.Sha256) continue;
            if (entry.ChangedOutside || entry.BackupPath is null)
            {
                failed.Add(path);
                continue;
            }
            try
            {
                var result = await guard.RestoreAsync(entry.BackupPath, entry.LastKnown, ct);
                if (result.Status != FileWriteStatus.Written) failed.Add(path);
                else entry.LastKnown = entry.Original;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed.Add(path);
            }
        }
        return failed;
    }

    /// <summary>Files whose last known content differs from the original.</summary>
    public IReadOnlyList<(string Path, string Before, string After)> Changes() =>
        _files.Values.Where(e => e.LastKnown.Sha256 != e.Original.Sha256)
            .Select(e => (e.Original.Path, e.Original.Text, e.LastKnown.Text)).ToList();
}

/// <summary>How an apply names a task's file: one full path and one key comparer for every caller.</summary>
public static class SpecTaskPaths
{
    /// <summary>Case-insensitive on Windows and macOS (their default file systems), ordinal elsewhere.</summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>The task path (either separator) under <paramref name="root"/>, made full as the FSM preflight makes it.</summary>
    public static string FullPath(IPath path, string root, string taskPath) =>
        path.GetFullPath(path.Combine(root, taskPath.Replace('\\', '/').Replace('/', path.DirectorySeparatorChar)));
}
