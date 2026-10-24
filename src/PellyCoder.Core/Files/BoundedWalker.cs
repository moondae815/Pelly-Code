using System.IO.Abstractions;

namespace PellyCoder.Core.Files;

/// <summary>
/// A finished <see cref="BoundedWalker.Collect"/>: the files found, whether the directory cap cut the walk short, and the
/// directories that could not be listed.
/// </summary>
public sealed record WalkResult(IReadOnlyList<string> Files, bool Truncated, IReadOnlyList<string> Unreadable);

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
        WalkCore(fs, root, pattern, maxDirectories, new State(false), ct);

    /// <summary>
    /// The same walk, run to the end, for scans that must know what they missed (a group's affected projects): reports a walk
    /// the cap cut short and every directory it could not list. With <paramref name="enterPackagesWithProjects"/> a
    /// <c>packages</c> folder is entered when it or one of its subfolders holds a <c>.csproj</c> (a project, not a NuGet cache).
    /// </summary>
    public static WalkResult Collect(IFileSystem fs, string root, string pattern, int maxDirectories, bool enterPackagesWithProjects = false,
        CancellationToken ct = default)
    {
        var state = new State(enterPackagesWithProjects);
        var files = WalkCore(fs, root, pattern, maxDirectories, state, ct).ToList();
        return new WalkResult(files, state.Truncated, state.Unreadable);
    }

    private sealed class State(bool enterPackagesWithProjects)
    {
        public int Directories;
        public bool Truncated;
        public List<string> Unreadable { get; } = [];
        public bool EnterPackagesWithProjects { get; } = enterPackagesWithProjects;
    }

    private static IEnumerable<string> WalkCore(IFileSystem fs, string dir, string pattern, int maxDirectories, State state, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (state.Directories >= maxDirectories)
        {
            state.Truncated = true;
            yield break;
        }
        state.Directories++;

        string[] files, dirs;
        try
        {
            files = fs.Directory.GetFiles(dir, pattern);
            dirs = fs.Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.Unreadable.Add(dir);
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
            if (state.Directories >= maxDirectories)
            {
                // Only a folder the walk would have entered makes it incomplete.
                if (!Skipped(fs, sub, state)) state.Truncated = true;
                yield break;
            }
            if (Skipped(fs, sub, state)) continue;
            foreach (var file in WalkCore(fs, sub, pattern, maxDirectories, state, ct))
                yield return file;
        }
    }

    private static bool Skipped(IFileSystem fs, string dir, State state)
    {
        var name = fs.Path.GetFileName(dir.TrimEnd('/', '\\'));
        if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)
            && !(state.EnterPackagesWithProjects && string.Equals(name, "packages", StringComparison.OrdinalIgnoreCase) && HoldsProject(fs, dir, state)))
            return true;
        return IsLink(fs, dir);
    }

    /// <summary>A <c>.csproj</c> directly in <paramref name="dir"/> or in one of its subfolders; an unlistable folder is reported.</summary>
    private static bool HoldsProject(IFileSystem fs, string dir, State state)
    {
        try
        {
            return fs.Directory.GetFiles(dir, "*.csproj").Length > 0
                || fs.Directory.GetDirectories(dir).Any(sub => fs.Directory.GetFiles(sub, "*.csproj").Length > 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.Unreadable.Add(dir);
            return false;
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
