using System.ComponentModel;
using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class CssSettings : RunSettings
{
    [CommandArgument(0, "<FILE>")]
    [Description("수정할 CSS 파일 (.css) 또는 페이지(.asp·.inc·.htm·.html)의 <style> 블록")]
    public string File { get; init; } = "";

    [CommandArgument(1, "[REQUEST]")]
    [Description("수정 요청문 (생략하면 대화형으로 입력)")]
    public string? Request { get; init; }

    [CommandOption("--selector <SELECTOR>")]
    [Description("대상 규칙의 선택자, 예: \".list_tbl th\" (--line, --lines와 함께 쓸 수 없음)")]
    public string? Selector { get; init; }

    [CommandOption("--line <N>")]
    [Description("대상 줄 번호, IDE 커서 위치 (--selector, --lines와 함께 쓸 수 없음)")]
    public int? Line { get; init; }

    [CommandOption("--lines <A-B>")]
    [Description("대상 줄 범위, 예: 10-25 (--selector, --line과 함께 쓸 수 없음)")]
    public string? Lines { get; init; }
}

/// <summary><c>pelly css</c>: validates arguments, merges configuration, then hands the run to <see cref="IRunner"/>.</summary>
public sealed class CssCommand(
    IFileSystem fs, OptionsResolver resolver, IRunner runner, IAnsiConsole console, IUserInteraction ui, CancellationToken cancellation)
    : AsyncCommand<CssSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CssSettings settings, CancellationToken _)
    {
        if (RunArguments.ValidateCss(settings) is [var invalid, ..])
            return Fail(invalid);

        var requestText = settings.Request;
        if (requestText is null)
        {
            if (!ui.IsInteractive)
                return Fail("요청문(REQUEST)이 필요합니다 (비대화형 실행)");
            requestText = console.Prompt(new TextPrompt<string>("수정 요청을 입력하세요:").AllowEmpty());
        }
        if (string.IsNullOrWhiteSpace(requestText))
            return Fail("요청문(REQUEST)이 비어 있습니다");

        var (prepared, errors) = RunArguments.PrepareWeb(settings, settings.File, fs, resolver);
        if (prepared is null)
            return Report(errors);

        var request = RunArguments.CssRequest(settings, prepared.File, requestText);
        try
        {
            return await runner.RunAsync(request, prepared.Options, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            console.MarkupLine("[yellow]사용자가 실행을 중단했습니다.[/]");
            return ExitCodes.UserCancelled;
        }
    }

    private int Report(IReadOnlyList<string> errors)
    {
        foreach (var error in errors) console.MarkupLine($"[red]{Markup.Escape(error)}[/]");
        return ExitCodes.EnvError;
    }

    private int Fail(string message)
    {
        console.MarkupLine($"[red]{Markup.Escape(message)}[/]");
        return ExitCodes.EnvError;
    }
}
