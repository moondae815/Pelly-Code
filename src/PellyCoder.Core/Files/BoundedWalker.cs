using System.IO.Abstractions;

namespace PellyCoder.Core.Files;

/// <summary>
/// The one directory walk the workspace scans share (DTO collection, test-project discovery): files of a directory first, then
/// its subdirectories, both in ordinal order. Never enters build output or tool folders, symlinks, junctions or other reparse
/// points; skips a directory it cannot list; stops listing after <c>maxDirectories</c> directories.
/// </summary>
public static class BoundedWalker
{
    private static readonly string[] SkippedDirectories = ["bin", "obj", ".pelly", ".git", ".vs", "node_modules", "packages", "TestResults"];

    /// <summary>Lazily yields the files matching <paramref name="pattern"/> under <paramref name="root"/>.</summary>
    public static IEnumerable<string> Files(IFileSystem fs, string root, string pattern, int maxDirectories, CancellationToken ct = default) =>
        WalkCore(fs, root, pattern, maxDirectories, new Counter(), ct);

    private sealed class Counter { public int Directories; }

    private static IEnumerable<string> WalkCore(IFileSystem fs, string dir, string pattern, int maxDirectories, Counter counter, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (counter.Directories >= maxDirectories) yield break;
        counter.Directories++;

        string[] files, dirs;
        try
        {
            files = fs.Directory.GetFiles(dir, pattern);
            dirs = fs.Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            yield break;
        }

        Array.Sort(files, StringComparer.Ordinal);
        Array.Sort(dirs, StringComparer.Ordinal);
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            yield return file;
        }

        foreach (var sub in dirs)
        {
            ct.ThrowIfCancellationRequested();
            if (counter.Directories >= maxDirectories) yield break;
            var name = fs.Path.GetFileName(sub.TrimEnd('/', '\\'));
            if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase) || IsLink(fs, sub)) continue;
            foreach (var file in WalkCore(fs, sub, pattern, maxDirectories, counter, ct))
                yield return file;
        }
    }

    /// <summary>Symlinks, junctions and other reparse points are not entered (cycles); an unreadable entry counts as a link.</summary>
    private static bool IsLink(IFileSystem fs, string dir)
    {
        try
        {
            var info = fs.DirectoryInfo.New(dir);
            return info.Attributes.HasFlag(FileAttributes.ReparsePoint) || info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return true;
        }
    }
}
