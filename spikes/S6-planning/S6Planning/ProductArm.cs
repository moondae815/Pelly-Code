using System.Diagnostics;
using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using PellyCoder.Core.Config;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Specs.Drafting;
using PellyCoder.Core.Workspaces;

namespace S6Planning;

/// <summary>
/// Arm P (spec draft preliminary measurement, design 2026-10-27 §8): each approved case through the product path
/// (<see cref="DraftModel"/> + <see cref="SpecDrafter"/> with the semantic impact finder, SQL impact off). The record's
/// Output is the final task list (model lines kept by the product parser + lines the impact search added), stored in the
/// raw layout so <c>score</c> and <c>report</c> score P like A and B. Extra fields ride along in the same JSON.
/// </summary>
public static class ProductArm
{
    public const string Arm = "P";
    private const string Marker = ".arm-p-ready";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>S6's "Sql" scope indexed Sql/objects only; the product takes the folder itself.</summary>
    public static IReadOnlyList<string> ProductScopes(IReadOnlyList<string> s6Scope) =>
        s6Scope.Select(s => s == "Sql" ? "Sql/objects" : s).ToList();

    /// <summary>The task lines under <c>## 작업</c> (the check comment and anything else there is left out).</summary>
    public static string FinalTaskLines(string document)
    {
        var lines = document.Replace("\r\n", "\n").Split('\n');
        var start = Array.IndexOf(lines, "## 작업");
        if (start < 0)
            return "";
        var sb = new StringBuilder();
        foreach (var l in lines[(start + 1)..])
        {
            if (l.StartsWith("## ", StringComparison.Ordinal))
                break;
            if (l.StartsWith("- [", StringComparison.Ordinal))
                sb.Append(l).Append('\n');
        }
        return sb.ToString();
    }

    public static string ToJson(RawRecord raw, JsonObject extra)
    {
        var node = JsonNode.Parse(raw.ToJson())!.AsObject();
        foreach (var (k, v) in extra.ToList())
        {
            extra.Remove(k);
            node[k] = v;
        }
        return node.ToJsonString(Web);
    }

