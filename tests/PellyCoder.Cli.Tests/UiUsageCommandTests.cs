using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Cli;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Ui.Session;
using PellyCoder.Ui.Views;
using Spectre.Console.Cli.Testing;

namespace PellyCoder.Cli.Tests;

public sealed class UiUsageCommandTests
{
    /// <summary>Never opens a window.</summary>
    private sealed class FakeLauncher : IUiLauncher
    {
        public int Runs;

        public int Run(UiLaunch launch, Func<GuiUserInteraction, UiSession> createSession)
        {
            Runs++;
            return 0;
        }
    }

    /// <summary>Returns <paramref name="exit"/> without drawing anything.</summary>
    private sealed class FakeSelfTest(int exit) : IUiSelfTest
    {
        public int Calls;

        public int Run()
        {
            Calls++;
            return exit;
        }
    }

    private sealed record Harness(CommandAppTester App, FakeLauncher Launcher, FakeSelfTest SelfTest);

    private static Harness Build(int selfTestExit = 0)
    {
        var launcher = new FakeLauncher();
        var selfTest = new FakeSelfTest(selfTestExit);
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(new MockFileSystem());
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton<IUiLauncher>(launcher);
        services.AddSingleton<IUiSelfTest>(selfTest);
        services.AddSingleton<IUserInteraction>(new FakeUi(false));
        services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
        services.AddSingleton(typeof(CancellationToken), CancellationToken.None);
        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);
        return new Harness(app, launcher, selfTest);
    }

    [Fact]
    public void UiWithoutCommand_PrintsUsage_Exit0()
    {
        var h = Build();

        var result = h.App.Run("ui");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("pelly ui cs <파일>", result.Output);
        Assert.Contains("pelly ui sql <파일>", result.Output);
        Assert.Equal(0, h.Launcher.Runs);
        Assert.Equal(0, h.SelfTest.Calls);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    public void UiSelfTest_ExitIsTheSelfTestResult(int selfTestExit)
    {
        var h = Build(selfTestExit);

        var result = h.App.Run("ui", "--selftest");

        Assert.Equal(selfTestExit, result.ExitCode);
        Assert.Equal(1, h.SelfTest.Calls);
        Assert.Equal(0, h.Launcher.Runs);
    }

    [Fact]
    public void UiSelfTest_IsHiddenFromHelp()
    {
        var result = Build().App.Run("ui", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain("selftest", result.Output);
    }

    [Fact]
    public void UiSelfTest_HasNoValueOptions() =>
        Assert.Null(CliApp.FindOptionWithoutValue(["ui", "--selftest"]));
}
