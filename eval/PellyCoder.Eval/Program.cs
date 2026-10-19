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

// Benchmark runner (design §10.6). Exit codes: 0 done, 1 verification invariant violated, 2 cancelled, 4 setup error
// (unreadable task files and a non-empty --out included).
// `compare <baseline> <candidate>` judges two --repeat summaries by the R4 rule (Acceptance.cs).
Console.OutputEncoding = Encoding.UTF8;
var parsed = EvalArguments.Parse(args);
if (parsed.Help || parsed.Error is not null)
{
    if (parsed.Error is not null) Console.Error.WriteLine(parsed.Error);
    Console.WriteLine(EvalArguments.Usage);
    return parsed.Error is null ? 0 : 4;
}
// Hidden: the packaged single file's bundled components (pelly doctor --offline-selftest rows), before any config or HTTP client.
if (parsed.OfflineSelfTest) return SelfTestCommand.Run(Console.Out); // 0 all Ok, 4 otherwise
if (parsed.Command == EvalArguments.Compare) return CompareCommand.Run(parsed.Positional, Console.Out); // R4 verdict: 0 adopted, 3 rejected, 4 not judged
if (parsed.Command == EvalArguments.Validate) return ValidateCommand.Run(parsed, Console.Out, Console.Error, processes: new SystemProcessRunner(TimeProvider.System)); // offline definition check: 0 no errors, 4 errors

if (parsed.Command == EvalArguments.Export) return ExportCommand.Run(parsed, Console.Out); // sanitized carry-out copy: 0 done, 4 refused

// Shared composition root of `run` and `record`: the only HttpClient / backend construction in this program.
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

if (parsed.Command == EvalArguments.Record) return await RecordCommand.RunAsync(parsed, backend, options, Console.Out, cts.Token);

var suiteDir = parsed.Suite ?? EvalPaths.FindSuite();
if (suiteDir is null) return Fail($"eval/benchmark/{BenchmarkSuite.FileName}을 찾을 수 없습니다 — 저장소 안에서 실행하거나 --suite <폴더>를 지정하세요.");
var suiteLoad = BenchmarkSuite.Load(suiteDir);
if (suiteLoad.Suite is not { } suite) return Fail("스위트 오류:\n  " + string.Join("\n  ", suiteLoad.Errors));
if (!Directory.Exists(suite.TasksDir)) return Fail($"작업 폴더가 없습니다: {suite.TasksDir}");
var plan = RunPlan.Prepare(suite, fs, parsed.Tasks, parsed.Kind);
if (plan.LoadErrors.Count > 0)
    return Fail("작업 파일을 읽을 수 없습니다 (validate로 확인하세요):\n  " + string.Join("\n  ", plan.LoadErrors));
var tasks = plan.Tasks;
if (tasks.Count == 0) return Fail($"선택된 작업이 없습니다 (--tasks {parsed.Tasks ?? "*"}, --kind {parsed.Kind ?? "*"}).");
foreach (var t in tasks.Where(t => t.Resolved is null))
    Console.Error.WriteLine($"작업 정의 오류: {t.Error}");

var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
var outDir = Path.GetFullPath(parsed.Out ?? Path.Combine(suite.ResultsDir, stamp));
if (RunPlan.OutConflict(outDir) is { } outConflict) return Fail(outConflict);

var status = await backend.GetStatusAsync(cts.Token);
if (!status.Reachable || !status.ModelInstalled)
    return Fail($"LLM 서버를 쓸 수 없습니다 ({options.Llm.Endpoint}, 모델 {options.Llm.Model}): {status.Error ?? "모델이 설치되어 있지 않습니다"}");

ISqlExecutor? sql = null;
string? sqlSkip = null;
if (tasks.Any(t => t.Task.Kind == "sql" && t.Resolved is not null))
{
    if (string.IsNullOrWhiteSpace(options.Sql.ConnectionString))
        sqlSkip = "SQL 연결이 없습니다 — 환경 변수 PELLY_SQL_CONNECTION(또는 sql.connectionString)을 지정하세요";
    else if (SqlTargetPolicy.Check(SqlConnectionText.Endpoint(options.Sql.ConnectionString), options.Sql) is { Allowed: false } check)
        sqlSkip = check.Message + " (--config의 pelly.json에서 허용 목록을 지정하세요)";
    else
        sql = new SqlServerExecutor(options.Sql.ConnectionString);
}

