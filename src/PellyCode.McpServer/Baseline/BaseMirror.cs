using System.IO.Abstractions;
using PellyCode.McpServer.Git;

namespace PellyCode.McpServer.Baseline;

public sealed record MirrorResult(bool Ok, string? FailureReason, int CopiedFiles, int DeletedFiles);

/// <summary>
/// A persistent copy of the workspace (without build/VCS folders) in which files git sees as changed are put back to HEAD.
/// The first sync copies everything; later syncs copy only what differs (size or modified time).
/// </summary>
public sealed class BaseMirror(IFileSystem fs, GitRepo git)
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "obj", ".git", ".pelly", ".vs", ".svn", ".hg", "$tf", "node_modules", "TestResults",
    };

    /// <summary>How many times a sync asks git again for paths changed while it copied the tree.</summary>
    private const int MaxRecheckRounds = 3;

    public async Task<MirrorResult> SyncAsync(string root, string mirrorDir, IReadOnlyList<GitChange> changes, CancellationToken ct)
    {
        var copied = 0;
        var deleted = 0;
        try
        {
            fs.Directory.CreateDirectory(mirrorDir);

            var source = new Dictionary<string, string>(StringComparer.Ordinal); // rel -> full
            Walk(root, "", source, ct);

            // What the mirror must hold from HEAD instead of from the tree.
            var head = new Dictionary<string, bool>(StringComparer.Ordinal); // rel -> put-back-HEAD
            var drop = new HashSet<string>(StringComparer.Ordinal);
            foreach (var c in changes)
            {
                if (IsExcluded(c.Path)) continue;
                switch (c.Kind)
                {
                    case GitChangeKind.Added: drop.Add(c.Path); break;
                    case GitChangeKind.Renamed:
                        drop.Add(c.Path);
                        if (c.OldPath is not null && !IsExcluded(c.OldPath)) head[c.OldPath] = true;
                        break;
                    default: head[c.Path] = true; break;
                }
            }
            foreach (var p in head.Keys) drop.Remove(p);

            // Prune first: a stale entry of the other kind (file where a folder now goes, or the reverse) must be gone before anything is written.
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var rel in source.Keys)
                if (!drop.Contains(rel) && !head.ContainsKey(rel)) keep.Add(rel);
            foreach (var rel in head.Keys) keep.Add(rel);
            deleted += Prune(mirrorDir, mirrorDir, keep, ct);

            foreach (var (rel, full) in source)
            {
                if (!keep.Contains(rel) || head.ContainsKey(rel)) continue;
                var dest = MirrorFile(mirrorDir, rel);
                var si = fs.FileInfo.New(full);
                var di = fs.FileInfo.New(dest);
                if (di.Exists && di.Length == si.Length && di.LastWriteTimeUtc == si.LastWriteTimeUtc) continue;
                fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(dest)!);
                MakeWritable(dest);
                fs.File.Copy(full, dest, overwrite: true);
                MakeWritable(dest); // the copy carries the source's read-only state
                fs.File.SetLastWriteTimeUtc(dest, si.LastWriteTimeUtc);
                copied++;
            }

            var restored = await RestoreAsync(mirrorDir, head.Keys, [], ct);
            copied += restored.Copied;
            deleted += restored.Deleted;

            // The tree was copied after the status above: a file the agent changed in between was copied with its new content.
            // Ask git again and put every path it now names (and the first status did not) back to HEAD, or drop it; repeat
            // a few times while new paths keep showing up.
            var seen = new HashSet<string>(changes.SelectMany(c => new[] { c.Path, c.OldPath }).OfType<string>(), StringComparer.Ordinal);
            for (var round = 0; round < MaxRecheckRounds; round++)
            {
                var later = (await git.ChangesAsync(ct)).Where(c => !seen.Contains(c.Path) || (c.OldPath is { } o && !seen.Contains(o))).ToList();
                if (later.Count == 0) break;
                var laterHead = new List<string>();
                var laterDrop = new List<string>();
                foreach (var c in later)
                {
                    foreach (var p in new[] { c.Path, c.OldPath }.OfType<string>()) seen.Add(p);
                    if (IsExcluded(c.Path)) continue;
                    switch (c.Kind)
                    {
                        case GitChangeKind.Added: laterDrop.Add(c.Path); break;
                        case GitChangeKind.Renamed:
                            laterDrop.Add(c.Path);
                            if (c.OldPath is not null && !IsExcluded(c.OldPath)) laterHead.Add(c.OldPath);
                            break;
                        default: laterHead.Add(c.Path); break;
                    }
                }
                var again = await RestoreAsync(mirrorDir, laterHead, laterDrop.Where(p => !laterHead.Contains(p)), ct);
                copied += again.Copied;
                deleted += again.Deleted;
            }

            return new MirrorResult(true, null, copied, deleted);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new MirrorResult(false, $"HEAD 기준 사본을 만들지 못했습니다: {e.Message}", copied, deleted);
        }
        catch (InvalidOperationException e) // git status failed on the re-check
        {
            return new MirrorResult(false, e.Message, copied, deleted);
        }
    }

    /// <summary>
    /// Puts each of <paramref name="headPaths"/> back to its HEAD bytes in the mirror (deleted when HEAD has no such file) and
    /// deletes each of <paramref name="dropPaths"/> from it.
    /// </summary>
    private async Task<(int Copied, int Deleted)> RestoreAsync(string mirrorDir, IEnumerable<string> headPaths, IEnumerable<string> dropPaths,
        CancellationToken ct)
    {
        int copied = 0, deleted = 0;
        foreach (var rel in dropPaths)
        {
            var dest = MirrorFile(mirrorDir, rel);
            if (fs.File.Exists(dest)) { MakeWritable(dest); fs.File.Delete(dest); deleted++; }
        }
        foreach (var rel in headPaths)
        {
            ct.ThrowIfCancellationRequested();
            var dest = MirrorFile(mirrorDir, rel);
            var bytes = await git.ShowHeadAsync(rel, ct);
            if (bytes is null)
            {
                if (fs.File.Exists(dest)) { MakeWritable(dest); fs.File.Delete(dest); deleted++; }
                continue;
            }
            if (fs.File.Exists(dest) && fs.File.ReadAllBytes(dest).AsSpan().SequenceEqual(bytes)) continue;
            // A folder where HEAD has the file (the tree turned it into a folder after the walk): HEAD wins.
            if (fs.Directory.Exists(dest)) fs.Directory.Delete(dest, recursive: true);
            fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(dest)!);
            MakeWritable(dest);
            fs.File.WriteAllBytes(dest, bytes);
            copied++;
        }
        return (copied, deleted);
    }

    /// <summary>Clears the read-only state of a mirror file so the mirror can always overwrite or delete it (no-op when absent).</summary>
    private void MakeWritable(string path)
    {
        if (!fs.File.Exists(path)) return;
        var a = fs.File.GetAttributes(path);
        if (a.HasFlag(FileAttributes.ReadOnly)) fs.File.SetAttributes(path, a & ~FileAttributes.ReadOnly);
    }

    private string MirrorFile(string mirrorDir, string rel) =>
        fs.Path.Combine(mirrorDir, rel.Replace('/', fs.Path.DirectorySeparatorChar));

    /// <summary>True when a segment of the root-relative path ('/') is a build or VCS folder the mirror leaves out.</summary>
    internal static bool IsExcluded(string rel) =>
        rel.Split('/').Any(s => Excluded.Contains(s));

    private bool IsLink(IFileSystemInfo info) =>
        info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;

    private void Walk(string dir, string rel, Dictionary<string, string> into, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var di = fs.DirectoryInfo.New(dir);
        foreach (var f in di.EnumerateFiles())
        {
            if (IsLink(f)) continue;
            into[rel + f.Name] = f.FullName;
        }
        foreach (var d in di.EnumerateDirectories())
        {
            if (Excluded.Contains(d.Name) || IsLink(d)) continue;
            Walk(d.FullName, rel + d.Name + "/", into, ct);
        }
    }

    /// <summary>Deletes mirror files not in <paramref name="keep"/> and folders left empty. Returns the deleted file count.</summary>
    private int Prune(string mirrorRoot, string dir, HashSet<string> keep, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var n = 0;
        var di = fs.DirectoryInfo.New(dir);
        foreach (var f in di.EnumerateFiles())
        {
            var rel = fs.Path.GetRelativePath(mirrorRoot, f.FullName).Replace(fs.Path.DirectorySeparatorChar, '/');
            if (keep.Contains(rel)) continue;
            MakeWritable(f.FullName);
            f.Delete();
            n++;
        }
        foreach (var d in di.EnumerateDirectories())
        {
            if (IsLink(d)) { d.Delete(); continue; }
            n += Prune(mirrorRoot, d.FullName, keep, ct);
            if (!d.EnumerateFileSystemInfos().Any()) d.Delete();
        }
        return n;
    }
}
