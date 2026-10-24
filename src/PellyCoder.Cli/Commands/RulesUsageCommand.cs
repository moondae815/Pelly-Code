using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

/// <summary>Default of the <c>rules</c> branch: a bare <c>pelly rules</c> is a usage error (exit 4, via <see cref="CliApp.HandleException"/>), not Spectre's exit 1.</summary>
public sealed class RulesUsageCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken cancellation) =>
        throw new InvalidOperationException("rules 뒤에 하위 명령이 필요합니다: `pelly rules check` 또는 `pelly rules init`");
}
