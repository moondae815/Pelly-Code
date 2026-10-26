using PellyCoder.Core.Fsm;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class UiUsageSettings : CommandSettings
{
    /// <summary>CI and install check: draws the window once off screen (no files touched) and exits 0, or 4 with the reason.</summary>
    [CommandOption("--selftest", IsHidden = true)]
    public bool SelfTest { get; init; }
}

/// <summary>Default of the <c>ui</c> branch: a bare <c>pelly ui</c> shows how to call <c>ui cs</c>/<c>ui sql</c> (exit 0); <c>--selftest</c> runs the GUI self-test.</summary>
public sealed class UiUsageCommand(IUiSelfTest selfTest, IAnsiConsole console) : Command<UiUsageSettings>
{
    public override int Execute(CommandContext context, UiUsageSettings settings, CancellationToken cancellation)
    {
        if (settings.SelfTest) return selfTest.Run();

        console.WriteLine("사용법:");
        console.WriteLine("  pelly ui cs <파일> [요청] [--line <n> | --class <이름> --method <이름>] [cs 실행 옵션]");
        console.WriteLine("  pelly ui sql <파일> [요청] [--line <n> | --object <이름>] [sql 실행 옵션]");
        console.WriteLine("작은 창에서 요청을 쓰고 진행·diff를 본 뒤 「적용」으로 승인합니다. 옵션은 `pelly ui cs --help`를 보십시오.");
        return ExitCodes.Success;
    }
}
