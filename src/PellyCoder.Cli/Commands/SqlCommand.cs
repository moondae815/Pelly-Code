using System.ComponentModel;
using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class SqlSettings : CommandSettings
{
    [CommandArgument(0, "<FILE>")]
    [Description("수정할 SQL 파일")]
    public string File { get; init; } = "";

    [CommandArgument(1, "[REQUEST]")]
    [Description("수정 요청문 (생략하면 대화형으로 입력)")]
    public string? Request { get; init; }

    [CommandOption("--object <NAME>")]
    [Description("대상 객체 이름, 예: dbo.MyProc (생략하면 파일의 객체가 하나일 때 자동 선택)")]
    public string? Object { get; init; }

    [CommandOption("--line <N>")]
    [Description("대상 줄 번호, IDE 커서 위치 (--object와 함께 쓸 수 없음)")]
    public int? Line { get; init; }

    [CommandOption("--yes")]
    [Description("검토 질문 없이 승인")]
    public bool Yes { get; init; }

    [CommandOption("--dry-run")]
    [Description("파일을 쓰지 않고 diff만 보여줌")]
    public bool DryRun { get; init; }

    [CommandOption("--max-retries <N>")]
    [Description("교정 재시도 최대 횟수 (기본: retry.max)")]
    public int? MaxRetries { get; init; }

    [CommandOption("--config <PATH>")]
    [Description("작업 공간 설정 파일 경로 (기본: <루트>/pelly.json)")]
    public string? Config { get; init; }

    [CommandOption("--think <MODE>")]
    [Description("off | retry | always (llm.thinkMode 덮어쓰기)")]
    public string? Think { get; init; }

    [CommandOption("--model <TAG>")]
    [Description("모델 태그 (llm.model 덮어쓰기)")]
    public string? Model { get; init; }

    [CommandOption("--endpoint <URL>")]
    [Description("Ollama 주소 (llm.endpoint 덮어쓰기)")]
    public string? Endpoint { get; init; }
}

/// <summary><c>pelly sql</c>: validates arguments, merges configuration, then hands the run to <see cref="IRunner"/>.</summary>
public sealed class SqlCommand(
    IFileSystem fs, OptionsResolver resolver, IRunner runner, IAnsiConsole console, IUserInteraction ui, CancellationToken cancellation)
    : AsyncCommand<SqlSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, SqlSettings settings, CancellationToken _)
    {
        if (settings.Line is not null && settings.Object is not null)
            return Fail("--line은 --object와 함께 쓸 수 없습니다");
        if (settings.Line is <= 0)
            return Fail("--line은 1 이상이어야 합니다");
        if (settings.MaxRetries is < 0)
            return Fail("--max-retries는 0 이상이어야 합니다");

        var requestText = settings.Request;
        if (requestText is null)
        {
            if (!ui.IsInteractive)
                return Fail("요청문(REQUEST)이 필요합니다 (비대화형 실행)");
            requestText = console.Prompt(new TextPrompt<string>("수정 요청을 입력하세요:").AllowEmpty());
        }
        if (string.IsNullOrWhiteSpace(requestText))
            return Fail("요청문(REQUEST)이 비어 있습니다");

        var file = fs.Path.GetFullPath(settings.File);
        if (!fs.File.Exists(file))
            return Fail($"파일을 찾을 수 없습니다: {file}");

        var loaded = resolver.Resolve(file, settings.Config, settings.Think, settings.Model, settings.Endpoint);
        if (loaded.Options is null)
        {
            foreach (var error in loaded.Errors) console.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            return ExitCodes.EnvError;
        }

        var request = new RunRequest(
            file, null, null, settings.Line, requestText, settings.Yes, settings.DryRun, settings.MaxRetries,
            ObjectName: settings.Object, Kind: EditKind.Sql);
        try
        {
            return await runner.RunAsync(request, loaded.Options, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            console.MarkupLine("[yellow]사용자가 실행을 중단했습니다.[/]");
            return ExitCodes.UserCancelled;
        }
    }

    private int Fail(string message)
    {
        console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return ExitCodes.EnvError;
    }
}
