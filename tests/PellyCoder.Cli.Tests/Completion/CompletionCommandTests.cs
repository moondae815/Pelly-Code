using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Cli.Completion;
using PellyCoder.Core;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using Spectre.Console.Cli.Testing;

namespace PellyCoder.Cli.Tests.Completion;

public sealed class CompletionCommandTests
{
    private static CommandAppTester App()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(new MockFileSystem());
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton<IUserInteraction>(new FakeUi(false));
        services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
        services.AddSingleton(typeof(CancellationToken), CancellationToken.None);
        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);
        return app;
    }

    private static string Expected => PowerShellCompletion.Script(CompletionTree.Build(), PellyInfo.Version);

    private static string Normalize(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();

    [Theory]
    [InlineData("powershell")]
    [InlineData("pwsh")]
    [InlineData("PowerShell")]
    public void Powershell_PrintsScriptVerbatim_Exit0(string shell)
    {
        var result = App().Run("completion", shell);

        Assert.Equal(0, result.ExitCode);
        // Verbatim: no markup parsing of '[' and no wrapping of long lines at the console width.
        Assert.Equal(Normalize(Expected), Normalize(result.Output));
    }

    [Fact]
    public void NoShell_Exit4WithHint()
    {
        var result = App().Run("completion");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("지원하는 셸은 powershell입니다: pelly completion powershell", result.Output);
    }

    [Fact]
    public void Bash_Exit4WithHint()
    {
        var result = App().Run("completion", "bash");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("pelly completion powershell", result.Output);
    }

    [Fact]
    public void Tree_IncludesCompletionCommand()
    {
        var completion = Assert.Single(CompletionTree.Build().Children, c => c.Name == "completion");
        Assert.True(completion.AcceptsPositional);
    }
}
