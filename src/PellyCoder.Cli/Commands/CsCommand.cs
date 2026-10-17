using System.ComponentModel;
using System.IO.Abstractions;
using PellyCoder.Core.Fsm;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class CsSettings : CommandSettings
{
    [CommandArgument(0, "<FILE>")]
    [Description("수정할 C# 파일")]
    public string File { get; init; } = "";

    [CommandArgument(1, "<REQUEST>")]
    [Description("수정 요청문")]
    public string Request { get; init; } = "";

    [CommandOption("--class <NAME>")]
    [Description("대상 클래스 이름")]
    public string? Class { get; init; }

    [CommandOption("--method <NAME>")]
    [Description("대상 메서드 이름 (M1에서는 필수)")]
    public string? Method { get; init; }

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

/// <summary><c>pelly cs</c>: validates arguments, merges configuration, then hands the run to <see cref="IRunner"/>.</summary>
public sealed class CsCommand(
    IFileSystem fs, OptionsResolver resolver, IRunner runner, IAnsiConsole console, CancellationToken cancellation)
    : AsyncCommand<CsSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CsSettings settings, CancellationToken _)
    {
        if (string.IsNullOrWhiteSpace(settings.Method))
            return Fail("M1에서는 --method로 대상을 지정해야 합니다 (자동 탐색은 다음 버전)");
        if (string.IsNullOrWhiteSpace(settings.Request))
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
            file, settings.Class, settings.Method, settings.Request, settings.Yes, settings.DryRun, settings.MaxRetries);
        return await runner.RunAsync(request, loaded.Options, cancellation);
    }

    private int Fail(string message)
    {
        console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return ExitCodes.EnvError;
    }
}
