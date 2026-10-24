using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

/// <summary>Default of the <c>spec</c> branch: a bare <c>pelly spec</c> is a usage error (exit 4, via <see cref="CliApp.HandleException"/>).</summary>
public sealed class SpecUsageCommand : Command
{
    public override int Execute(CommandContext context, CancellationToken cancellation) =>
        throw new InvalidOperationException("spec 뒤에 하위 명령이 필요합니다: `pelly spec new` 또는 `pelly spec check`");
}
