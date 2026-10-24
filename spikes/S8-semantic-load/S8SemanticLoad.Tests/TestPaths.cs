/// <summary>Locates repo folders from the test binary by walking up to global.json.</summary>
public static class TestPaths
{
    public static string RepoRoot { get; } = FindRoot();
    public static string Spike => Path.Combine(RepoRoot, "spikes", "S8-semantic-load");
    public static string Corpus => Path.Combine(RepoRoot, "eval", "corpus");
    public static string S6Cases => Path.Combine(RepoRoot, "spikes", "S6-planning", "cases");
    public static string Fixture(params string[] parts) =>
        Path.Combine([Spike, "S8SemanticLoad.Tests", "Fixtures", .. parts]);

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("global.json not found above test binary");
    }
}
