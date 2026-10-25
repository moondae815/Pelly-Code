using System.IO.Abstractions;

namespace PellyCoder.Core.Files;

/// <summary>
/// A finished <see cref="BoundedWalker.Collect"/>: the files found, whether the directory cap cut the walk short, the
/// directories that could not be listed (or whose attributes could not be read), and — in scope-discovery mode — the link
/// folders whose target is outside the walked root or cannot be resolved.
/// </summary>
public sealed record WalkResult(IReadOnlyList<string> Files, bool Truncated, IReadOnlyList<string> Unreadable, IReadOnlyList<string> OutsideLinks);

/// <summary>
/// The one directory walk the workspace scans share (DTO collection, test-project discovery, a group's affected projects):
/// files of a directory first, then its subdirectories, both in ordinal order. Never enters build output or tool folders,
/// symlinks, junctions or other reparse points; skips a directory it cannot list; stops listing after <c>maxDirectories</c>
/// directories.
/// </summary>
public static class BoundedWalker
{
    private static readonly string[] SkippedDirectories = ["bin", "obj", ".pelly", ".git", ".vs", "node_modules", "packages", "TestResults"];

    /// <summary>Lazily yields the files matching <paramref name="pattern"/> under <paramref name="root"/>.</summary>
    public static IEnumerable<string> Files(IFileSystem fs, string root, string pattern, int maxDirectories, CancellationToken ct = default) =>
        WalkCore(fs, root, pattern, maxDirectories, new State(false, root), ct);

    /// <summary>
    /// The same walk, run to the end, for scans that must know what they missed: reports a walk the cap cut short and every
    /// directory it could not list. <paramref name="scopeDiscovery"/> (a group's affected projects, which must never miss a
    /// project silently) also reports a folder whose attributes cannot be read and a link folder whose target is outside
    /// <paramref name="root"/> or unknown (a link inside the root is skipped: the walk reaches its target anyway), and walks a
    /// <c>packages</c> folder unless it looks like a NuGet packages folder (see <see cref="IsNuGetPackagesFolder"/>).
    /// </summary>
    public static WalkResult Collect(IFileSystem fs, string root, string pattern, int maxDirectories, bool scopeDiscovery = false,
        CancellationToken ct = default)
    {
        var state = new State(scopeDiscovery, fs.Path.GetFullPath(root));
        var files = WalkCore(fs, root, pattern, maxDirectories, state, ct).ToList();
        return new WalkResult(files, state.Truncated, state.Unreadable, state.OutsideLinks);
    }

    private sealed class State(bool scopeDiscovery, string root)
    {
        public int Directories;
        public bool Truncated;
        public List<string> Unreadable { get; } = [];
        public List<string> OutsideLinks { get; } = [];
        public bool ScopeDiscovery { get; } = scopeDiscovery;
        public string Root { get; } = root;
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
            && !(state.ScopeDiscovery && string.Equals(name, "packages", StringComparison.OrdinalIgnoreCase) && !IsNuGetPackagesFolder(fs, dir, state)))
            return true;
        return IsLink(fs, dir, state);
    }

    /// <summary>
    /// A packages.config restore folder: it has subfolders and every one of them holds a <c>.nupkg</c>. "Every", not "any":
    /// a wrong guess must only ever walk more (a project folder with one stray package is still walked; a NuGet folder with
    /// an odd extra folder is walked too, which at worst costs directories toward the cap — and a cut walk is reported).
    /// An unlistable folder is reported.
    /// </summary>
    private static bool IsNuGetPackagesFolder(IFileSystem fs, string dir, State state)
    {
        try
        {
            var subs = fs.Directory.GetDirectories(dir);
            return subs.Length > 0 && subs.All(sub => fs.Directory.GetFiles(sub, "*.nupkg").Length > 0);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            state.Unreadable.Add(dir);
            return true; // already reported; walking it would report it again
        }
    }

    /// <summary>
    /// Symlinks, junctions and other reparse points are not entered (cycles). Outside scope discovery an unreadable entry
    /// counts as a link and is skipped silently; in scope discovery it is reported as unreadable, and a link whose target is
    /// outside the root or unknown (a reparse point that is not a link) is reported.
    /// </summary>
    private static bool IsLink(IFileSystem fs, string dir, State state)
    {
        bool reparse;
        string? target;
        try
        {
            var info = fs.DirectoryInfo.New(dir);
            reparse = info.Attributes.HasFlag(FileAttributes.ReparsePoint);
            target = info.LinkTarget;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (state.ScopeDiscovery) state.Unreadable.Add(dir);
            return true;
        }

        if (!reparse && target is null) return false;
        if (state.ScopeDiscovery && !TargetInsideRoot(fs, dir, target, state.Root)) state.OutsideLinks.Add(dir);
        return true;
    }

    /// <summary>The link's target (relative targets resolve from the link's folder) is the root or under it.</summary>
    private static bool TargetInsideRoot(IFileSystem fs, string link, string? target, string root)
    {
        if (string.IsNullOrEmpty(target)) return false;
        string full;
        try
        {
            full = fs.Path.GetFullPath(fs.Path.Combine(fs.Path.GetDirectoryName(fs.Path.GetFullPath(link)) ?? root, target));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
        var trimmedRoot = fs.Path.TrimEndingDirectorySeparator(root);
        var trimmed = fs.Path.TrimEndingDirectorySeparator(full);
        var comparison = PathComparers.ForOs == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(trimmed, trimmedRoot, comparison)
            || (trimmed.Length > trimmedRoot.Length && trimmed.StartsWith(trimmedRoot, comparison)
                && (trimmed[trimmedRoot.Length] == fs.Path.DirectorySeparatorChar || trimmed[trimmedRoot.Length] == fs.Path.AltDirectorySeparatorChar));
    }
}
