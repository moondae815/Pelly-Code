// S4: Microsoft.ML.Tokenizers로 Qwen 토크나이저를 로드해 서버 prompt_eval_count와 비교한다.
// 실행: dotnet run tokenizer.cs
#:package Microsoft.ML.Tokenizers@2.0.1
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.ML.Tokenizers;

var endpoint = Environment.GetEnvironmentVariable("PELLY_SPIKE_OLLAMA") ?? "http://localhost:11434";
var model = Environment.GetEnvironmentVariable("PELLY_SPIKE_MODEL") ?? "qwen3.6:35b-a3b-coding-nvfp4";
var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromMinutes(5) };

// tokenizer.json에서 특수 토큰과 전처리 정규식을 읽는다.
var tj = JsonNode.Parse(File.ReadAllText("tokenizer.json"))!;
var special = tj["added_tokens"]!.AsArray().ToDictionary(a => (string)a!["content"]!, a => (int)a!["id"]!);
var pattern = (string)tj["pre_tokenizer"]!["pretokenizers"]![0]!["pattern"]!["Regex"]!;
Console.WriteLine($"special tokens: {special.Count}, regex: {pattern}");

var sw = Stopwatch.StartNew();
var options = new BpeOptions("vocab.json", "merges.txt")
{
    ByteLevel = true,
    SpecialTokens = special,
    PreTokenizer = new RegexPreTokenizer(new Regex(pattern, RegexOptions.Compiled), special),
    Normalizer = new NfcNormalizer(),
};
var tokenizer = BpeTokenizer.Create(options);
Console.WriteLine($"load: {sw.ElapsedMilliseconds}ms");

var samples = JsonSerializer.Deserialize<List<Sample>>(File.ReadAllText("samples.json"), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;

// 워밍업
await ServerCount("hi");

var rows = new List<string> { "index,category,chars,local,server,diff,diff_pct" };
var perCat = new Dictionary<string, List<double>>();
var countMs = new List<double>();
for (var i = 0; i < samples.Count; i++)
{
    var s = samples[i];
    var t0 = Stopwatch.GetTimestamp();
    var local = tokenizer.CountTokens(s.Text);
    countMs.Add(Stopwatch.GetElapsedTime(t0).TotalMilliseconds);
    var server = await ServerCount(s.Text);
    var diff = local - server;
    var pct = server == 0 ? 0 : 100.0 * diff / server;
    rows.Add($"{i},{s.Category},{s.Text.Length},{local},{server},{diff},{pct:F2}");
    (perCat.TryGetValue(s.Category, out var l) ? l : perCat[s.Category] = new()).Add(Math.Abs(pct));
}

File.WriteAllLines("results.csv", rows);
Console.WriteLine(string.Join("\n", rows));
Console.WriteLine();
foreach (var (cat, list) in perCat)
    Console.WriteLine($"{cat}: n={list.Count} mean|%|={list.Average():F2} max|%|={list.Max():F2}");
var all = perCat.Values.SelectMany(x => x).ToList();
Console.WriteLine($"ALL: mean|%|={all.Average():F2} max|%|={all.Max():F2}; count time median={countMs.Order().ElementAt(countMs.Count / 2):F2}ms max={countMs.Max():F2}ms");

async Task<int> ServerCount(string prompt)
{
    var body = new JsonObject
    {
        ["model"] = model, ["prompt"] = prompt, ["raw"] = true, ["stream"] = false, ["keep_alive"] = "30m",
        ["options"] = new JsonObject { ["num_predict"] = 1, ["num_ctx"] = 8192 },
    };
    var resp = await http.PostAsync("/api/generate", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
    resp.EnsureSuccessStatusCode();
    return (int)JsonNode.Parse(await resp.Content.ReadAsStringAsync())!["prompt_eval_count"]!;
}

sealed record Sample(string Category, string Text);

sealed class NfcNormalizer : Normalizer
{
    public override string Normalize(string original) => original.Normalize(NormalizationForm.FormC);
    public override string Normalize(ReadOnlySpan<char> original) => original.ToString().Normalize(NormalizationForm.FormC);
}
