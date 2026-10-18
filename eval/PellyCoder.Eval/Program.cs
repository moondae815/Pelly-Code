using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PellyCoder.Core;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Processes;
using PellyCoder.Core.Sql;
using PellyCoder.Eval;

// Benchmark runner (design §10.6). Exit codes: 0 done, 1 verification invariant violated, 2 cancelled, 4 setup error.
Console.OutputEncoding = Encoding.UTF8;
var parsed = EvalArguments.Parse(args);
if (parsed.Help || parsed.Error is not null)
{
    if (parsed.Error is not null) Console.Error.WriteLine(parsed.Error);
    Console.WriteLine(EvalArguments.Usage);
    return parsed.Error is null ? 0 : 4;
}

var evalRoot = EvalPaths.FindRoot();
if (evalRoot is null) return Fail("eval/benchmark/tasks 폴더를 찾을 수 없습니다 — 저장소 안에서 실행하세요.");
var tasks = BenchmarkTask.LoadAll(EvalPaths.TasksDir(evalRoot)).Where(t => TaskFilter.Matches(t, parsed.Tasks, parsed.Kind)).ToList();
if (tasks.Count == 0) return Fail($"선택된 작업이 없습니다 (--tasks {parsed.Tasks ?? "*"}, --kind {parsed.Kind ?? "*"}).");

var fs = new FileSystem();
if (parsed.Config is not null && !File.Exists(parsed.Config)) return Fail($"설정 파일이 없습니다: {parsed.Config}");
var environment = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
    .ToDictionary(e => (string)e.Key, e => (string?)e.Value);
var loaded = PellyOptionsLoader.Load(fs, PellyPaths.UserConfigPath(), parsed.Config, environment, new Dictionary<string, string?>());
if (loaded.Options is not { } options) return Fail("설정 오류:\n  " + string.Join("\n  ", loaded.Errors));

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
using var http = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(options.Llm.ConnectTimeoutSeconds) })
{
    Timeout = Timeout.InfiniteTimeSpan, // OllamaBackend enforces its own timers (as the CLI's composition root does).
};
var backend = new OllamaBackend(http, options.Llm, TimeProvider.System);
var status = await backend.GetStatusAsync(cts.Token);
if (!status.Reachable || !status.ModelInstalled)
    return Fail($"LLM 서버를 쓸 수 없습니다 ({options.Llm.Endpoint}, 모델 {options.Llm.Model}): {status.Error ?? "모델이 설치되어 있지 않습니다"}");

ISqlExecutor? sql = null;
string? sqlSkip = null;
if (tasks.Any(t => t.Kind == "sql"))
{
    if (string.IsNullOrWhiteSpace(options.Sql.ConnectionString))
        sqlSkip = "SQL 연결이 없습니다 — 환경 변수 PELLY_SQL_CONNECTION(또는 sql.connectionString)을 지정하세요";
    else if (SqlTargetPolicy.Check(SqlConnectionText.Endpoint(options.Sql.ConnectionString), options.Sql) is { Allowed: false } check)
        sqlSkip = check.Message + " (--config의 pelly.json에서 허용 목록을 지정하세요)";
    else
        sql = new SqlServerExecutor(options.Sql.ConnectionString);
}

var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
var outDir = Path.GetFullPath(parsed.Out ?? Path.Combine(evalRoot, "results", stamp));
Directory.CreateDirectory(outDir);
Console.WriteLine($"작업 {tasks.Count}건 → {outDir}");

var runner = new BenchmarkRunner(new RunnerSetup(fs, new SystemProcessRunner(TimeProvider.System), backend, options, TimeProvider.System,
    evalRoot, sql, sqlSkip, Console.WriteLine));
var results = new List<TaskResult>();
var cancelled = false;
foreach (var task in tasks)
{
    try
    {
        results.Add(await runner.RunAsync(task, outDir, cts.Token));
    }
    catch (OperationCanceledException) when (cts.IsCancellationRequested)
    {
        cancelled = true;
        break;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[{task.Id}] 실행기 오류: {ex}");
        results.Add(TaskResult.Failed(task, $"실행기 오류: {ex.Message}", "RunnerError"));
    }
}

var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
File.WriteAllText(Path.Combine(outDir, "results.json"), JsonSerializer.Serialize(results, json));
var header = $"* 실행: {stamp} · pelly {PellyInfo.Version} · 모델 `{options.Llm.Model}` ({options.Llm.Endpoint}, Ollama {status.Version}) · "
    + $"thinkMode {options.Llm.ThinkMode} · {Environment.OSVersion.Platform}\n* 선택: --tasks {parsed.Tasks ?? "*"} --kind {parsed.Kind ?? "*"}"
    + (cancelled ? "\n* **중단됨** — 끝난 작업만 집계했습니다" : "");
var report = KpiReport.Render(results, header);
File.WriteAllText(Path.Combine(outDir, "report.md"), report);
Console.WriteLine();
Console.WriteLine(report);
Console.WriteLine($"결과: {Path.Combine(outDir, "report.md")}");

if (cancelled) return 2;
return results.Any(r => r.InvariantViolated) ? 1 : 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 4;
}
