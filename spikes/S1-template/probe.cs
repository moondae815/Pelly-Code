// S1: Ollama raw 모드에서 ChatML + <think> 프롬프트가 /api/chat 렌더러와 같은지 확인한다.
// 실행: dotnet run probe.cs
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var endpoint = Environment.GetEnvironmentVariable("PELLY_SPIKE_OLLAMA") ?? "http://localhost:11434";
var model = Environment.GetEnvironmentVariable("PELLY_SPIKE_MODEL") ?? "qwen3.6:35b-a3b-coding-nvfp4";
var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromMinutes(5) };

const string system = "You are a C# coding assistant.";
const string user = "두 정수를 더하는 C# 메서드를 써줘. 설명 없이 코드만.";
var nonce = Guid.NewGuid().ToString("N")[..8];

// 1) /api/chat (렌더러 사용), think=true
var chat = await Stream(http, "/api/chat", new
{
    model, stream = true, think = true,
    messages = new object[] { new { role = "system", content = $"{system} [{nonce}]" }, new { role = "user", content = user } },
    options = new { num_predict = 400, temperature = 0 },
});
Report("chat think=true", chat);

// 2) /api/generate raw: ChatML + "<think>\n" 프리필
var raw = $"<|im_start|>system\n{system} [{nonce}]<|im_end|>\n<|im_start|>user\n{user}<|im_end|>\n<|im_start|>assistant\n<think>\n";
var gen = await Stream(http, "/api/generate", new
{
    model, prompt = raw, raw = true, stream = true,
    options = new { num_predict = 400, temperature = 0 },
});
Report("generate raw ChatML+<think>", gen);

// 3) raw, 빈 think 프리필 (생각 없이 바로 답)
var rawNoThink = raw + "\n</think>\n\n";
var gen2 = await Stream(http, "/api/generate", new
{
    model, prompt = rawNoThink, raw = true, stream = true,
    options = new { num_predict = 200, temperature = 0 },
});
Report("generate raw empty-think", gen2);

static async Task<Result> Stream(HttpClient http, string path, object body)
{
    var req = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
    var started = DateTime.UtcNow;
    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
    resp.EnsureSuccessStatusCode();
    using var reader = new StreamReader(await resp.Content.ReadAsStreamAsync());
    var r = new Result();
    string? line;
    while ((line = await reader.ReadLineAsync()) is not null)
    {
        if (line.Length == 0) continue;
        var j = JsonNode.Parse(line)!.AsObject();
        foreach (var kv in j) r.Fields.Add(kv.Key);
        var msg = j["message"]?.AsObject();
        if (msg is not null) foreach (var kv in msg) r.Fields.Add("message." + kv.Key);
        var text = (string?)j["response"] ?? (string?)msg?["content"];
        var think = (string?)j["thinking"] ?? (string?)msg?["thinking"];
        if (!string.IsNullOrEmpty(text)) { r.Response.Append(text); r.Ttft ??= DateTime.UtcNow - started; }
        if (!string.IsNullOrEmpty(think)) { r.Thinking.Append(think); r.Ttft ??= DateTime.UtcNow - started; }
        if ((bool?)j["done"] == true)
        {
            r.PromptEval = (int?)j["prompt_eval_count"];
            r.Eval = (int?)j["eval_count"];
            r.DoneReason = (string?)j["done_reason"];
        }
    }
    return r;
}

static void Report(string name, Result r)
{
    Console.WriteLine($"===== {name}");
    Console.WriteLine($"fields: {string.Join(", ", r.Fields.Order())}");
    Console.WriteLine($"prompt_eval_count={r.PromptEval} eval_count={r.Eval} done_reason={r.DoneReason} ttft={r.Ttft?.TotalMilliseconds:F0}ms");
    Console.WriteLine($"--- thinking field ({r.Thinking.Length} chars):\n{Trunc(r.Thinking.ToString())}");
    Console.WriteLine($"--- response field ({r.Response.Length} chars):\n{Trunc(r.Response.ToString())}");
    var resp = r.Response.ToString();
    Console.WriteLine($"response contains '</think>' at index {resp.IndexOf("</think>", StringComparison.Ordinal)}; '<think>' at {resp.IndexOf("<think>", StringComparison.Ordinal)}");
}

static string Trunc(string s) => s.Length <= 700 ? s : s[..350] + "\n...\n" + s[^350..];

sealed class Result
{
    public HashSet<string> Fields { get; } = new();
    public StringBuilder Response { get; } = new();
    public StringBuilder Thinking { get; } = new();
    public int? PromptEval, Eval;
    public string? DoneReason;
    public TimeSpan? Ttft;
}
