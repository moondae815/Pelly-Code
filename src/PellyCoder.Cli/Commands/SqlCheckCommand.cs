using System.ComponentModel;
using PellyCoder.Core.Fsm;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class SqlCheckSettings : CommandSettings
{
    [CommandArgument(0, "<FILE>")]
    [Description("검사할 SQL 파일")]
    public string File { get; init; } = "";

    [CommandOption("--object <NAME>")]
    [Description("대상 객체 이름, 예: dbo.MyProc")]
    public string? Object { get; init; }

    [CommandOption("--config <PATH>")]
    [Description("작업 공간 설정 파일 경로 (기본: <루트>/pelly.json)")]
    public string? Config { get; init; }

    [CommandOption("--pause-on-exit")]
    [Description("끝난 뒤 키 입력을 기다림 (IDE 외부 도구 창용)")]
    public bool PauseOnExit { get; init; }
}

/// <summary><c>pelly sql check</c>: placeholder until the check task fills it in.</summary>
public sealed class SqlCheckCommand(IAnsiConsole console) : AsyncCommand<SqlCheckSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, SqlCheckSettings settings, CancellationToken _)
    {
        console.MarkupLine("[red]아직 구현되지 않았습니다.[/]");
        return Task.FromResult(ExitCodes.EnvError);
    }
}