Directory.CreateDirectory(outDir);
Console.WriteLine($"스위트 {suite.Name} ({suite.Dir}) · 작업 {tasks.Count}건{(parsed.Repeat > 1 ? $" × {parsed.Repeat}회" : "")} → {outDir}");

var runner = new BenchmarkRunner(new RunnerSetup(fs, new SystemProcessRunner(TimeProvider.System), backend, options, TimeProvider.System,
    suite, sql, sqlSkip, Console.WriteLine, KeepWork: parsed.KeepWork));
var json = new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
var prompts = PromptResources.LoadDefault();
var conditions = new RunConditions(options.Llm.Model, prompts.Version, prompts.SqlVersion, options.Llm.ThinkMode.ToString(),
    options.Budget.Think, options.Sampling.Think, options.Sampling.Code, PellyInfo.Version, parsed.Tasks, parsed.Kind,
    status.Version, status.ModelDigest);
var header = $"* 실행: {stamp} · pelly {PellyInfo.Version} · 모델 `{options.Llm.Model}` ({options.Llm.Endpoint}, Ollama {status.Version}) · "
    + $"thinkMode {options.Llm.ThinkMode} · {Environment.OSVersion.Platform}\n* 스위트: {suite.Name}\n* 선택: --tasks {parsed.Tasks ?? "*"} --kind {parsed.Kind ?? "*"}"
    + (parsed.Repeat > 1 ? $" --repeat {parsed.Repeat}" : "");

async Task<RepeatedRun.RunOnce> RunOnceAsync(string dir, int run)
{
    Directory.CreateDirectory(dir);
    if (parsed.Repeat > 1) Console.WriteLine($"== 회차 {run}/{parsed.Repeat} → {dir}");
    var results = new List<TaskResult>();
    var cancelled = false;
    foreach (var (task, resolved, error) in tasks)
    {
        // Definition failures (schema, unresolved target) are recorded and never run: their ids may not even be path-safe.
        if (resolved is null)
        {
            results.Add(TaskResult.Failed(task, false, error!, "Definition"));
            continue;
        }
        try
        {
            results.Add(await runner.RunAsync(resolved, dir, cts.Token));
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            cancelled = true;
            break;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[{task.Id}] 실행기 오류: {ex}");
            results.Add(TaskResult.Failed(task, resolved.IsLegacy, $"실행기 오류: {ex.Message}", "RunnerError"));
        }
    }

    File.WriteAllText(Path.Combine(dir, "results.json"), JsonSerializer.Serialize(results, json));
    var runHeader = header + (parsed.Repeat > 1 ? $"\n* 회차: {run}/{parsed.Repeat}" : "") + (cancelled ? "\n* **중단됨** — 끝난 작업만 집계했습니다" : "");
    var report = KpiReport.Render(results, runHeader);
    File.WriteAllText(Path.Combine(dir, "report.md"), report);
    Console.WriteLine();
    Console.WriteLine(report);
    Console.WriteLine($"결과: {Path.Combine(dir, "report.md")}");
    return new RepeatedRun.RunOnce(results, cancelled);
}

var outcome = await RepeatedRun.ExecuteAsync(parsed.Repeat, outDir, RunOnceAsync, conditions, header);
if (outcome.Summary is not null)
{
    var summaryMd = Path.Combine(outDir, "summary.md");
    Console.WriteLine();
    Console.WriteLine(File.ReadAllText(summaryMd));
    Console.WriteLine($"반복 요약: {summaryMd}");
}

if (outcome.Cancelled) return 2;
return outcome.Runs.Any(r => r.Any(t => t.InvariantViolated)) ? 1 : 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 4;
}
