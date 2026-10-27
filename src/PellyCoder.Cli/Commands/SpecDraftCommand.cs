using System.ComponentModel;
using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Specs.Drafting;
using PellyCoder.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class SpecDraftSettings : CommandSettings
{
    [CommandArgument(0, "<ID>")]
    [Description("명세 ID (소문자·숫자·하이픈, 예: cancel-shipped)")]
    public string Id { get; init; } = "";

    [CommandArgument(1, "[REQUEST]")]
    [Description("변경 요청문 (생략하면 대화형으로 입력)")]
    public string? Request { get; init; }

    [CommandOption("--scope <PATH>")]
    [Description("모델에게 보여 줄 범위: 폴더, .csproj, .cs, .sql (하나 이상, 여러 번 지정 가능)")]
    public string[]? Scope { get; init; }

    [CommandOption("--dry-run")]
    [Description("초안과 검사 결과만 보여주고 파일을 쓰지 않음")]
    public bool DryRun { get; init; }

    [CommandOption("--yes")]
    [Description("확인 없이 씀 (비대화형 실행용)")]
    public bool Yes { get; init; }

    [CommandOption("--force")]
    [Description("이미 있는 changes/<id>.md를 덮어씀")]
    public bool Force { get; init; }

    [CommandOption("--config <PATH>")]
    [Description("작업 공간 설정 파일 경로 (기본: <루트>/pelly.json)")]
    public string? Config { get; init; }

    [CommandOption("--model <TAG>")]
    [Description("모델 태그 (llm.model 덮어쓰기)")]
    public string? Model { get; init; }

    [CommandOption("--endpoint <URL>")]
    [Description("Ollama 주소 (llm.endpoint 덮어쓰기)")]
    public string? Endpoint { get; init; }

    [CommandOption("--pause-on-exit")]
    [Description("끝난 뒤 키 입력을 기다림 (IDE 외부 도구 창용)")]
    public bool PauseOnExit { get; init; }
}

