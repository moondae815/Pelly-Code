using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Doctor;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using Spectre.Console.Cli.Testing;

namespace PellyCoder.Cli.Tests.Commands;

public class JsCommandTests
{
    private const string File = "/w/site/a.js";

    private sealed class FakeRunner : IRunner
    {
        public RunRequest? Request;
        public int Calls;

        public Task<RunResult> RunDetailedAsync(RunRequest request, PellyOptions options, CancellationToken ct)
        {
            Calls++;
            Request = request;
            return Task.FromResult(new RunResult(0, "run-fake", request.FilePath, null, null, null));
        }
    }

    private sealed class FakeDoctor : IDoctorRunner
    {
        public Task<IReadOnlyList<DoctorCheck>> RunAsync(PellyOptions options, DoctorContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DoctorCheck>>([]);
    }

    private static (CommandAppTester App, FakeRunner Runner) Build(bool interactive = false)
    {
        var fs = new MockFileSystem();
        fs.AddFile(File, new MockFileData("function f() {}"));
        fs.AddDirectory("/w/.git");
        var runner = new FakeRunner();
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fs);
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton<IRunner>(runner);
        services.AddSingleton<IUserInteraction>(new FakeUi(interactive));
        services.AddSingleton<IDoctorRunner>(new FakeDoctor());
        services.AddSingleton<IProcessRunner>(new NoProcessRunner());
        services.AddSingleton(TimeProvider.System);
        Func<string, ISqlDefinitionReader> noReader = _ => throw new InvalidOperationException("no connection expected");
        services.AddSingleton(noReader);
        services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
        services.AddSingleton(typeof(CancellationToken), CancellationToken.None);
        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);
        return (app, runner);
    }

    [Theory]
    [InlineData("--function", "f", "--line", "3")]
    [InlineData("--function", "f", "--lines", "1-2")]
    [InlineData("--line", "3", "--lines", "1-2")]
    public void Js_TwoTargetOptions_Rejected(params string[] options)
    {
        var (app, runner) = Build();

        var result = app.Run(["js", File, "요청", .. options]);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("--function, --line, --lines 중 하나만 쓸 수 있습니다", result.Output);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void Js_LinesFormat_Rejected()
    {
        var (app, runner) = Build();

        var result = app.Run("js", File, "요청", "--lines", "10-5");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("--lines는 A-B 형식입니다 (1 ≤ A ≤ B)", result.Output);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void Js_PassesRequestToRunner()
    {
        var (app, runner) = Build();

        var result = app.Run("js", File, "요청", "--lines", "3-9", "--dry-run");

        Assert.Equal(0, result.ExitCode);
        var request = runner.Request!;
        Assert.Equal(EditKind.Js, request.Kind);
        Assert.Equal(new TextSpanInfo(3, 9), request.Lines);
        Assert.Null(request.MethodName);
        Assert.Null(request.Selector);
        Assert.True(request.DryRun);

        var byFunction = Build();
        byFunction.App.Run("js", File, "요청", "--function", "Order.save");
        Assert.Equal("Order.save", byFunction.Runner.Request!.MethodName);
        Assert.Equal(EditKind.Js, byFunction.Runner.Request.Kind);
    }

    [Fact]
    public void Js_NonInteractiveNoRequest_Fails()
    {
        var (app, runner) = Build(interactive: false);

        var result = app.Run("js", File);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("요청문(REQUEST)이 필요합니다", result.Output);
        Assert.Equal(0, runner.Calls);
    }
}
