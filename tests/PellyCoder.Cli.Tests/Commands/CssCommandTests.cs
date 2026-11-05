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

public class CssCommandTests
{
    private const string File = "/w/site/a.css";

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
        fs.AddFile(File, new MockFileData("a { color: red; }"));
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
    [InlineData("--selector", ".a", "--line", "3")]
    [InlineData("--selector", ".a", "--lines", "1-2")]
    [InlineData("--line", "3", "--lines", "1-2")]
    public void Css_TwoTargetOptions_Rejected(params string[] options)
    {
        var (app, runner) = Build();

        var result = app.Run(["css", File, "요청", .. options]);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("--selector, --line, --lines 중 하나만 쓸 수 있습니다", result.Output);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void Css_EmptySelector_Rejected()
    {
        var (app, runner) = Build();

        var result = app.Run("css", File, "요청", "--selector", "  ");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("--selector가 비어 있습니다", result.Output);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void Css_PassesSelector()
    {
        var (app, runner) = Build();

        var result = app.Run("css", File, "요청", "--selector", ".list_tbl th");

        Assert.Equal(0, result.ExitCode);
        var request = runner.Request!;
        Assert.Equal(EditKind.Css, request.Kind);
        Assert.Equal(".list_tbl th", request.Selector);
        Assert.Null(request.MethodName);
        Assert.Null(request.Lines);
    }
}
