// S2: raw 2단계 생성(THINK → CODE 프리필)에서 접두사 KV 캐시가 재사용되는지 측정한다.
// 실행: dotnet run prefill.cs [runs=5]
using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;

var endpoint = Environment.GetEnvironmentVariable("PELLY_SPIKE_OLLAMA") ?? "http://localhost:11434";
var model = Environment.GetEnvironmentVariable("PELLY_SPIKE_MODEL") ?? "qwen3.6:35b-a3b-coding-nvfp4";
var runs = args.Length > 0 ? int.Parse(args[0]) : 5;
var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromMinutes(10) };

var repoRoot = FindRepoRoot();
var source = File.ReadAllText(Path.Combine(repoRoot, "eval/corpus/Modern/Shop.Api/Services/OrderService.cs"));
const string system = "You are a C# editor. Output only SEARCH/REPLACE blocks. SEARCH must copy the original lines exactly.";
string User(string src) => $"### TARGET\nOrderService.CancelAsync(int, string)\n### CONTEXT\n{src}\n### REQUEST\n배송 중(Shipped)인 주문을 취소하려고 하면 InvalidOperationException을 던지도록 수정해줘";

// 워밍업 (모델 로드)
await Generate(http, model, "<|im_start|>user\nhi<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n", 4, null, null);

Console.WriteLine("variant,run,stage,ttft_ms,prompt_eval_count,prompt_eval_cached_count,prompt_eval_ms,eval_count,chunks,done_reason,think_tokens_kept");
foreach (var variant in new[] { "A-server-num_predict", "B-client-cancel" })
{
    for (var run = 1; run <= runs; run++)
    {
        var nonce = Guid.NewGuid().ToString("N");
        // 기본 프롬프트를 늘려 약 3,500토큰으로 맞춘다 (소스 2회 + nonce)
        var basePrompt = $"<|im_start|>system\n// run {nonce}\n{system}<|im_end|>\n<|im_start|>user\n{User(source + "\n" + source)}<|im_end|>\n<|im_start|>assistant\n";

        Gen s1 = variant.StartsWith("A")
            ? await Generate(http, model, basePrompt + "<think>\n", 512, new[] { "</think>" }, null)
            : await Generate(http, model, basePrompt + "<think>\n", 4096, new[] { "</think>" }, cancelAfterChunks: 512);

        var think = s1.Text;
        var truncated = s1.DoneReason != "stop";
        if (truncated)
        {
            var cut = think.LastIndexOf('\n');
            if (cut > 0) think = think[..cut];
        }
        think = think.TrimEnd();

        var prefill = basePrompt + "<think>\n" + think + "\n</think>\n\n<<<<<<< SEARCH\n";
        var s2 = await Generate(http, model, prefill, 256, new[] { "<|im_end|>" }, null);

        Console.WriteLine($"{variant},{run},think,{s1.TtftMs:F0},{s1.PromptEval},{s1.PromptCached},{s1.PromptEvalMs:F0},{s1.Eval},{s1.Chunks},{s1.DoneReason},");
        Console.WriteLine($"{variant},{run},code,{s2.TtftMs:F0},{s2.PromptEval},{s2.PromptCached},{s2.PromptEvalMs:F0},{s2.Eval},{s2.Chunks},{s2.DoneReason},{think.Length}c");
        if (run == 1) File.WriteAllText($"sample-{variant}.txt", "=== THINK\n" + think + "\n=== CODE\n<<<<<<< SEARCH\n" + s2.Text);
    }
}

static async Task<Gen> Generate(HttpClient http, string model, string prompt, int numPredict, string[]? stop, int? cancelAfterChunks)
{
    var body = new JsonObject
    {
        ["model"] = model, ["prompt"] = prompt, ["raw"] = true, ["stream"] = true, ["keep_alive"] = "30m",
        ["options"] = new JsonObject
        {
            ["num_ctx"] = 8192, ["num_predict"] = numPredict, ["temperature"] = 0.6, ["top_p"] = 0.95, ["top_k"] = 20,
            ["stop"] = stop is null ? null : new JsonArray(stop.Select(s => (JsonNode)s!).ToArray()),
        },
    };
    using var cts = new CancellationTokenSource();
    var sw = Stopwatch.StartNew();
    var g = new Gen();
    try
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/generate") { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        resp.EnsureSuccessStatusCode();
        using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync(cts.Token));
        var sb = new StringBuilder();
        string? line;
        while ((line = await reader.ReadLineAsync(cts.Token)) is not null)
        {
            if (line.Length == 0) continue;
            var j = JsonNode.Parse(line)!;
            var t = (string?)j["response"];
            if (!string.IsNullOrEmpty(t))
            {
                g.TtftMs ??= sw.Elapsed.TotalMilliseconds;
                sb.Append(t);
                g.Chunks++;
                if (cancelAfterChunks is int max && g.Chunks >= max)
                {
                    g.Text = sb.ToString();
                    g.DoneReason = "client-cancel";
                    cts.Cancel();
                    break;
                }
            }
            if ((bool?)j["done"] == true)
            {
                g.PromptEval = (int?)j["prompt_eval_count"];
                g.PromptCached = (int?)j["prompt_eval_cached_count"];
                g.PromptEvalMs = ((long?)j["prompt_eval_duration"] ?? 0) / 1e6;
                g.Eval = (int?)j["eval_count"];
                g.DoneReason = (string?)j["done_reason"];
            }
        }
        g.Text ??= sb.ToString();
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested) { }
    return g;
}

static string FindRepoRoot()
{
    for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "eval", "corpus"))) return d.FullName;
    throw new DirectoryNotFoundException("repo root");
}

sealed class Gen
{
    public string? Text, DoneReason;
    public double? TtftMs;
    public int? PromptEval, PromptCached, Eval;
    public double PromptEvalMs;
    public int Chunks;
}
