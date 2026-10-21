using PellyCoder.Core.Llm;

namespace S6Planning;

public static class Program
{
    private const string Usage = "usage: index <caseId> | run --arm A|B --repeat N [--case id] | callers | score | report";

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        switch (args[0])
        {
            case "index" when args.Length == 2:
                var gold = GoldLoader.Load(Path.Combine(SpikePaths.Cases, args[1]));
                var rendered = CorpusIndexBuilder.Build(SpikePaths.Corpus, gold.Scope).Render();
                Console.Write(rendered);
                var prompt = PlanPrompt.Raw(QwenChatTemplate.LoadDefault(), gold.Request, CorpusIndexBuilder.Build(SpikePaths.Corpus, gold.Scope), think: true);
                Console.WriteLine($"tokens={QwenTokenCounter.Shared.Count(rendered)} prompt={PlanPrompt.CountTokens(prompt)}");
                return 0;
            default:
                Console.Error.WriteLine(Usage);
                return 2;
        }
    }
}

/// <summary>Spike folders, found by walking up from the working directory to global.json.</summary>
public static class SpikePaths
{
    public static string RepoRoot { get; } = FindRoot();
    public static string Spike => Path.Combine(RepoRoot, "spikes", "S6-planning");
    public static string Cases => Path.Combine(Spike, "cases");
    public static string Raw => Path.Combine(Spike, "raw");
    public static string Results => Path.Combine(Spike, "results");
    public static string Corpus => Path.Combine(RepoRoot, "eval", "corpus");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "global.json")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Run inside the Pelly-Code repository (global.json not found).");
    }
}
