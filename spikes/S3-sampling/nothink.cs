// S3 보조: think 512 후 CODE vs 빈 think CODE — 요청 반영 여부·소요 시간 비교. 실행: dotnet run nothink.cs
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
var endpoint = Environment.GetEnvironmentVariable("PELLY_SPIKE_OLLAMA") ?? "http://localhost:11434";
var model = Environment.GetEnvironmentVariable("PELLY_SPIKE_MODEL") ?? "qwen3.6:35b-a3b-coding-nvfp4";
var http = new HttpClient { BaseAddress = new Uri(endpoint), Timeout = TimeSpan.FromMinutes(10) };
var root = FindRepoRoot();

var tasks = new (string Id, string File, string Signature, string Request)[]
{
    ("cs-legacy-001", "Legacy/Shop.Web/Services/OrderService.cs", "public void Cancel(int orderId", "배송 중(Shipped)인 주문은 취소할 수 없도록 InvalidOperationException을 던지게 수정해줘"),
    ("cs-legacy-002", "Legacy/Shop.Batch/SettlementBatch.cs", "public SettlementResult Execute(", "판매자 이름이 NULL이면 예외가 나지 않고 \"(이름 없음)\"으로 처리되게 수정해줘"),
    ("cs-legacy-003", "Legacy/Shop.Data/OrderDao.cs", "public IList<OrderDto> SearchOrders(", "키워드 검색 때 고객 이름뿐 아니라 주문 상품명(OrderItems.ProductName)도 검색되게 해줘"),
    ("cs-modern-001", "Modern/Shop.Api/Services/OrderService.cs", "public async Task CancelAsync(", "배송 중(Shipped)인 주문을 취소하려고 하면 InvalidOperationException을 던지도록 수정해줘"),
    ("cs-modern-002", "Modern/Shop.Api/Services/OrderService.cs", "public async Task<decimal> CalculateRefundAsync(", "환불액의 10원 미만을 버리지 말고 10원 단위로 반올림하도록 바꿔줘"),
    ("x-001", "Modern/Shop.Api/Services/OrderService.cs", "public async Task<IReadOnlyList<Order>> SearchAsync(", "검색 기간이 90일을 넘으면 ArgumentException을 던지도록 해줘"),
    ("x-002", "Modern/Shop.Jobs/DailySettlementJob.cs", "public bool Run(", "정산 대상 주문 수를 로그에 남겨줘"),
    ("x-003", "Legacy/Shop.Data/OrderDao.cs", "public int UpdateStatus(", "변경된 행이 없으면 InvalidOperationException을 던지도록 해줘"),
    ("x-004", "Legacy/Shop.Web/Controllers/OrderController.cs", "public ActionResult Cancel(int id", "사유가 200자를 넘으면 TempData[\"Error\"]에 메시지를 넣고 Detail로 돌려보내줘"),
    ("x-005", "Legacy/Shop.Batch/SettlementBatch.cs", "public SettlementResult Execute(", "SqlException이 나면 경고 메시지에 오류 번호(ex.Number)도 포함해줘"),
};

var thinkConfigs = new (string Name, double T, double P, int K, double Presence)[]
{
    ("A-card-coding", 0.6, 0.95, 20, 0.0),
    ("B-card-general", 1.0, 0.95, 20, 1.5),
    ("C-low", 0.1, 0.9, 20, 0.0),
};
var codeConfigs = new (string Name, double T, double P, double Repeat)[]
{
    ("X-T0.1-repeat1.05", 0.1, 0.9, 1.05),
    ("Y-T0.2-repeat1.0", 0.2, 0.95, 1.0),
};

const string system = """
You edit exactly one C# member. Output only SEARCH/REPLACE blocks, no explanation.
Format:
<<<<<<< SEARCH
(original lines copied exactly, including indentation)
=======
(replacement lines)
>>>>>>> REPLACE
SEARCH must copy lines from the [EDITABLE: target] section verbatim.
""";

