using System.Security;

namespace PellyCoder.Eval;

/// <summary>Copies a suite root into a task's work folder so runs never touch the original sources.</summary>
public static class WorkCopy
{
    /// <summary>
    /// Left out at any depth (case ignored): build output, pelly state, test results, VCS (git, SVN, Mercurial, TFVC local
    /// workspace) and IDE folders. VCS folders only cost copy time; the work root's own <c>.svn</c> marker is made by <see cref="Prepare"/>.
    /// </summary>
    internal static readonly string[] Excluded = ["bin", "obj", ".pelly", "TestResults", ".git", ".vs", ".svn", ".hg", "$tf"];

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

    /// <summary>Copies <paramref name="from"/> over <paramref name="to"/> (same relative paths, existing files overwritten), with the standard excludes.</summary>
    public static void Overlay(string from, string to) =>
        Copy(from, to, new HashSet<string>(Excluded, StringComparer.OrdinalIgnoreCase), overwrite: true);

    private static void Copy(string from, string to, HashSet<string> skip, bool overwrite = false)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, GitMarker, StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(to, name), overwrite);
        }
        foreach (var dir in Directory.GetDirectories(from))
        {
            var name = Path.GetFileName(dir);
            if (skip.Contains(name)) continue;
            Copy(dir, Path.Combine(to, name), skip, overwrite);
        }
    }

    /// <summary>Project rules file name (pelly reads it at the workspace root); <c>--no-rules</c> removes it everywhere in the copy.</summary>
    public const string RulesFileName = "AGENTS.md";

    private static void RemoveRules(string workRoot)
    {
        foreach (var file in Directory.GetFiles(workRoot, "*", SearchOption.AllDirectories))
            if (string.Equals(Path.GetFileName(file), RulesFileName, StringComparison.OrdinalIgnoreCase))
                File.Delete(file);
    }

    /// <summary>
    /// Builds <c>&lt;workRoot&gt;</c>: the suite's support files at the top by file name (the built-in suite's <c>Directory.*.props</c>
    /// and the repository's <c>global.json</c>, so the repository's own build settings do not leak into a copy inside the
    /// repository) and the task's root at <c>&lt;workRoot&gt;/&lt;root name&gt;/</c>. An empty <c>.svn</c> folder marks the work
    /// root as a workspace, so a run on a folder without a solution (Sql) keeps its <c>.pelly</c> here instead of walking up
    /// to the repository's <c>.git</c>.
    /// </summary>
    /// <param name="removeRules"><c>--no-rules</c>: delete every <c>AGENTS.md</c> (any case, any depth) from the copy, so the same suite runs without project rules.</param>
    /// <returns>The copied target file.</returns>
    public static string Prepare(BenchmarkSuite suite, SuiteTask task, string workRoot, bool removeRules = false)
    {
        Directory.CreateDirectory(workRoot);
        foreach (var support in suite.SupportFiles)
            File.Copy(support, Path.Combine(workRoot, Path.GetFileName(support)));
        var rootCopy = Path.Combine(workRoot, task.Root.Name);
        CopyDirectory(task.Root.Path, rootCopy, task.Root.Exclude);
        if (task.Root.Overlay is { } overlay)
            Overlay(overlay, rootCopy);
        if (removeRules) RemoveRules(workRoot);
        Directory.CreateDirectory(Path.Combine(workRoot, ".svn"));
        return Path.Combine([workRoot, task.Root.Name, .. task.RelativeFile.Split('/')]);
    }

    /// <summary>Folder of the root's work copy that holds the judge projects of a <c>run --test-first</c> (<c>&lt;root&gt;/JudgeTests/</c>).</summary>
    public const string WorkspaceJudgesFolder = "JudgeTests";

    /// <summary>
    /// <c>run --test-first</c>: copies each judge test project of a root (<see cref="BenchmarkSuite.JudgesDir"/>) into the
    /// workspace, <c>&lt;workRoot&gt;/&lt;root&gt;/JudgeTests/&lt;project folder&gt;/</c>, where pelly's test index finds it. Each
    /// copy's <c>Directory.Build.props</c> (imported before the project body; created when missing) pins
    /// <see cref="IndependentVerifier.JudgeTargetRootProperty"/> to the work copy of the root, so both a project reference and
    /// a <c>Compile Include</c> of corpus sources resolve to the edited copy without a <c>-p:</c> argument. A value passed on
    /// the command line still wins (the property is set only when empty).
    /// </summary>
    /// <returns>The copied project folders; empty when the root has no judge projects.</returns>
    public static IReadOnlyList<string> CopyJudgesIntoWorkspace(BenchmarkSuite suite, SuiteRoot root, string workRoot)
    {
        var source = suite.JudgesDir(root);
        if (!Directory.Exists(source)) return [];
        var rootCopy = Path.GetFullPath(Path.Combine(workRoot, root.Name));
        var target = Path.Combine(rootCopy, WorkspaceJudgesFolder);
        var copied = new List<string>();
        foreach (var project in Directory.GetDirectories(source).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(project);
            if (Excluded.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            var to = Path.Combine(target, name);
            CopyDirectory(project, to);
            PinJudgeTargetRoot(Path.Combine(to, "Directory.Build.props"), rootCopy);
            copied.Add(to);
        }
        return copied;
    }

    private static void PinJudgeTargetRoot(string props, string rootCopy)
    {
        var property = IndependentVerifier.JudgeTargetRootProperty;
        var group = $"""
              <!-- pelly-eval run, test-first mode: this copy lives inside the workspace and is built against the work copy of the root. -->
              <PropertyGroup>
                <{property} Condition="'$({property})' == ''">{SecurityElement.Escape(rootCopy)}</{property}>
              </PropertyGroup>

            """;
        if (!File.Exists(props))
        {
            File.WriteAllText(props, "<Project>\n" + group + "</Project>\n");
            return;
        }
        var text = File.ReadAllText(props);
        var end = text.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (end < 0) throw new InvalidDataException($"Directory.Build.props에 </Project>가 없습니다: {props}");
        File.WriteAllText(props, text[..end] + group + text[end..]);
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