    private static string? Option(string[] args, string name)
    {
        var i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary><c>run --arm P --repeat N [--case id] [--workspace dir]</c> or <c>run --arm P --stats</c>.</summary>
    public static int Command(string[] args)
    {
        var gold = Runner.GoldCommit(SpikePaths.Spike);
        if (ApprovalGate.Check(SpikePaths.Cases, gold, Runner.CasesDirty(SpikePaths.Spike)) is { } reason)
        {
            Console.Error.WriteLine("refused: " + reason);
            return 2;
        }
        if (args.Contains("--stats"))
            return Stats(gold);
        if (!int.TryParse(Option(args, "--repeat"), out var repeat) || repeat < 1)
        {
            Console.Error.WriteLine("usage: run --arm P --repeat N [--case id] [--workspace dir] | run --arm P --stats");
            return 2;
        }
        var workspace = Option(args, "--workspace") ?? Path.Combine(Path.GetTempPath(), "pelly-s6-arm-p", "corpus");
        return RunAsync(repeat, Option(args, "--case"), workspace, gold).GetAwaiter().GetResult();
    }

    private static async Task<int> RunAsync(int repeat, string? only, string workspace, string gold)
    {
        var endpoint = Environment.GetEnvironmentVariable("PELLY_SPIKE_OLLAMA") ?? "http://localhost:11434";
        var modelName = Environment.GetEnvironmentVariable("PELLY_SPIKE_MODEL") ?? "qwen3.6:35b";
        string? digest;
        using (var tags = new HttpClient { BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) })
            digest = await Runner.GetDigestAsync(tags, modelName, CancellationToken.None);
        if (Runner.CheckDigest(digest) is { } bad)
        {
            Console.Error.WriteLine("refused: " + bad);
            return 2;
        }

        PrepareWorkspace(SpikePaths.Corpus, workspace);
        var fs = new FileSystem();
        var ws = WorkspaceLocator.Resolve(fs, Path.Combine(workspace, "pelly-arm-p"));
        if (!string.Equals(ws.Root, workspace, StringComparison.Ordinal))
            throw new InvalidOperationException($"workspace root resolved to {ws.Root}, not {workspace}");

        using var http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        var llm = new LlmOptions { Endpoint = endpoint, Model = modelName };
        var model = new DraftModel(new OllamaBackend(http, llm, TimeProvider.System), QwenChatTemplate.LoadDefault(), TimeProvider.System);
        var drafter = new SpecDrafter(fs, model, ImpactFinders.Default(fs), sql: null, new SqlOptions().CompatLevel);

        foreach (var c in GoldLoader.LoadAll(SpikePaths.Cases).Where(c => only is null || c.Id == only))
        {
            var scopes = ProductScopes(c.Scope).Select(s => Path.Combine(workspace, s)).ToList();
            for (var rep = 1; rep <= repeat; rep++)
            {
                var path = Runner.RawPath(SpikePaths.Raw, Arm, c.Id, rep);
                if (File.Exists(path))
                    continue;
                var sw = Stopwatch.StartNew();
                var (outcome, failure) = await drafter.DraftAsync(ws, new DraftRequest(c.Id, c.Request, scopes), CancellationToken.None);
                sw.Stop();
                if (failure is { ExitCode: not 1 })
                    throw new InvalidOperationException($"{c.Id} #{rep}: exit {failure.ExitCode}: {failure.Message}");

                var call = outcome?.Call;
                var output = outcome is null ? "" : FinalTaskLines(outcome.Document);
                var raw = new RawRecord(c.Id, Arm, rep, digest, call?.InputTokens, call?.Think, call?.ThinkTruncated ?? false,
                    output, null, null, sw.Elapsed.TotalSeconds, gold);
                var impact = outcome?.Check.Impact;
                var extra = new JsonObject
                {
                    ["answer"] = call?.Answer,
                    ["modelSeconds"] = call?.Seconds,
                    ["impactAdded"] = outcome?.Added.Count ?? 0,
                    ["addedTasks"] = new JsonArray((outcome?.Added ?? []).Select(a => (JsonNode)$"{a.TaskId} {a.Path}#{a.Member} <- {a.FromTaskId}").ToArray()),
                    ["dropped"] = outcome?.DroppedLines.Count ?? 0,
                    ["droppedLines"] = new JsonArray((outcome?.DroppedLines ?? []).Select(d => (JsonNode)d).ToArray()),
                    ["impactSkippedForErrors"] = outcome?.ImpactSkippedForErrors ?? false,
                    ["impactMode"] = impact?.Mode.ToString(),
                    ["impactFallback"] = impact?.Fallback?.Kind.ToString(),
                    ["checkErrors"] = outcome?.Check.Issues.Count(i => i.Severity == SpecIssueSeverity.Error) ?? 0,
                    ["failure"] = failure?.Message,
                    ["workspace"] = workspace,
                };
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, ToJson(raw, extra));
                Console.WriteLine($"P {c.Id} #{rep} {sw.Elapsed.TotalSeconds:F1}s tasks={PlanParser.Parse(output).Tasks.Count} added={outcome?.Added.Count ?? 0} " +
                    $"dropped={outcome?.DroppedLines.Count ?? 0} impact={impact?.Mode.ToString() ?? "-"}{(impact?.Fallback is { } f ? "/" + f.Kind : "")} " +
                    $"skipped={outcome?.ImpactSkippedForErrors ?? false} truncated={call?.ThinkTruncated}{(failure is null ? "" : " failure=exit1")}");
            }
        }
        return 0;
    }

    /// <summary>
    /// A copy of eval/corpus with an empty .git folder as the workspace marker (the corpus root has none, so the product
    /// would otherwise take the repository root and every path would gain an "eval/corpus/" prefix), Modern restored for
    /// the semantic load. Legacy (non-SDK, .NET Framework) is copied as is; its semantic load needs Windows.
    /// </summary>
    private static void PrepareWorkspace(string corpus, string dest)
    {
        if (File.Exists(Path.Combine(dest, Marker)))
            return;
        if (Directory.Exists(dest))
            Directory.Delete(dest, recursive: true);
        foreach (var file in Directory.GetFiles(corpus, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(corpus, file).Replace('\\', '/');
            if (rel.Split('/').Any(p => p is "bin" or "obj" or ".pelly" or "TestResults"))
                continue;
            var to = Path.Combine(dest, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        Directory.CreateDirectory(Path.Combine(dest, ".git"));

        var psi = new ProcessStartInfo("dotnet", ["restore", Path.Combine(dest, "Modern", "Modern.sln"), "--nologo"]) { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("dotnet did not start");
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"dotnet restore Modern.sln failed ({p.ExitCode}):\n{stdout}\n{stderr}");
        File.WriteAllText(Path.Combine(dest, Marker), "");
        Console.WriteLine($"workspace copy: {dest} (Modern restored)");
    }

    /// <summary>
    /// Per-run facts the S6 report does not show: impact-added and dropped lines, lines the product parser kept that the S6
    /// parser rejects, and the score of the model answer alone (before product parsing and impact lines).
    /// </summary>
    private static int Stats(string gold)
    {
        var golds = GoldLoader.LoadAll(SpikePaths.Cases).ToDictionary(g => g.Id);
        var dir = Path.Combine(SpikePaths.Raw, Arm);
        var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.json", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToList() : [];
        var indexes = new Dictionary<string, CorpusIndex>();
        var rows = new List<(GoldCase Gold, RawRecord Raw, JsonNode Node, RunScore P, RunScore Answer)>();
        foreach (var f in files)
        {
            var text = File.ReadAllText(f);
            var raw = RawRecord.FromJson(text);
            var node = JsonNode.Parse(text)!;
            var g = golds[raw.CaseId];
            if (!indexes.TryGetValue(g.Id, out var idx))
                indexes[g.Id] = idx = CorpusIndexBuilder.Build(SpikePaths.Corpus, g.Scope);
            rows.Add((g, raw, node, Scorer.Score(g, idx, Arm, raw.Rep, raw.Output),
                Scorer.Score(g, idx, "answer", raw.Rep, (string?)node["answer"] ?? "")));
        }

        static string F(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
        var sb = new StringBuilder($"arm P stats · goldCommit `{gold}` · runs {rows.Count}\n\n");
        sb.Append("| 사례 | 범주 | P 쓸 만함 | 답만 쓸 만함 | P 재현율 평균 | 답만 재현율 평균 | 영향 추가 줄(합) | 추가가 있던 반복 | 버린 줄(합) | S6 파서가 거부한 P 줄(합) | 검사 오류 반복 | 영향 건너뜀 | 영향 모드 | 0줄 실패 | think 잘림 |\n");
        sb.Append("| :--- | :---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | :--- | ---: | ---: |\n");
        foreach (var g in rows.GroupBy(r => r.Gold.Id).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var x = g.ToList();
            var modes = x.Select(r => ((string?)r.Node["impactMode"] ?? "-") + ((string?)r.Node["impactFallback"] is { } fb ? "/" + fb : ""))
                .GroupBy(m => m).Select(m => $"{m.Key}×{m.Count()}");
            sb.Append($"| {g.Key} | {x[0].Gold.Category} | {x.Count(r => r.P.Usable)}/{x.Count} | {x.Count(r => r.Answer.Usable)}/{x.Count} | {F(x.Average(r => r.P.Recall))} | {F(x.Average(r => r.Answer.Recall))} | ");
            sb.Append($"{x.Sum(r => (int)r.Node["impactAdded"]!)} | {x.Count(r => (int)r.Node["impactAdded"]! > 0)} | {x.Sum(r => (int)r.Node["dropped"]!)} | {x.Sum(r => PlanParser.Parse(r.Raw.Output).InvalidLines.Count)} | ");
            sb.Append($"{x.Count(r => (int)r.Node["checkErrors"]! > 0)} | {x.Count(r => (bool)r.Node["impactSkippedForErrors"]!)} | {string.Join(", ", modes)} | {x.Count(r => r.Node["failure"] is not null)} | {x.Count(r => r.Raw.ThinkTruncated)} |\n");
        }
        sb.Append("\n| 범주 | P 쓸 만함 | 답만 쓸 만함 | P 재현율 평균 | 답만 재현율 평균 |\n| :---: | ---: | ---: | ---: | ---: |\n");
        foreach (var cat in "ABCD")
        {
            var x = rows.Where(r => r.Gold.Category == cat).ToList();
            if (x.Count > 0)
                sb.Append($"| {cat} | {x.Count(r => r.P.Usable)}/{x.Count} | {x.Count(r => r.Answer.Usable)}/{x.Count} | {F(x.Average(r => r.P.Recall))} | {F(x.Average(r => r.Answer.Recall))} |\n");
        }
        if (rows.Count > 0)
            sb.Append($"| 전체 | {rows.Count(r => r.P.Usable)}/{rows.Count} | {rows.Count(r => r.Answer.Usable)}/{rows.Count} | {F(rows.Average(r => r.P.Recall))} | {F(rows.Average(r => r.Answer.Recall))} |\n");

        sb.Append("\n## 버린 줄 원문\n\n");
        foreach (var r in rows.Where(r => (int)r.Node["dropped"]! > 0))
            foreach (var d in r.Node["droppedLines"]!.AsArray())
                sb.Append($"- {r.Gold.Id} #{r.Raw.Rep}: `{(string?)d}`\n");
        sb.Append("\n## S6 파서가 거부한 P 줄\n\n");
        foreach (var r in rows)
            foreach (var l in PlanParser.Parse(r.Raw.Output).InvalidLines)
                sb.Append($"- {r.Gold.Id} #{r.Raw.Rep}: `{l}`\n");
        sb.Append("\n## 영향 탐색이 더한 줄\n\n");
        foreach (var r in rows.Where(r => (int)r.Node["impactAdded"]! > 0))
            sb.Append($"- {r.Gold.Id} #{r.Raw.Rep}: {string.Join("; ", r.Node["addedTasks"]!.AsArray().Select(a => (string?)a))}\n");
        sb.Append("\n## 0줄 실패\n\n");
        foreach (var r in rows.Where(r => r.Node["failure"] is not null))
            sb.Append($"- {r.Gold.Id} #{r.Raw.Rep}: {((string?)r.Node["failure"])!.Replace('\n', ' ')}\n");

        Directory.CreateDirectory(SpikePaths.Results);
        File.WriteAllText(Path.Combine(SpikePaths.Results, "premeasure-stats.md"), sb.ToString());
        Console.Write(sb.ToString());
        return 0;
    }
}
