namespace PellyCoder.Eval;

/// <summary>Copies a suite root into a task's work folder so runs never touch the original sources.</summary>
public static class WorkCopy
{
    /// <summary>Left out at any depth (case ignored): build output, pelly state, test results, VCS and IDE folders.</summary>
    private static readonly string[] Excluded = ["bin", "obj", ".pelly", "TestResults", ".git", ".vs"];

    /// <summary>
    /// A <c>.git</c> file (git worktree, submodule) is a workspace marker for <c>WorkspaceLocator</c>: copied, it would make a
    /// stray workspace root inside the work copy. Left out at any depth (case ignored), like the <c>.git</c> folder.
    /// </summary>
    private const string GitMarker = ".git";

    /// <summary>
    /// Recursive copy without the folders named in <see cref="Excluded"/> and <paramref name="exclude"/> (names, case ignored,
    /// at any depth) and without <c>.git</c> files.
    /// </summary>
    public static void CopyDirectory(string from, string to, IReadOnlyCollection<string>? exclude = null)
    {
        var skip = new HashSet<string>(Excluded.Concat(exclude ?? []), StringComparer.OrdinalIgnoreCase);
        Copy(from, to, skip);
    }

    private static void Copy(string from, string to, HashSet<string> skip)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, GitMarker, StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(to, name));
        }
        foreach (var dir in Directory.GetDirectories(from))
        {
            var name = Path.GetFileName(dir);
            if (skip.Contains(name)) continue;
            Copy(dir, Path.Combine(to, name), skip);
        }
    }

    /// <summary>
    /// Builds <c>&lt;workRoot&gt;</c>: the suite's support files at the top by file name (the built-in suite's <c>Directory.*.props</c>
    /// and the repository's <c>global.json</c>, so the repository's own build settings do not leak into a copy inside the
    /// repository) and the task's root at <c>&lt;workRoot&gt;/&lt;root name&gt;/</c>. An empty <c>.svn</c> folder marks the work
    /// root as a workspace, so a run on a folder without a solution (Sql) keeps its <c>.pelly</c> here instead of walking up
    /// to the repository's <c>.git</c>.
    /// </summary>
    /// <returns>The copied target file.</returns>
    public static string Prepare(BenchmarkSuite suite, SuiteTask task, string workRoot)
    {
        Directory.CreateDirectory(workRoot);
        foreach (var support in suite.SupportFiles)
            File.Copy(support, Path.Combine(workRoot, Path.GetFileName(support)));
        CopyDirectory(task.Root.Path, Path.Combine(workRoot, task.Root.Name), task.Root.Exclude);
        Directory.CreateDirectory(Path.Combine(workRoot, ".svn"));
        return Path.Combine([workRoot, task.Root.Name, .. task.RelativeFile.Split('/')]);
    }

    /// <summary>
    /// Copies the judge test projects of a root (<see cref="BenchmarkSuite.JudgesDir"/>) to <c>&lt;workRoot&gt;/judges/&lt;root&gt;</c>,
    /// beside the copied root, so they are built against the work copy and never in place.
    /// </summary>
    /// <returns>The copied folder, or null when the root has no judge projects.</returns>
    public static string? CopyJudges(BenchmarkSuite suite, SuiteRoot root, string workRoot)
    {
        var source = suite.JudgesDir(root);
        if (!Directory.Exists(source)) return null;
        var target = Path.Combine(workRoot, "judges", root.Name);
        if (!Directory.Exists(target)) CopyDirectory(source, target);
        return target;
    }
}
