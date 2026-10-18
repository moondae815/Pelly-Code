namespace PellyCoder.Eval;

/// <summary>Copies corpus folders into a task's work folder so runs never touch <c>eval/corpus</c>.</summary>
public static class WorkCopy
{
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase) { "bin", "obj", ".pelly", "TestResults" };

    /// <summary>Recursive copy without build output (<c>bin</c>, <c>obj</c>, <c>TestResults</c>) and pelly state (<c>.pelly</c>).</summary>
    public static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var dir in Directory.GetDirectories(from))
        {
            var name = Path.GetFileName(dir);
            if (Excluded.Contains(name)) continue;
            CopyDirectory(dir, Path.Combine(to, name));
        }
    }

    /// <summary>
    /// Builds <c>&lt;workRoot&gt;</c> as a miniature <c>eval/corpus</c>: the corpus root's <c>Directory.*.props</c> and the repository's <c>global.json</c> (so the
    /// repository's own build settings — central package management, warnings as errors — do not leak into the copy when
    /// the output folder is inside the repository) and the task's top-level corpus folder (<c>Modern</c>, <c>Legacy</c>,
    /// <c>Sql</c>). An empty <c>.svn</c> folder marks the work root as a workspace, so a run on a folder without a
    /// solution (Sql) keeps its <c>.pelly</c> here instead of walking up to the repository's <c>.git</c>.
    /// </summary>
    /// <returns>The copied target file.</returns>
    public static string Prepare(string evalRoot, BenchmarkTask task, string workRoot)
    {
        var parts = task.File.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 || parts[0] != "corpus")
            throw new InvalidDataException($"{task.Id}: file은 corpus/<폴더>/... 형식이어야 합니다: {task.File}");

        var corpus = Path.Combine(evalRoot, "corpus");
        Directory.CreateDirectory(workRoot);
        foreach (var props in Directory.GetFiles(corpus, "Directory.*.props"))
            File.Copy(props, Path.Combine(workRoot, Path.GetFileName(props)));
        // Same SDK and test runner (Microsoft.Testing.Platform) as eval/corpus in the repository, wherever --out points.
        var globalJson = Path.Combine(evalRoot, "..", "global.json");
        if (File.Exists(globalJson))
            File.Copy(globalJson, Path.Combine(workRoot, "global.json"));
        CopyDirectory(Path.Combine(corpus, parts[1]), Path.Combine(workRoot, parts[1]));
        Directory.CreateDirectory(Path.Combine(workRoot, ".svn"));
        return Path.Combine([workRoot, .. parts[1..]]);
    }

    /// <summary>
    /// Copies the judge test projects of a top-level corpus folder (<c>eval/benchmark/judges/&lt;folder&gt;</c>) to
    /// <c>&lt;workRoot&gt;/judges/&lt;folder&gt;</c>, beside the copied corpus, so they are built against the work copy and never
    /// in place. Kept outside the corpus so the corpus build, CI and pelly <c>--test</c> never see them (R2).
    /// </summary>
    /// <returns>The copied folder, or null when the corpus folder has no judge projects.</returns>
    public static string? CopyJudges(string evalRoot, string corpusFolder, string workRoot)
    {
        var source = Path.Combine(evalRoot, "benchmark", "judges", corpusFolder);
        if (!Directory.Exists(source)) return null;
        var target = Path.Combine(workRoot, "judges", corpusFolder);
        if (!Directory.Exists(target)) CopyDirectory(source, target);
        return target;
    }
}
