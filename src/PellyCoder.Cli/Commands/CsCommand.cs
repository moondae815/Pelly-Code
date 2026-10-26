using System.ComponentModel;
using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Testing;
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

    [CommandOption("--verify-test <NAME>")]
    [Description("패치 전 실패·패치 뒤 통과해야 하는 테스트 (여러 번 지정 가능)")]
    public string[]? VerifyTest { get; init; }

    [CommandOption("--expect-break <NAME>")]
    [Description("이 변경으로 실패해도 되는 기존 테스트 (--verify-test와 함께)")]
    public string[]? ExpectBreak { get; init; }
}

/// <summary><c>pelly cs</c>: validates arguments, merges configuration, then hands the run to <see cref="IRunner"/>.</summary>
public sealed class CsCommand(
    IFileSystem fs, OptionsResolver resolver, IRunner runner, IAnsiConsole console, IUserInteraction ui, CancellationToken cancellation)
    : AsyncCommand<CsSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, CsSettings settings, CancellationToken _)
    {
        if (RunArguments.ValidateCs(settings) is [var invalid, ..])
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

        var (prepared, errors) = RunArguments.PrepareCs(settings, fs, resolver);
        if (prepared is null)
            return Report(errors);

        var request = RunArguments.CsRequest(settings, prepared.File, requestText);
        try
        {
            return await runner.RunAsync(request, prepared.Options, cancellation);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // The orchestrator turns cancellation into exit 2 itself; this covers a cancel before it took over.
            console.MarkupLine("[yellow]사용자가 실행을 중단했습니다.[/]");
            return ExitCodes.UserCancelled;
        }
    }

    // A missing file is a plain failure; configuration errors are each printed in red. Both exit with EnvError.
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
