using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Cli;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Doctor;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using Spectre.Console.Cli.Testing;

namespace PellyCoder.Cli.Tests;

public class SqlCommandTests
{
    private const string File = "/w/db/Proc.sql";

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

    private sealed record Harness(CommandAppTester App, FakeRunner Runner);

    private static Harness Build(bool interactive = false)
    {
        var fs = new MockFileSystem();
        fs.AddFile(File, new MockFileData("CREATE PROCEDURE dbo.P AS SELECT 1"));
        fs.AddDirectory("/w/.git");
        var runner = new FakeRunner();
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fs);
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton<IRunner>(runner);
        services.AddSingleton<IUserInteraction>(new FakeUi(interactive));
        services.AddSingleton<IDoctorRunner>(new FakeDoctor());
        services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
        services.AddSingleton(typeof(CancellationToken), CancellationToken.None);
        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);
        return new Harness(app, runner);
    }

    [Fact]
    public void Bare_sql_with_file_still_runs_edit()
    {
        var h = Build();

        var result = h.App.Run("sql", File, "요청", "--dry-run");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, h.Runner.Calls);
        Assert.Equal(EditKind.Sql, h.Runner.Request!.Kind);
        Assert.Equal("요청", h.Runner.Request.Request);
        Assert.True(h.Runner.Request.DryRun);
    }

    [Fact]
    public void Sql_pull_routes_to_pull_command()
    {
        var h = Build();

        var result = h.App.Run("sql", "pull", "dbo.X", "--from", "prod", "--out", "/w/out", "--force");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("아직 구현되지 않았습니다", result.Output);
        Assert.Equal(0, h.Runner.Calls);
    }

    [Fact]
    public void Sql_check_routes_to_check_command()
    {
        var h = Build();

        var result = h.App.Run("sql", "check", File, "--object", "dbo.P");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("아직 구현되지 않았습니다", result.Output);
        Assert.Equal(0, h.Runner.Calls);
    }

    [Fact]
    public void Sql_pull_out_without_value_is_rejected()
    {
        var console = new Spectre.Console.Testing.TestConsole();

        var code = CliApp.RejectOptionWithoutValue(["sql", "pull", "dbo.X", "--out"], console);

        Assert.Equal(4, code);
        Assert.Contains("--out 옵션에 값이 없습니다.", console.Output);
        Assert.Equal("--object", CliApp.FindOptionWithoutValue(["sql", "check", "a.sql", "--object"]));
        Assert.Equal("--object", CliApp.FindOptionWithoutValue(["sql", "a.sql", "요청", "--object"]));
        Assert.Null(CliApp.FindOptionWithoutValue(["sql", "a.sql", "요청", "--object", "dbo.P"]));
    }

    [Fact]
    public void AcceptsPauseOnExitAndVerbose()
    {
        var h = Build();

        var result = h.App.Run("sql", File, "do it", "--pause-on-exit", "--verbose");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, h.Runner.Calls);
    }

    [Fact]
    public void Sql_PassesKindObjectAndLine()
    {
        var h = Build();

        var r1 = h.App.Run("sql", File, "do it", "--object", "dbo.P", "--yes", "--dry-run", "--max-retries", "2");
        Assert.Equal(0, r1.ExitCode);
        var req = h.Runner.Request!;
        Assert.Equal(EditKind.Sql, req.Kind);
        Assert.Equal("dbo.P", req.ObjectName);
        Assert.Null(req.ClassName);
        Assert.Null(req.MethodName);
        Assert.True(req.Yes);
        Assert.True(req.DryRun);
        Assert.Equal(2, req.MaxRetries);

        h.App.Run("sql", File, "do it", "--line", "7");
        Assert.Equal(7, h.Runner.Request!.Line);
        Assert.Null(h.Runner.Request.ObjectName);
    }

    [Fact]
    public void LineWithObject_Exit4()
    {
        var h = Build();

        var result = h.App.Run("sql", File, "do it", "--line", "3", "--object", "dbo.P");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("--line은 --object와 함께 쓸 수 없습니다", result.Output);
        Assert.Equal(0, h.Runner.Calls);
    }

    [Fact]
    public void MissingRequest_NonInteractive_Exit4()
    {
        var h = Build(interactive: false);

        var result = h.App.Run("sql", File);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("요청문(REQUEST)이 필요합니다", result.Output);
        Assert.Equal(0, h.Runner.Calls);
    }

    [Fact]
    public void UnknownFile_Exit4()
    {
        var h = Build();

        var result = h.App.Run("sql", "/w/db/Nope.sql", "do it");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("Nope.sql", result.Output);
        Assert.Equal(0, h.Runner.Calls);
    }

    [Fact]
    public void Help_ListsSqlCommand()
    {
        var h = Build();

        var result = h.App.Run("--help");

        Assert.Contains("sql", result.Output);
        // The help table wraps long descriptions, so compare with whitespace collapsed (Spectre also drops the trailing period).
        Assert.Contains("SQL저장프로시저·함수·뷰하나를요청대로수정합니다",
            System.Text.RegularExpressions.Regex.Replace(result.Output, @"\s+", ""), StringComparison.Ordinal);
    }
}