/// <summary>
/// <c>pelly spec draft</c> (design §3, §4.5): shows the model's spec draft with its check result and writes
/// <c>changes/&lt;id&gt;.md</c> only after approval (or <c>--yes</c>); every run that reached the model leaves
/// <c>.pelly/runs/&lt;run-id&gt;/draft.json</c>.
/// </summary>
/// <param name="impact">The impact search; null (not registered) uses <see cref="ImpactFinders.Default"/>.</param>
/// <param name="readerFactory">Opens the SQL catalog readers; null (not registered) runs no SQL search.</param>
/// <param name="sqlImpact">The SQL impact search; null (not registered) uses <see cref="SqlImpactFinder"/>.</param>
public sealed class SpecDraftCommand(
    IFileSystem fs, OptionsResolver resolver, IAnsiConsole console, IUserInteraction ui, TimeProvider time, LlmBackendFactory backends,
    CancellationToken cancellation, IImpactFinder? impact = null, Func<string, ISqlDefinitionReader>? readerFactory = null,
    ISqlImpactFinder? sqlImpact = null)
    : AsyncCommand<SpecDraftSettings>
{
    private static readonly JsonSerializerOptions RecordJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = true,
    };

    public override async Task<int> ExecuteAsync(CommandContext context, SpecDraftSettings settings, CancellationToken _)
    {
        if (!SpecNewCommand.IdPattern().IsMatch(settings.Id)) return Fail(SpecNewCommand.IdRule);
        if (settings.Scope is not { Length: > 0 } scopes) return Fail("`--scope`가 하나 이상 필요합니다.");

        var requestText = settings.Request;
        if (requestText is null)
        {
            if (!ui.IsInteractive) return Fail("요청문(REQUEST)이 필요합니다 (비대화형 실행)");
            requestText = console.Prompt(new TextPrompt<string>("변경 요청을 입력하세요:").AllowEmpty());
        }
        if (string.IsNullOrWhiteSpace(requestText)) return Fail("요청문(REQUEST)이 비어 있습니다");
        // Raw mode would let the server read this string as a control token (the same guard as the edit preflight).
        if (SpecialTokenGuard.FindIn(requestText, QwenTokenCounter.Shared.SpecialTokens) is { } token)
            return Fail($"요청문에 모델 제어 토큰 '{token}'이(가) 있어 실행할 수 없습니다");

        // The workspace is the current directory's, as for spec new; the placeholder file name is never read.
        var anchor = fs.Path.Combine(fs.Directory.GetCurrentDirectory(), "pelly-spec");
        var ws = WorkspaceLocator.Resolve(fs, anchor);
        var path = fs.Path.Combine(ws.Root, "changes", settings.Id + ".md");
        var existed = fs.File.Exists(path);
        if (existed && !settings.Force) return Fail($"이미 있는 명세 파일입니다: {path} (--force로 덮어쓸 수 있습니다)");
        if (!settings.Yes && !settings.DryRun && !ui.IsInteractive)
            return Fail("비대화형 실행에서는 --yes 또는 --dry-run이 필요합니다.");

        var loaded = resolver.Resolve(anchor, settings.Config, null, settings.Model, settings.Endpoint);
        if (loaded.Options is not { } options)
        {
            foreach (var error in loaded.Errors) console.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            return ExitCodes.EnvError;
        }

        QwenChatTemplate template;
        try
        {
            template = options.Llm.Template is { } templatePath ? QwenChatTemplate.LoadFromFile(fs, templatePath) : QwenChatTemplate.LoadDefault();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        {
            return Fail($"llm.template을 읽을 수 없습니다: {ex.Message}");
        }

        var request = new DraftRequest(settings.Id, requestText, scopes);
        var model = new DraftModel(backends(options.Llm), template, time);

        // The drafter builds the same index again; building it here lets the summary come before the model wait.
        var (index, scopeError) = DraftIndexBuilder.Build(fs, ws, scopes, options.Sql.CompatLevel);
        if (scopeError is not null) return Fail(scopeError.Message);
        var tokens = model.CountInput(requestText, index!.Render());
        Print($"색인: 파일 {N(index.Files.Count)}개, 멤버 {N(index.Files.Sum(f => f.Members.Count))}개, {N(tokens)} 토큰 (예산 {N(DraftPrompt.InputBudget)})");
        foreach (var skipped in index.Skipped)
            Warn($"경고: 읽거나 해석할 수 없어 색인에서 뺀 파일: {skipped}");

        var sql = readerFactory is null ? null : new SqlImpactSetup(sqlImpact ?? new SqlImpactFinder(fs, options.Sql.CompatLevel), options.Sql, readerFactory);
        var drafter = new SpecDrafter(fs, model, impact ?? ImpactFinders.Default(fs), sql, options.Sql.CompatLevel);

        DraftOutcome? outcome;
        DraftFailure? failure;
        try
        {
            if (tokens <= DraftPrompt.InputBudget) Print("모델 초안 받는 중… (생각 단계 포함, 수십 초 걸릴 수 있음)");
            (outcome, failure) = await drafter.DraftAsync(ws, request, cancellation, elapsed => ui.ShowWaiting(RunPhase.Edit, 0,
                $"서버 응답 대기 중 ({elapsed.TotalSeconds:0}초) — 다른 요청을 처리 중이거나 모델을 불러오는 중일 수 있습니다"));
        }
        catch (LlmUnavailableException ex)
        {
            return Fail($"LLM 서버를 사용할 수 없습니다: {ex.Message} (`pelly doctor`로 확인하세요)");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            console.MarkupLine("[yellow]사용자가 실행을 중단했습니다.[/]");
            return ExitCodes.UserCancelled;
        }

        if (failure is not null)
        {
            if (failure.ExitCode == ExitCodes.VerifyFailed)
            {
                Warn("초안에 작업이 없습니다. 쓰지 않았습니다.");
                foreach (var line in failure.Message.Split('\n')) Print(line);
                return failure.ExitCode;
            }
            return Fail(failure.Message, failure.ExitCode);
        }

        var o = outcome!;
        Show(o);

        var errors = o.Check.Issues.Count(i => i.Severity == SpecIssueSeverity.Error);
        if (errors > 0) Warn("오류가 있는 명세는 `pelly apply`가 실행하지 않습니다. 쓴 뒤 고치십시오.");

        var runDir = fs.Path.Combine(ws.PellyDir, "runs", RunLogger.NewRunId(time));
        if (settings.DryRun)
        {
            Print("--dry-run: 쓰지 않았습니다.");
            WriteRecord(runDir, request, o, "dryRun", null);
            return ExitCodes.Success;
        }

        if (!settings.Yes)
        {
            var question = $"changes/{settings.Id}.md에 쓸까요?" + (existed ? " (이미 있는 파일을 덮어씁니다)" : "");
            if (!await ui.ConfirmAsync(question, defaultValue: false, cancellation))
            {
                Print("쓰지 않았습니다.");
                WriteRecord(runDir, request, o, "rejected", null);
                return ExitCodes.UserCancelled;
            }
        }

        // The file may have appeared while the model ran; without --force it is never replaced.
        if (!settings.Force && fs.File.Exists(path))
        {
            WriteRecord(runDir, request, o, "notWritten", null);
            return Fail($"이미 있는 명세 파일입니다: {path} (--force로 덮어쓸 수 있습니다)");
        }
        try
        {
            fs.Directory.CreateDirectory(fs.Path.GetDirectoryName(path)!);
            fs.File.WriteAllText(path, o.Document.Replace("\r\n", "\n"), new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            WriteRecord(runDir, request, o, "writeFailed", null);
            return Fail($"명세 파일을 쓰지 못했습니다: {path} ({ex.Message})");
        }

        WriteRecord(runDir, request, o, "written", path);
        Print($"명세 파일을 썼습니다: {path}");
        Print("`pelly apply`는 작업마다 다시 승인을 받습니다. 먼저 검토하고 `pelly spec check`로 확인하세요.");
        return ExitCodes.Success;
    }

    private void Show(DraftOutcome o)
    {
        Print("");
        Print("## 작업 (초안)");
        // The task lines of the final document (its check comment, if any, moved them after the check ran).
        var lines = o.Document.Split('\n');
        foreach (var task in ChangeSpecParser.Parse(o.Document).Tasks)
            Print(lines[task.Line - 1]);

        if (o.DroppedLines.Count > 0)
        {
            Print("");
            Warn($"형식에 맞지 않아 버린 줄 {N(o.DroppedLines.Count)}개:");
            foreach (var line in o.DroppedLines) Print("  " + line);
        }

        if (o.Added.Count > 0)
        {
            Print("");
            Print($"영향 탐색으로 더한 작업 {N(o.Added.Count)}개:");
            foreach (var a in o.Added) Print($"  {a.TaskId} {a.Path}#{a.Member} ({a.FromTaskId}의 영향)");
        }

        if (o.ImpactSkippedForErrors) Warn("검사 오류가 있어 영향 탐색을 하지 않았습니다.");
        if (o.ImpactSkippedForCap > 0)
            Warn($"영향 대상 {N(o.ImpactSkippedForCap)}개를 더하면 작업 수 상한 {N(ChangeSpecParser.MaxTasks)}을 넘어 더하지 않았습니다.");

        var errors = o.Check.Issues.Count(i => i.Severity == SpecIssueSeverity.Error);
        Print("");
        Print($"검사: 오류 {N(errors)}, 경고 {N(o.Check.Issues.Count - errors)}");
        foreach (var issue in o.Check.Issues)
        {
            var where = issue.Line > 0 ? $"줄 {N(issue.Line)}: " : "";
            var (color, label) = issue.Severity == SpecIssueSeverity.Error ? ("red", "오류") : ("yellow", "경고");
            console.MarkupLine($"[{color}]{Markup.Escape($"  {label} {where}[{issue.Code}] {issue.Message}")}[/]");
        }
    }

    /// <summary>Best effort: a run record that cannot be written is a warning, never a failure of the command.</summary>
    private void WriteRecord(string runDir, DraftRequest request, DraftOutcome o, string result, string? writtenPath)
    {
        var record = new DraftRunRecord(
            request.Id, request.Request, request.Scopes,
            new DraftRunIndex(o.Index.Files.Count, o.Index.Files.Sum(f => f.Members.Count), o.Call.InputTokens, o.Index.Skipped),
            o.Call.Stage1Prompt, o.Call.Think, o.Call.ThinkTruncated, o.Call.Answer, o.Call.Seconds,
            o.DroppedLines, o.Added, o.ImpactSkippedForErrors, o.ImpactSkippedForCap,
            o.Check.Issues.Select(i => new DraftRunIssue(i.Line, i.Severity == SpecIssueSeverity.Error ? "error" : "warning", i.Code, i.Message)).ToList(),
            result, writtenPath);
        try
        {
            fs.Directory.CreateDirectory(runDir);
            fs.File.WriteAllText(fs.Path.Combine(runDir, "draft.json"), JsonSerializer.Serialize(record, RecordJson) + "\n", new UTF8Encoding(false));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Warn($"실행 기록을 쓰지 못했습니다: {runDir} ({ex.Message})");
        }
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private void Print(string text) => console.MarkupLine(Markup.Escape(text));

    private void Warn(string text) => console.MarkupLine($"[yellow]{Markup.Escape(text)}[/]");

    private int Fail(string message, int code = ExitCodes.EnvError)
    {
        console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return code;
    }

    private sealed record DraftRunIndex(int Files, int Members, int InputTokens, IReadOnlyList<string> Skipped);

    private sealed record DraftRunIssue(int Line, string Severity, string Code, string Message);

    /// <summary><c>draft.json</c>: what the model saw and said, and what became of it. No connection string is ever part of it.</summary>
    private sealed record DraftRunRecord(
        string Id, string Request, IReadOnlyList<string> Scopes, DraftRunIndex Index,
        string Stage1Prompt, string Think, bool ThinkTruncated, string Answer, double Seconds,
        IReadOnlyList<string> DroppedLines, IReadOnlyList<AddedImpactTask> AddedLines, bool ImpactSkippedForErrors, int ImpactSkippedForCap,
        IReadOnlyList<DraftRunIssue> Issues, string Outcome, string? WrittenPath);
}
