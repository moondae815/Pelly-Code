using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Cli;
using PellyCoder.Core.Fsm;
using Spectre.Console.Testing;

namespace PellyCoder.Cli.Tests;

public sealed class CompositionTests
{
    [Fact]
    public void RunnerFactory_UsesTheGivenInteraction_NotTheConsoleOne()
    {
        var consoleUi = new FakeUi(true);
        using var provider = Composition.Services(consoleUi, new TestConsole(), CancellationToken.None).BuildServiceProvider();
        var windowUi = new FakeUi(true);

        var runner = provider.GetRequiredService<RunnerFactory>()(windowUi);

        var orchestrator = Assert.IsType<Composition.OrchestratorRunner>(runner);
        Assert.Same(windowUi, orchestrator.Interaction);
        // The DI runner (cs/sql/spec) keeps the console interaction.
        Assert.Same(consoleUi, Assert.IsType<Composition.OrchestratorRunner>(provider.GetRequiredService<IRunner>()).Interaction);
    }
}
