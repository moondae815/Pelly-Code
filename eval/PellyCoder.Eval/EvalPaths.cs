namespace PellyCoder.Eval;

/// <summary>Finds the repository's <c>eval/</c> folder (the one holding <c>benchmark/tasks</c> and <c>corpus</c>).</summary>
public static class EvalPaths
{
    /// <summary>The first <c>eval/</c> found walking up from <paramref name="start"/>, or null.</summary>
    public static string? FindRoot(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "eval");
            if (Directory.Exists(Path.Combine(candidate, "benchmark", "tasks")) && Directory.Exists(Path.Combine(candidate, "corpus")))
                return candidate;
        }
        return null;
    }

    /// <summary>From the current directory, then from the executable's folder.</summary>
    public static string? FindRoot() => FindRoot(Directory.GetCurrentDirectory()) ?? FindRoot(AppContext.BaseDirectory);

    public static string TasksDir(string evalRoot) => Path.Combine(evalRoot, "benchmark", "tasks");

    /// <summary>A task's <c>file</c> (relative to <c>eval/</c>, forward slashes) as a path under <paramref name="root"/>.</summary>
    public static string Resolve(string root, string relative) => Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
}