// 워밍업
await Gen("<|im_start|>user\nhi<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n", 4, 0.6, 0.95, 20, 0, 1.0, null);
var checks = new Dictionary<string, string[]>
{
    ["cs-legacy-001"] = ["OrderStatus.Shipped", "InvalidOperationException"],
    ["cs-legacy-002"] = ["IsDBNull(1)", "(이름 없음)"],
    ["cs-legacy-003"] = ["ProductName"],
    ["cs-modern-001"] = ["OrderStatus.Shipped", "InvalidOperationException"],
    ["cs-modern-002"] = ["Math.Round"],
    ["x-001"] = ["90", "ArgumentException"],
    ["x-002"] = ["rows.Count"],
    ["x-003"] = ["InvalidOperationException"],
    ["x-004"] = ["200", "TempData[\"Error\"]"],
    ["x-005"] = ["ex.Number"],
};
var rows = new List<string> { "task,mode,parsed,exact_match,request_applied,think_tokens,code_tokens,think_sec,code_sec,total_sec" };
foreach (var t in tasks)
{
    var member = ExtractMember(ReadSource(Path.Combine(root, "eval", "corpus", t.File)), t.Signature);
    var user = $"### TARGET\n{t.Signature}...\n### CONTEXT\n[EDITABLE: target]\n{member}\n### REQUEST\n{t.Request}";
    var basePrompt = $"<|im_start|>system\n{system}<|im_end|>\n<|im_start|>user\n{user}<|im_end|>\n<|im_start|>assistant\n";
    foreach (var mode in new[] { "think512", "nothink" })
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string think = "";
        int? thinkTokens = 0;
        if (mode == "think512")
        {
            var r1 = await Gen(basePrompt + "<think>\n", 512, 0.6, 0.95, 20, 0, 1.0, ["</think>"]);
            think = r1.Text;
            if (r1.DoneReason == "length" && think.LastIndexOf('\n') > 0) think = think[..think.LastIndexOf('\n')];
            think = think.TrimEnd();
            thinkTokens = r1.Eval;
        }
        var thinkSec = sw.Elapsed.TotalSeconds;
        var prefill = basePrompt + "<think>\n" + think + "\n</think>\n\n<<<<<<< SEARCH\n";
        var r2 = await Gen(prefill, 2048, 0.1, 0.9, 20, 0, 1.05, ["<|im_end|>"]);
        var total = sw.Elapsed.TotalSeconds;
        var output = "<<<<<<< SEARCH\n" + r2.Text;
        var blocks = ParseBlocks(output);
        var exact = blocks is not null && blocks.All(b => ContainsLines(member, b.Search, trimEnd: false));
        var replaced = blocks is null ? "" : string.Join("\n", blocks.Select(b => b.Replace));
        var applied = checks[t.Id].All(c => replaced.Contains(c));
        rows.Add($"{t.Id},{mode},{blocks is not null},{exact},{applied},{thinkTokens},{r2.Eval},{thinkSec:F1},{total - thinkSec:F1},{total:F1}");
        File.WriteAllText($"cmp-{t.Id}-{mode}.txt", output);
        Console.Error.WriteLine($"done {t.Id} {mode}");
    }
}
File.WriteAllLines("nothink.csv", rows);
Console.WriteLine(string.Join("\n", rows));

async Task<(string Text, int? Eval, string? DoneReason)> Gen(string prompt, int numPredict, double t, double p, int k, double presence, double repeat, string[]? stop)
{
    var body = new JsonObject
    {
        ["model"] = model, ["prompt"] = prompt, ["raw"] = true, ["stream"] = false, ["keep_alive"] = "30m",
        ["options"] = new JsonObject
        {
            ["num_ctx"] = 8192, ["num_predict"] = numPredict, ["temperature"] = t, ["top_p"] = p, ["top_k"] = k,
            ["presence_penalty"] = presence, ["repeat_penalty"] = repeat,
            ["stop"] = stop is null ? null : new JsonArray(stop.Select(s => (JsonNode)s!).ToArray()),
        },
    };
    var resp = await http.PostAsync("/api/generate", new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"));
    resp.EnsureSuccessStatusCode();
    var j = JsonNode.Parse(await resp.Content.ReadAsStringAsync())!;
    return ((string?)j["response"] ?? "", (int?)j["eval_count"], (string?)j["done_reason"]);
}

static bool HasRepetition(string text) => text.Split('\n')
    .Select(l => l.Trim()).Where(l => l.Length > 10)
    .GroupBy(l => l).Any(g => g.Count() >= 3);

static List<(string Search, string Replace)>? ParseBlocks(string output)
{
    var lines = output.Replace("\r\n", "\n").Split('\n');
    var blocks = new List<(string, string)>();
    int state = 0; var s = new List<string>(); var r = new List<string>();
    foreach (var raw in lines)
    {
        var l = raw.TrimEnd();
        if (l == "<<<<<<< SEARCH") { if (state != 0) return null; state = 1; s.Clear(); r.Clear(); continue; }
        if (l == "=======" && state == 1) { state = 2; continue; }
        if (l == ">>>>>>> REPLACE" && state == 2) { blocks.Add((string.Join("\n", s), string.Join("\n", r))); state = 0; continue; }
        if (state == 1) s.Add(raw); else if (state == 2) r.Add(raw);
    }
    return state == 0 && blocks.Count > 0 && blocks.All(b => b.Item1.Length > 0) ? blocks : null;
}

static bool ContainsLines(string member, string search, bool trimEnd)
{
    string N(string x) => string.Join("\n", x.Replace("\r\n", "\n").Split('\n').Select(l => trimEnd ? l.TrimEnd() : l));
    return N(member).Contains(N(search), StringComparison.Ordinal);
}

static string ExtractMember(string text, string signature)
{
    var lines = text.Replace("\r\n", "\n").Split('\n');
    var start = Array.FindIndex(lines, l => l.Contains(signature));
    var indent = lines[start].Length - lines[start].TrimStart().Length;
    var closing = new string(' ', indent) + "}";
    // 앞쪽 주석·특성 포함
    var first = start;
    while (first > 0 && (lines[first - 1].TrimStart().StartsWith("///") || lines[first - 1].TrimStart().StartsWith("[")))
        first--;
    for (var i = start + 1; i < lines.Length; i++)
        if (lines[i] == closing) return string.Join("\n", lines[first..(i + 1)]);
    throw new InvalidOperationException(signature);
}

static string ReadSource(string path)
{
    var bytes = File.ReadAllBytes(path);
    try { return new UTF8Encoding(false, true).GetString(bytes); }
    catch (DecoderFallbackException) { return Encoding.GetEncoding(949).GetString(bytes); }
}

static string FindRepoRoot()
{
    for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
        if (Directory.Exists(Path.Combine(d.FullName, "eval", "corpus"))) return d.FullName;
    throw new DirectoryNotFoundException("repo root");
}
