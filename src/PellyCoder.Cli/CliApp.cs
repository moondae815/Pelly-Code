using PellyCoder.Cli.Commands;
using PellyCoder.Core;
using PellyCoder.Core.Fsm;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli;

public static class CliApp
{
    public static void Configure(IConfigurator config)
    {
        config.SetApplicationName("pelly");
        config.SetApplicationVersion(PellyInfo.Version);
        config.AddCommand<CsCommand>("cs").WithDescription("C# 메서드 하나를 요청대로 수정합니다.");
        config.AddCommand<DoctorCommand>("doctor").WithDescription("Ollama 서버·모델·토크나이저 환경을 점검합니다.");

        // Without a handler Spectre returns -1 (process exit 255) for parse errors; PRD FR-00 allows only 0–5.
        var settings = config.Settings;
        config.SetExceptionHandler((ex, resolver) =>
        {
            var console = resolver?.Resolve(typeof(IAnsiConsole)) as IAnsiConsole ?? settings.Console ?? AnsiConsole.Console;
            var shutdown = resolver?.Resolve(typeof(CancellationToken)) is CancellationToken t ? t : CancellationToken.None;
            return HandleException(ex, console, shutdown);
        });
    }

    /// <summary>Exit code for an exception that escaped a command or came from argument parsing.</summary>
    /// <remarks>An <see cref="OperationCanceledException"/> is a user cancel (2) only when the shared shutdown token is cancelled; otherwise it is unexpected (4).</remarks>
    public static int HandleException(Exception ex, IAnsiConsole console, CancellationToken shutdown)
    {
        if (ex is OperationCanceledException && shutdown.IsCancellationRequested)
        {
            console.MarkupLine("[yellow]사용자가 실행을 중단했습니다.[/]");
            return ExitCodes.UserCancelled;
        }
        console.MarkupLine(ex is OperationCanceledException
            ? $"[red]예기치 않은 오류로 중단합니다: {Markup.Escape(ex.GetType().Name)}: {Markup.Escape(ex.Message)}[/]"
            : $"[red]{Markup.Escape(ex.Message)}[/]");
        return ExitCodes.EnvError;
    }

    /// <summary>Backstop for a result outside 0–5 (Spectre uses negative codes for its own failures): treated as an argument error.</summary>
    public static int NormalizeExitCode(int code) => code is >= ExitCodes.Success and <= ExitCodes.SafetyRejected ? code : ExitCodes.EnvError;
}
