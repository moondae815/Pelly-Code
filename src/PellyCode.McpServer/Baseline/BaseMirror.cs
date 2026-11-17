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

            foreach (var rel in head.Keys)
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
                fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(dest)!);
                MakeWritable(dest);
                fs.File.WriteAllBytes(dest, bytes);
                copied++;
            }

            return new MirrorResult(true, null, copied, deleted);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new MirrorResult(false, $"HEAD 기준 사본을 만들지 못했습니다: {e.Message}", copied, deleted);
        }
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
