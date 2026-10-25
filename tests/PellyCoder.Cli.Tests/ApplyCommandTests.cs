using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using Spectre.Console;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;

namespace PellyCoder.Cli.Tests;

public class ApplyCommandTests
{
    private const string Spec = "/w/changes/x.md";

    private sealed class FakeRunner(int exitCode = 0) : IRunner
    {
        public readonly List<RunRequest> Requests = [];
        public readonly List<PellyOptions> Options = [];

        public Task<RunResult> RunDetailedAsync(RunRequest request, PellyOptions options, CancellationToken ct)
        {
            Requests.Add(request);
            Options.Add(options);
            return Task.FromResult(new RunResult(exitCode, "run-fake", request.FilePath, null, null, null));
        }
    }

    private sealed class CancellingRunner(CancellationTokenSource cts) : IRunner
    {
        public Task<RunResult> RunDetailedAsync(RunRequest request, PellyOptions options, CancellationToken ct)
        {
            cts.Cancel();
            ct.ThrowIfCancellationRequested();
            throw new InvalidOperationException("unreachable");
        }
    }

    private sealed record Harness(CommandAppTester App, FakeRunner Runner, MockFileSystem Fs, TestConsole Console);

    private static CommandAppTester Wire(MockFileSystem fs, IRunner runner, TestConsole console, CancellationToken ct, bool interactive = false)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fs);
        services.AddSingleton<IAnsiConsole>(console);
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IUserInteraction>(new FakeUi(interactive));
        services.AddSingleton(runner);
        services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
        services.AddSingleton<IProcessRunner>(new NoProcessRunner()); // apply builds groups only; these specs have none
        services.AddSingleton(typeof(CancellationToken), ct);
        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);
        return app;
    }

    private static MockFileSystem NewFs()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/w/.git");
        fs.AddFile("/w/db/a.sql", new MockFileData("CREATE PROCEDURE dbo.P AS SELECT 1;"));
        fs.AddFile(Spec, new MockFileData(
            "---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n- AC1: 하나\n\n## 작업\n- [ ] T1 sql db/a.sql 바꾼다 (AC1)\n"));
        fs.Directory.SetCurrentDirectory("/w");
        return fs;
    }

    private static Harness Build(int exitCode = 0, bool interactive = false)
    {
        var fs = NewFs();
        var runner = new FakeRunner(exitCode);
        var console = new TestConsole().Width(200);
        return new Harness(Wire(fs, runner, console, CancellationToken.None, interactive), runner, fs, console);
    }

    [Fact]
    public void Apply_passes_options_to_runner()
    {
        var h = Build(interactive: true); // --review-at-end needs a console

        var result = h.App.Run("apply", Spec, "--continue", "--review-at-end", "--yes", "--max-retries", "2", "--verbose");

        Assert.True(result.ExitCode is 0 or 1 or 2, $"exit {result.ExitCode}: {result.Output}");
        var request = Assert.Single(h.Runner.Requests);
        Assert.True(request.Yes);
        Assert.False(request.DryRun);
        Assert.Equal(2, request.MaxRetries);
        Assert.True(request.Verbose);
        Assert.NotNull(request.Host);
        Assert.EndsWith("a.sql", request.FilePath);
    }

    [Fact]
    public void Review_at_end_without_a_console_exits_4_even_with_yes()
    {
        var h = Build();

        var result = h.App.Run("apply", Spec, "--review-at-end", "--yes");

        Assert.Equal(4, result.ExitCode);
        Assert.Empty(h.Runner.Requests);
        Assert.Contains("status: draft", h.Fs.File.ReadAllText(Spec));
    }

    [Fact]
    public void Missing_spec_file_exits_4()
    {
        var h = Build();

        var result = h.App.Run("apply", "/w/changes/none.md", "--yes");

        Assert.Equal(4, result.ExitCode);
        Assert.Empty(h.Runner.Requests);
        Assert.Contains("none.md", result.Output);
    }

    [Fact]
    public void Spec_with_errors_exits_4_without_running()
    {
        var h = Build();
        h.Fs.File.WriteAllText(Spec, "no front matter\n");

        Assert.Equal(4, h.App.Run("apply", Spec, "--yes").ExitCode);
        Assert.Empty(h.Runner.Requests);
    }

    [Fact]
    public void Negative_max_retries_exits_4()
    {
        Assert.Equal(4, Build().App.Run("apply", Spec, "--yes", "--max-retries", "-1").ExitCode);
    }

    [Fact]
    public void Cancellation_during_apply_exits_2()
    {
        var fs = NewFs();
        using var cts = new CancellationTokenSource();
        var console = new TestConsole().Width(200);
        var app = Wire(fs, new CancellingRunner(cts), console, cts.Token);

        var result = app.Run("apply", Spec, "--yes");

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Exit_codes_are_within_0_to_5()
    {
        foreach (var code in new[] { 0, 1, 2, 3, 5 })
        {
            var result = Build(code).App.Run("apply", Spec, "--yes");
            Assert.InRange(result.ExitCode, 0, 5);
        }
        Assert.InRange(Build().App.Run("apply").ExitCode, 0, 5);
        Assert.Equal(4, Build().App.Run("apply").ExitCode);
    }
}
