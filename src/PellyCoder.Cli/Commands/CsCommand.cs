using System.ComponentModel;
using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class CsSettings : RunSettings
{
    [CommandArgument(0, "<FILE>")]
    [Description("수정할 C# 파일")]
    public string File { get; init; } = "";

    [CommandArgument(1, "[REQUEST]")]
    [Description("수정 요청문 (생략하면 대화형으로 입력)")]
    public string? Request { get; init; }

    [CommandOption("--class <NAME>")]
    [Description("대상 클래스 이름")]
    public string? Class { get; init; }

    [CommandOption("--method <NAME>")]
    [Description("대상 메서드 이름 (생략하면 요청문·모델로 찾음)")]
    public string? Method { get; init; }

    [CommandOption("--line <N>")]
    [Description("대상 줄 번호, IDE 커서 위치 (--class/--method와 함께 쓸 수 없음)")]
    public int? Line { get; init; }

    [CommandOption("--strict-baseline")]
    [Description("원래 있던 CS 오류도 종료 4로 처리 (build.strictBaseline)")]
    public bool StrictBaseline { get; init; }

    [CommandOption("--warn-as-error")]
    [Description("빌드 경고를 오류로 취급 (build.warnAsError)")]
    public bool WarnAsError { get; init; }

    [CommandOption("--test")]
    [Description("검증 때 테스트도 실행 (새로 실패한 테스트만 실패로 봄)")]
    public bool Test { get; init; }
}

/// <summary><c>pelly cs</c>: validates arguments, merges configuration, then hands the run to <see cref="IRunner"/>.</summary>
public sealed class CsCommand(
    IFileSystem fs, OptionsResolver resolver, IRunner runner, IAnsiConsole console, IUserInteraction ui, CancellationToken cancellation)
    : AsyncCommand<CsSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CsSettings settings, CancellationToken _)
    {
        if (settings.Line is not null && (settings.Class is not null || settings.Method is not null))
            return Fail("--line은 --class/--method와 함께 쓸 수 없습니다");
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

        var loaded = resolver.Resolve(
            file, settings.Config, settings.Think, settings.Model, settings.Endpoint, settings.StrictBaseline, settings.WarnAsError);
        if (loaded.Options is null)
        {
            foreach (var error in loaded.Errors) console.MarkupLine($"[red]{Markup.Escape(error)}[/]");
            return ExitCodes.EnvError;
        }

        var request = new RunRequest(
            file, settings.Class, settings.Method, settings.Line, requestText, settings.Yes, settings.DryRun, settings.MaxRetries,
            Verbose: settings.Verbose, RunTests: settings.Test);
        try
        {
            return await runner.RunAsync(request, loaded.Options, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The orchestrator turns cancellation into exit 2 itself; this covers a cancel before it took over.
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
