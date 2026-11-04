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

public class AspCommandTests
{
    private const string File = "/w/site/page.asp";

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
        fs.AddFile(File, new MockFileData("<% Sub P() : End Sub %>"));
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
    [InlineData("--proc", "P", "--line", "3")]
    [InlineData("--proc", "P", "--lines", "1-2")]
    [InlineData("--line", "3", "--lines", "1-2")]
    public void Asp_TwoTargetOptions_Rejected(params string[] options)
    {
        var (app, runner) = Build();

        var result = app.Run(["asp", File, "요청", .. options]);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("--proc, --line, --lines 중 하나만 쓸 수 있습니다", result.Output);
        Assert.Equal(0, runner.Calls);
    }

    [Theory]
    [InlineData("10-5")]
    [InlineData("a-b")]
    [InlineData("0-3")]
    [InlineData("7")]
    public void Asp_LinesFormat_Rejected(string lines)
    {
        var (app, runner) = Build();

        var result = app.Run("asp", File, "요청", "--lines", lines);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("--lines는 A-B 형식입니다 (1 ≤ A ≤ B)", result.Output);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void Asp_LineNotPositive_Rejected()
    {
        var (app, runner) = Build();

        var result = app.Run("asp", File, "요청", "--line", "0");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("--line은 1 이상이어야 합니다", result.Output);
        Assert.Equal(0, runner.Calls);
    }

    [Fact]
    public void Asp_PassesRequestToRunner()
    {
        var (app, runner) = Build();

        var result = app.Run("asp", File, "요청", "--lines", "3-9", "--dry-run");

        Assert.Equal(0, result.ExitCode);
        var request = runner.Request!;
        Assert.Equal(EditKind.Asp, request.Kind);
        Assert.Equal(new TextSpanInfo(3, 9), request.Lines);
        Assert.Null(request.MethodName);
        Assert.Null(request.Line);
        Assert.Equal("요청", request.Request);
        Assert.True(request.DryRun);

        var byProc = Build();
        byProc.App.Run("asp", File, "요청", "--proc", "ShowOrder");
        Assert.Equal("ShowOrder", byProc.Runner.Request!.MethodName);
        Assert.Null(byProc.Runner.Request.Lines);

        var byLine = Build();
        byLine.App.Run("asp", File, "요청", "--line", "5");
        Assert.Equal(5, byLine.Runner.Request!.Line);
    }

    [Fact]
    public void Asp_NonInteractiveNoRequest_Fails()
    {
        var (app, runner) = Build(interactive: false);

        var result = app.Run("asp", File);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("요청문(REQUEST)이 필요합니다", result.Output);
        Assert.Equal(0, runner.Calls);
    }
}
