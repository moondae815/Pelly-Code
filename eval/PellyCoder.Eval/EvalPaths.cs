namespace PellyCoder.Eval;

/// <summary>Finds the repository's built-in suite (<c>eval/benchmark</c>, the folder holding <c>suite.json</c>).</summary>
public static class EvalPaths
{
    /// <summary>The first <c>eval/benchmark</c> with a <c>suite.json</c> found walking up from <paramref name="start"/>, or null.</summary>
    public static string? FindSuite(string start)
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "eval", "benchmark");
            if (File.Exists(Path.Combine(candidate, BenchmarkSuite.FileName)))
                return candidate;
        }
        return null;
    }

    /// <summary>From the current directory, then from the executable's folder.</summary>
    public static string? FindSuite() => FindSuite(Directory.GetCurrentDirectory()) ?? FindSuite(AppContext.BaseDirectory);
}
