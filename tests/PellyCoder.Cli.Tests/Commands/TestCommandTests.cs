using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.CharTests;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;
using Spectre.Console;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;

namespace PellyCoder.Cli.Tests.Commands;

public class TestCommandTests
{
    private const string File = "/w/src/A.cs";

    private sealed class FakeCharTestRunner(int exitCode) : ICharTestRunner
    {
        public CharTestRequest? Request;
        public PellyOptions? Options;
        public int Calls;

        public Task<int> RunAsync(CharTestRequest request, PellyOptions options, CancellationToken ct)
        {
            Calls++;
            Request = request;
            Options = options;
            return Task.FromResult(exitCode);
        }
    }

    private static (CommandAppTester App, FakeCharTestRunner Runner, TestConsole Console, MockFileSystem Fs) Build(int exitCode = 0)
    {
        var fs = new MockFileSystem();
        fs.AddFile(File, new MockFileData("class A { void M() {} }"));
        fs.AddDirectory("/w/.git");
        var runner = new FakeCharTestRunner(exitCode);
        var console = new TestConsole().Width(200);
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fs);
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton<ICharTestRunner>(runner);
        services.AddSingleton<IAnsiConsole>(console);
        services.AddSingleton<IUserInteraction>(new FakeUi(false));
        services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
        services.AddSingleton(typeof(CancellationToken), CancellationToken.None);
        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);
        return (app, runner, console, fs);
    }

    [Fact]
    public void Test_RequiresMethodOrLine()
    {
        var (app, runner, _, _) = Build();

        var result = app.Run("test", File);

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, runner.Calls);
        Assert.Contains("--method나 --line이 필요합니다", result.Output);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("6")]
    [InlineData("-1")]
    public void Test_CountOutOfRange(string count)
    {
        var (app, runner, _, _) = Build();

        var result = app.Run("test", File, "--method", "M", "--count", count);

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, runner.Calls);
        Assert.Contains("--count는 1~5입니다", result.Output);
    }

    [Theory]
    [InlineData("--method", "M")]
    [InlineData("--class", "A")]
    public void Test_LineWithMethod_Rejected(string name, string value)
    {
        var (app, runner, _, _) = Build();

        var result = app.Run("test", File, "--line", "1", name, value);

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, runner.Calls);
        Assert.Contains("--line은 --class/--method와 함께 쓸 수 없습니다", result.Output);
    }

    [Fact]
    public void Test_PassesRequestToRunner()
    {
        var (app, runner, _, fs) = Build(exitCode: 1);

        var result = app.Run("test", File, "빈 입력도 확인", "--class", "A", "--method", "M", "--count", "2",
            "--test-project", "/w/tests/T.csproj", "--yes", "--dry-run", "--max-retries", "1", "--verbose", "--model", "m1");

        Assert.Equal(1, result.ExitCode);
        var r = runner.Request!;
        // The CLI resolves the path through the (mock) file system, which roots it on its own drive on Windows.
        Assert.Equal(fs.Path.GetFullPath(File), r.FilePath);
        Assert.Equal("A", r.ClassName);
        Assert.Equal("M", r.MethodName);
        Assert.Null(r.Line);
        Assert.Equal("빈 입력도 확인", r.Hint);
        Assert.Equal(2, r.Count);
        Assert.Equal("/w/tests/T.csproj", r.TestProject);
        Assert.True(r.Yes);
        Assert.True(r.DryRun);
        Assert.Equal(1, r.MaxRetries);
        Assert.True(r.Verbose);
        Assert.Equal("m1", runner.Options!.Llm.Model);
    }

    [Fact]
    public void Test_DefaultCountIsThree_LineOnly()
    {
        var (app, runner, _, _) = Build();

        Assert.Equal(0, app.Run("test", File, "--line", "1").ExitCode);

        Assert.Equal(CharTestLimits.DefaultCount, runner.Request!.Count);
        Assert.Equal(1, runner.Request.Line);
        Assert.Null(runner.Request.Hint);
    }

    [Fact]
    public void Test_MissingFile_ExitsFour()
    {
        var (app, runner, _, _) = Build();

        Assert.Equal(4, app.Run("test", "/w/src/Nope.cs", "--method", "M").ExitCode);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void Test_UnknownOption_ExitsFour()
    {
        var (app, _, _, _) = Build();

        Assert.Equal(4, CliApp.NormalizeExitCode(app.Run("test", File, "--method", "M", "--cuont", "2").ExitCode));
    }

    [Fact]
    public void Test_TestProjectWithoutValue_ExitsFour()
    {
        var (_, _, console, _) = Build();

        Assert.Equal(4, CliApp.RejectOptionWithoutValue(["test", File, "--method", "M", "--test-project"], console));
    }
}
