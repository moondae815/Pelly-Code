/// <summary>Locates repo folders from the test binary by walking up to global.json.</summary>
public static class TestPaths
{
    public static string RepoRoot { get; } = FindRoot();
    public static string Spike => Path.Combine(RepoRoot, "spikes", "S6-planning");
    public static string Cases => Path.Combine(Spike, "cases");
    public static string Corpus => Path.Combine(RepoRoot, "eval", "corpus");
    public static string Fixture(string name) => Path.Combine(Spike, "S6Planning.Tests", "Fixtures", name);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("global.json not found above test binary");
    }
}

public static class StringPartition
{
    public static (string Before, string Sep, string After) Partition(this string s, char c)
    {
        var i = s.IndexOf(c);
        return i < 0 ? (s, "", "") : (s[..i], c.ToString(), s[(i + 1)..]);
    }
}
