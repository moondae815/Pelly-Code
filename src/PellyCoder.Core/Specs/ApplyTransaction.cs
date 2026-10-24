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

    /// <summary>A file as the open group first saw it: the content pelly knew then, and its group backup.</summary>
    private sealed record GroupStart(Entry Entry, FileSnapshot Start, string? BackupPath);

    private readonly Dictionary<string, Entry> _files = new(pathComparer ?? SpecTaskPaths.Comparer);
    private readonly Dictionary<string, GroupStart> _group = new(pathComparer ?? SpecTaskPaths.Comparer);
    private readonly HashSet<string> _usedGroupIds = new(StringComparer.Ordinal);
    private string? _groupId;

    /// <summary>
    /// Before a task runs: backs the file up the first time it is seen (throws when the backup fails). For a file
    /// already tracked, an <paramref name="original"/> other than the last known content marks it changed outside pelly.
    /// </summary>
    public void Track(FileSnapshot original)
    {
        if (_files.TryGetValue(original.Path, out var entry))
        {
            Observe(entry, original);
            if (_groupId is not null && !_group.ContainsKey(original.Path))
            {
                // A file changed outside pelly is never restored, so its bytes are not worth a group backup.
                string? groupBackup = entry.ChangedOutside ? null : guard.Backup(ws, GroupRunId, original);
                _group[original.Path] = new GroupStart(entry, entry.LastKnown, groupBackup);
            }
            return;
        }
        string backup = guard.Backup(ws, applyId, original);
        entry = new Entry(original, backup);
        _files[original.Path] = entry;
        if (_groupId is not null) _group[original.Path] = new GroupStart(entry, original, guard.Backup(ws, GroupRunId, original));
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
            entry = new Entry(original, TryBackup(applyId, original));
            _files[original.Path] = entry;
        }
        if (_groupId is not null && !_group.ContainsKey(original.Path))
        {
            string? groupBackup = entry.ChangedOutside ? null : TryBackup(GroupRunId, original);
            _group[original.Path] = new GroupStart(entry, entry.LastKnown, groupBackup);
        }
        // The entry keeps one spelling of the path, so a task path differing only in case restores the same file.
        if (final is not null) entry.LastKnown = final with { Path = entry.Original.Path };
    }

    private string? TryBackup(string runId, FileSnapshot original)
    {
        try { return guard.Backup(ws, runId, original); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    private string GroupRunId => $"{applyId}/{_groupId}";

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

    /// <summary>
    /// Opens a checkpoint: from now on each file is backed up, the first time the group sees it, to
    /// <c>.pelly/backup/&lt;apply-id&gt;/&lt;groupId&gt;/</c> as it was then (which may include earlier tasks' edits).
    /// A still-open group is closed. A group id is used once per apply: a second group under the same id would find
    /// the first one's backups (first backup wins) and restore the wrong bytes.
    /// </summary>
    public void BeginGroup(string groupId)
    {
        if (groupId.Length == 0 || groupId is "." or ".." || groupId.IndexOfAny(['/', '\\']) >= 0)
            throw new ArgumentException($"Group id must be one path segment: '{groupId}'", nameof(groupId));
        if (!_usedGroupIds.Add(groupId))
            throw new InvalidOperationException($"Group id already used in this apply: {groupId}");
        _group.Clear();
        _groupId = groupId;
    }

    /// <summary>Closes the open group; its changes stay part of the apply.</summary>
    public void EndGroup()
    {
        _group.Clear();
        _groupId = null;
    }

    /// <summary>
    /// Restores every file the open group changed to its content at group start; returns the paths that could not
    /// be restored (same rules as <see cref="RollbackAsync"/>). A restored file's last known content becomes its
    /// group-start content, so a later <see cref="RollbackAsync"/> still restores the apply original.
    /// </summary>
    public async Task<IReadOnlyList<string>> RollbackGroupAsync(CancellationToken ct)
    {
        var failed = new List<string>();
        foreach (var (path, (entry, start, backupPath)) in _group)
        {
            if (entry.LastKnown.Sha256 == start.Sha256) continue;
            if (entry.ChangedOutside || backupPath is null)
            {
                failed.Add(path);
                continue;
            }
            try
            {
                var result = await guard.RestoreAsync(backupPath, entry.LastKnown, ct);
                if (result.Status != FileWriteStatus.Written) failed.Add(path);
                else entry.LastKnown = start;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                failed.Add(path);
            }
        }
        return failed;
    }

    /// <summary>Files the open group changed: content at group start → last known content.</summary>
    public IReadOnlyList<(string Path, string Before, string After)> GroupChanges() =>
        _group.Values.Where(g => g.Entry.LastKnown.Sha256 != g.Start.Sha256)
            .Select(g => (g.Entry.Original.Path, g.Start.Text, g.Entry.LastKnown.Text)).ToList();

    /// <summary>Files whose last known content differs from the original.</summary>
    public IReadOnlyList<(string Path, string Before, string After)> Changes() =>
        _files.Values.Where(e => e.LastKnown.Sha256 != e.Original.Sha256)
            .Select(e => (e.Original.Path, e.Original.Text, e.LastKnown.Text)).ToList();
}

/// <summary>How an apply names a task's file: one full path and one key comparer for every caller.</summary>
public static class SpecTaskPaths
{
    /// <summary>Case-insensitive on Windows and macOS (their default file systems), ordinal elsewhere.</summary>
    public static StringComparer Comparer => PathComparers.ForOs;

    /// <summary>The task path (either separator) under <paramref name="root"/>, made full as the FSM preflight makes it.</summary>
    public static string FullPath(IPath path, string root, string taskPath) =>
        path.GetFullPath(path.Combine(root, taskPath.Replace('\\', '/').Replace('/', path.DirectorySeparatorChar)));
}
