using System.ComponentModel;
using PellyCoder.Core.Fsm;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class SqlPullSettings : CommandSettings
{
    [CommandArgument(0, "<OBJECTS>")]
    [Description("가져올 객체 이름 (예: dbo.usp_X)")]
    public string[] Objects { get; init; } = [];

    [CommandOption("--from <SOURCE>")]
    [Description("가져올 서버 (운영 연결)")]
    public string? From { get; init; }

    [CommandOption("--out <DIR>")]
    [Description("저장할 폴더")]
    public string? Out { get; init; }

    [CommandOption("--force")]
    [Description("이미 있는 파일을 덮어씀")]
    public bool Force { get; init; }

    [CommandOption("--config <PATH>")]
    [Description("작업 공간 설정 파일 경로 (기본: <루트>/pelly.json)")]
    public string? Config { get; init; }

    [CommandOption("--pause-on-exit")]
    [Description("끝난 뒤 키 입력을 기다림 (IDE 외부 도구 창용)")]
    public bool PauseOnExit { get; init; }
}

/// <summary><c>pelly sql pull</c>: placeholder until the pull task fills it in.</summary>
public sealed class SqlPullCommand(IAnsiConsole console) : AsyncCommand<SqlPullSettings>
{
    public override Task<int> ExecuteAsync(CommandContext context, SqlPullSettings settings, CancellationToken _)
    {
        console.MarkupLine("[red]아직 구현되지 않았습니다.[/]");
        return Task.FromResult(ExitCodes.EnvError);
    }
}
