using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Llm;

namespace S6Planning;

/// <summary>One stored response. Scoring reads only these files, so rules can change without calling the model again.</summary>
public sealed record RawRecord(
    string CaseId, string Arm, int Rep, string? ModelDigest, int? PromptTokens, string? Think, bool ThinkTruncated,
    string Output, string? DoneReason, int? EvalCount, double? Seconds, string? GoldCommit = null)
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public static RawRecord FromJson(string json) =>
        JsonSerializer.Deserialize<RawRecord>(json, Options) ?? throw new InvalidDataException("Empty raw record.");
}

/// <summary>
/// Refuses a run unless every gold case is approved and cases/APPROVED.json names the commit the gold files are at
/// (design §8). The gold must not be written or changed after the model's answers are seen.
/// </summary>
public static class ApprovalGate
{
    public static string? Check(string casesDir, string goldCommit, bool casesDirty = false)
    {
        var approvalPath = Path.Combine(casesDir, "APPROVED.json");
        if (!File.Exists(approvalPath))
            return "cases/APPROVED.json is missing: a human must approve the gold answers first.";

        var drafts = GoldLoader.LoadAll(casesDir).Where(c => c.Status != "approved").Select(c => c.Id).ToList();
        if (drafts.Count > 0)
            return $"gold is not approved (draft or other status): {string.Join(", ", drafts)}";
        if (casesDirty)
            return "cases/ has uncommitted or untracked changes: the gold that is scored must be the committed, approved gold.";

        using var doc = JsonDocument.Parse(File.ReadAllText(approvalPath));
        var approved = doc.RootElement.TryGetProperty("goldCommit", out var c) ? c.GetString() : null;
        return approved == goldCommit
            ? null
            : $"goldCommit in APPROVED.json ({approved}) does not match the gold files' last commit ({goldCommit}).";
    }
}

public static class Runner
{
    public const string FieldDigestPrefix = "a7eb95c53bcf";

    // Product defaults (PellyOptions.SamplingSet).
    public static readonly SamplingOptions ThinkSampling = new(0.6, 0.95, 20, 1.0, 0.0);
    public static readonly SamplingOptions CodeSampling = new(0.1, 0.9, 20, 1.05, 0.0);

    public static string? CheckDigest(string? digest) =>
        digest is not null && digest.StartsWith(FieldDigestPrefix, StringComparison.Ordinal)
            ? null
            : $"model digest {digest ?? "(none)"} is not the field model file ({FieldDigestPrefix}…).";

    public static string Stage2Prompt(string stage1Raw, string thinkText, QwenChatTemplate t) => stage1Raw + thinkText + t.ThinkClose;

    public static JsonObject GenerateBody(string model, string prompt, SamplingOptions s, int numPredict, IReadOnlyList<string> stop) => new()
    {
        ["model"] = model,
        ["prompt"] = prompt,
        ["raw"] = true,
        ["stream"] = false,
        ["keep_alive"] = "10m",
        ["options"] = new JsonObject
        {
            ["temperature"] = s.Temperature,
            ["top_p"] = s.TopP,
            ["top_k"] = s.TopK,
            ["repeat_penalty"] = s.RepeatPenalty,
            ["presence_penalty"] = s.PresencePenalty,
            ["num_predict"] = numPredict,
            ["num_ctx"] = 8192,
            ["stop"] = new JsonArray(stop.Select(x => (JsonNode)x!).ToArray()),
        },
    };

    public static async Task<string?> GetDigestAsync(HttpClient http, string model, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await http.GetStringAsync("api/tags", ct));
        foreach (var m in doc.RootElement.GetProperty("models").EnumerateArray())
            if (m.TryGetProperty("name", out var n) && n.GetString() == model)
                return m.TryGetProperty("digest", out var d) ? d.GetString() : null;
        return null;
    }

    public static async Task<RawRecord> RunOnceAsync(HttpClient http, string model, string digest, GoldCase c, CorpusIndex idx, string arm, int rep, CancellationToken ct,
        string? goldCommit = null)
    {
        var t = QwenChatTemplate.LoadDefault();
        var sw = Stopwatch.StartNew();
        string? think = null;
        var truncated = false;
        var evalTotal = 0;
        string prompt;
        if (arm == "B")
        {
            var stage1 = PlanPrompt.Raw(t, c.Request, idx, think: true);
            var r1 = await GenerateAsync(http, GenerateBody(model, stage1, ThinkSampling, 2048, [t.ThinkClose]), ct);
            think = r1.Text;
            truncated = r1.DoneReason == "length";
            evalTotal += r1.EvalCount ?? 0;
            prompt = Stage2Prompt(stage1, think, t);
        }
        else
        {
            prompt = PlanPrompt.Raw(t, c.Request, idx, think: false);
        }

        var r2 = await GenerateAsync(http, GenerateBody(model, prompt, CodeSampling, 1024, [t.EndOfTurn]), ct);
        evalTotal += r2.EvalCount ?? 0;
        return new RawRecord(c.Id, arm, rep, digest, r2.PromptTokens, think, truncated, r2.Text, r2.DoneReason, evalTotal, sw.Elapsed.TotalSeconds, goldCommit);
    }

    private sealed record Generated(string Text, string? DoneReason, int? EvalCount, int? PromptTokens);

    private static async Task<Generated> GenerateAsync(HttpClient http, JsonObject body, CancellationToken ct)
    {
        using var resp = await http.PostAsJsonAsync("api/generate", body, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var r = doc.RootElement;
        if (!r.TryGetProperty("response", out var text))
            throw new InvalidOperationException("Ollama response has no 'response' field: " + r.GetRawText()[..Math.Min(200, r.GetRawText().Length)]);
        return new Generated(text.GetString() ?? "",
            r.TryGetProperty("done_reason", out var d) ? d.GetString() : null,
            r.TryGetProperty("eval_count", out var e) ? e.GetInt32() : null,
            r.TryGetProperty("prompt_eval_count", out var p) ? p.GetInt32() : null);
    }

    /// <summary>Last commit touching any gold file, via git (the approval gate compares against it).</summary>
    public static string GoldCommit(string spikeDir)
    {
        var psi = new ProcessStartInfo("git", ["-C", spikeDir, "log", "-1", "--format=%H", "--", "cases/*/gold.*"])
            { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        var output = p.StandardOutput.ReadToEnd().Trim();
        p.WaitForExit();
        return p.ExitCode == 0 ? output : throw new InvalidOperationException("git log failed: " + p.StandardError.ReadToEnd());
    }

    /// <summary>True when git reports any change (including untracked files) under cases/.</summary>
    public static bool CasesDirty(string spikeDir)
    {
        var psi = new ProcessStartInfo("git", ["-C", spikeDir, "status", "--porcelain", "--", "cases"])
            { RedirectStandardOutput = true, RedirectStandardError = true };
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode != 0 || output.Trim().Length > 0;
    }

    public static string RawPath(string rawDir, string arm, string caseId, int rep) =>
        Path.Combine(rawDir, arm, caseId, rep + ".json");
}
