using System.ComponentModel;
using System.IO.Abstractions;
using PellyCoder.Core.Fsm;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class TestSettings : RunSettings
{
    [CommandArgument(0, "<FILE>")]
    [Description("테스트를 만들 대상이 있는 C# 파일")]
    public string File { get; init; } = "";

    [CommandArgument(1, "[HINT]")]
    [Description("무엇을 확인할지 덧붙이는 말 (선택, 200토큰 이내)")]
    public string? Hint { get; init; }

    [CommandOption("--class <NAME>")]
    [Description("대상 클래스 이름")]
    public string? Class { get; init; }

    [CommandOption("--method <NAME>")]
    [Description("대상 메서드 이름 (--method나 --line 중 하나 필요)")]
    public string? Method { get; init; }

    [CommandOption("--line <N>")]
    [Description("대상 줄 번호, IDE 커서 위치 (--class/--method와 함께 쓸 수 없음)")]
    public int? Line { get; init; }

    [CommandOption("--count <N>")]
    [Description("만들 테스트 수 1~5 (기본 3)")]
    public int? Count { get; init; }

    [CommandOption("--test-project <PATH>")]
    [Description("테스트를 넣을 테스트 프로젝트 (.csproj)")]
    public string? TestProject { get; init; }
}

/// <summary><c>pelly test</c>: validates arguments, merges configuration, then hands the run to <see cref="ICharTestRunner"/>.</summary>
public sealed class TestCommand(
    IFileSystem fs, OptionsResolver resolver, ICharTestRunner runner, IAnsiConsole console, CancellationToken cancellation)
    : AsyncCommand<TestSettings>
{
    public override async Task<int> ExecuteAsync(CommandContext context, TestSettings settings, CancellationToken _)
    {
        if (RunArguments.ValidateTest(settings) is [var invalid, ..])
            return Fail(invalid);

        var (prepared, errors) = RunArguments.PrepareTest(settings, fs, resolver);
        if (prepared is null)
        {
            foreach (var error in errors) Fail(error);
            return ExitCodes.EnvError;
        }

        try
        {
            return await runner.RunAsync(RunArguments.TestRequest(settings, prepared.File), prepared.Options, cancellation);
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
