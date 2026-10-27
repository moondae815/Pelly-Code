using System.ComponentModel;
using PellyCoder.Cli.Completion;
using PellyCoder.Core;
using PellyCoder.Core.Fsm;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class CompletionSettings : CommandSettings
{
    [CommandArgument(0, "[SHELL]")]
    [Description("셸 이름: powershell (pwsh도 같음)")]
    public string? Shell { get; init; }
}

/// <summary><c>pelly completion powershell</c>: prints the PowerShell completion script (design 2026-10-27 §4.1).</summary>
public sealed class CompletionCommand(IAnsiConsole console) : Command<CompletionSettings>
{
    /// <summary>The supported shell name (<c>pwsh</c> is accepted as the same).</summary>
    public const string PowerShell = "powershell";

    public override int Execute(CommandContext context, CompletionSettings settings, CancellationToken cancellation)
    {
        if (settings.Shell is not { } shell
            || !(shell.Equals(PowerShell, StringComparison.OrdinalIgnoreCase) || shell.Equals("pwsh", StringComparison.OrdinalIgnoreCase)))
        {
            console.MarkupLine("[red]지원하는 셸은 powershell입니다: pelly completion powershell[/]");
            return ExitCodes.EnvError;
        }

        // Straight to the output writer: the console would parse '[' as markup and wrap long lines at its width.
        var writer = console.Profile.Out.Writer;
        writer.Write(PowerShellCompletion.Script(CompletionTree.Build(), PellyInfo.Version));
        writer.Flush();
        return ExitCodes.Success;
    }
}
