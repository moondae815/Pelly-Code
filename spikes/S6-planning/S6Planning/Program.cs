using PellyCoder.Core.Llm;

namespace S6Planning;

public static class Program
{
    private const string Usage = "usage: index <caseId> | smoke | run --arm A|B --repeat N [--case id] | callers | score | report";

    private static string? Gate()
    {
        var reason = ApprovalGate.Check(SpikePaths.Cases, Runner.GoldCommit(SpikePaths.Spike), Runner.CasesDirty(SpikePaths.Spike));
        if (reason is not null)
            Console.Error.WriteLine("refused: " + reason);
        return reason;
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static async Task<int> RunCommand(string[] args)
    {
        var arm = Option(args, "--arm");
        if (arm is not ("A" or "B") || !int.TryParse(Option(args, "--repeat"), out var repeat) || repeat < 1)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }
        if (Gate() is not null)
            return 2;

        var endpoint = Environment.GetEnvironmentVariable("PELLY_SPIKE_OLLAMA") ?? "http://localhost:11434";
        var model = Environment.GetEnvironmentVariable("PELLY_SPIKE_MODEL") ?? "qwen3.6:35b";
        using var http = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(10) };
        var digest = await Runner.GetDigestAsync(http, model, CancellationToken.None);
        if (Runner.CheckDigest(digest) is { } bad)
        {
            Console.Error.WriteLine("refused: " + bad);
            return 2;
        }

        var only = Option(args, "--case");
        foreach (var c in GoldLoader.LoadAll(SpikePaths.Cases).Where(c => only is null || c.Id == only))
        {
            var idx = CorpusIndexBuilder.Build(SpikePaths.Corpus, c.Scope);
            for (var rep = 1; rep <= repeat; rep++)
            {
                var path = Runner.RawPath(SpikePaths.Raw, arm, c.Id, rep);
                if (File.Exists(path))
                    continue;
                var r = await Runner.RunOnceAsync(http, model, digest!, c, idx, arm, rep, CancellationToken.None, Runner.GoldCommit(SpikePaths.Spike));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, r.ToJson());
                Console.WriteLine($"{arm} {c.Id} #{rep} {r.Seconds:F1}s done={r.DoneReason} truncated={r.ThinkTruncated}");
            }
        }
        return 0;
    }

    /// <summary>
    /// Exercises the Ollama path once per arm on a request outside the 12 gold cases (benchmark cs-legacy-002), so the
    /// gold answers stay unseen by the model before approval. Prints a summary only; writes nothing under raw/.
    /// </summary>
    private static async Task<int> SmokeCommand()
    {
        var endpoint = Environment.GetEnvironmentVariable("PELLY_SPIKE_OLLAMA") ?? "http://localhost:11434";
        var model = Environment.GetEnvironmentVariable("PELLY_SPIKE_MODEL") ?? "qwen3.6:35b";
        using var http = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromMinutes(10) };
        var digest = await Runner.GetDigestAsync(http, model, CancellationToken.None);
        Console.WriteLine($"digest={digest} check={Runner.CheckDigest(digest) ?? "ok"}");
        var smoke = new GoldCase("smoke", 'D', ["Legacy"], "smoke", "판매자 이름이 NULL이면 예외가 나지 않고 \"(이름 없음)\"으로 처리되게 수정해줘",
            [], new HashSet<string>(), [], [], [], "");
        var idx = CorpusIndexBuilder.Build(SpikePaths.Corpus, smoke.Scope);
        foreach (var arm in new[] { "A", "B" })
        {
            var r = await Runner.RunOnceAsync(http, model, digest ?? "", smoke, idx, arm, 1, CancellationToken.None);
            var p = PlanParser.Parse(r.Output);
            Console.WriteLine($"{arm}: {r.Seconds:F1}s prompt={r.PromptTokens} eval={r.EvalCount} done={r.DoneReason} truncated={r.ThinkTruncated} tasks={p.Tasks.Count} invalid={p.InvalidLines.Count} extra={p.ExtraLines}");
            Console.WriteLine(r.Output.Trim());
        }
        return 0;
    }

    private static int CallersCommand()
    {
        if (Gate() is not null)
            return 2;
        foreach (var c in GoldLoader.LoadAll(SpikePaths.Cases).Where(c => c.Category == 'A'))
        {
            var text = CallerFinder.Plan(SpikePaths.Corpus, c.Scope, c.Tasks[0]);
            var path = Runner.RawPath(SpikePaths.Raw, "C", c.Id, 1);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, new RawRecord(c.Id, "C", 1, null, null, null, false, text, null, null, null, Runner.GoldCommit(SpikePaths.Spike)).ToJson());
            Console.WriteLine($"C {c.Id}: {PlanParser.Parse(text).Tasks.Count} tasks");
        }
        return 0;
    }

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
            case "run" when Option(args, "--arm") == ProductArm.Arm:
                return ProductArm.Command(args);
            case "run":
                return RunCommand(args).GetAwaiter().GetResult();
            case "smoke":
                return SmokeCommand().GetAwaiter().GetResult();
            case "callers":
                return CallersCommand();
            case "score":
                if (Gate() is not null)
                    return 2;
                Directory.CreateDirectory(SpikePaths.Results);
                var scores = Report.ScoreAll(SpikePaths.Raw, SpikePaths.Cases, SpikePaths.Corpus, Runner.GoldCommit(SpikePaths.Spike));
                File.WriteAllText(Path.Combine(SpikePaths.Results, "results.csv"), Report.Csv(scores));
                Console.WriteLine($"scored {scores.Count} runs -> results/results.csv");
                return 0;
            case "report":
                if (Gate() is not null)
                    return 2;
                Directory.CreateDirectory(SpikePaths.Results);
                var commit = Runner.GoldCommit(SpikePaths.Spike);
                var md = Report.Summary(Report.ScoreAll(SpikePaths.Raw, SpikePaths.Cases, SpikePaths.Corpus, commit), Report.LoadRaws(SpikePaths.Raw), commit);
                File.WriteAllText(Path.Combine(SpikePaths.Results, "summary.md"), md);
                Console.Write(md);
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
