using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Cli;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Doctor;
using PellyCoder.Core.Fsm;
using Spectre.Console.Cli.Testing;
using Spectre.Console.Testing;

namespace PellyCoder.Cli.Tests;

/// <summary>Every way the CLI ends maps to an exit code in 0–5 (PRD FR-00): parse errors are 4, cancellation is 2.</summary>
public class ExitCodeMappingTests
{
    private const string File = "/w/src/A.cs";

    private sealed class CancellingRunner : IRunner
    {
        public Task<int> RunAsync(RunRequest request, PellyOptions options, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult(0);
        }
    }

    private sealed class CancellingDoctor : IDoctorRunner
    {
        public Task<IReadOnlyList<DoctorCheck>> RunAsync(PellyOptions options, DoctorContext context, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<DoctorCheck>>([]);
        }
    }

    private sealed class StrayCancelRunner : IRunner
    {
        public Task<int> RunAsync(RunRequest request, PellyOptions options, CancellationToken ct) =>
            throw new TaskCanceledException("http timeout");
    }

    private static CommandAppTester Build(CancellationToken ct, IRunner? runner = null)
    {
        var fs = new MockFileSystem();
        fs.AddFile(File, new MockFileData("class A { void M() {} }"));
        fs.AddFile("/w/src/A.sql", new MockFileData("CREATE PROCEDURE dbo.P AS SELECT 1;"));
        fs.AddFile("/w/src/A.csproj", new MockFileData("<Project Sdk=\"Microsoft.NET.Sdk\" />"));
        fs.AddFile("/w/pelly.json", new MockFileData("{}"));
        fs.AddDirectory("/w/.git");
        fs.Directory.SetCurrentDirectory("/w");

        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fs);
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton<IUserInteraction>(new FakeUi(false));
        services.AddSingleton<IRunner>(runner ?? new CancellingRunner());
        services.AddSingleton<IDoctorRunner>(new CancellingDoctor());
        services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
        services.AddSingleton(typeof(CancellationToken), ct);

        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);
        return app;
    }

    private static CancellationToken Cancelled() => new(canceled: true);

    [Fact]
    public void MissingFileArgument_Exit4()
    {
        var result = Build(CancellationToken.None).Run("cs");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("FILE", result.Output);
    }

    [Fact]
    public void NonNumericMaxRetries_Exit4()
    {
        var result = Build(CancellationToken.None).Run("cs", File, "do it", "--method", "M", "--max-retries", "abc");

        Assert.Equal(4, result.ExitCode);
    }

    [Fact]
    public void UnknownCommand_Exit4()
    {
        var result = Build(CancellationToken.None).Run("frobnicate");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("frobnicate", result.Output);
    }

    [Fact]
    public void Doctor_CancelledToken_Exit2()
    {
        var result = Build(Cancelled()).Run("doctor");

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Cs_CancelledToken_Exit2()
    {
        var result = Build(Cancelled()).Run("cs", File, "do it", "--method", "M");

        Assert.Equal(2, result.ExitCode);
    }

    [Fact]
    public void Cli_UnexpectedOperationCanceled_Exit4()
    {
        var result = Build(CancellationToken.None, new StrayCancelRunner()).Run("cs", File, "do it", "--method", "M");

        Assert.Equal(4, result.ExitCode);
        Assert.DoesNotContain("사용자가 실행을 중단했습니다", result.Output);
    }

    /// <summary>A typo such as <c>--dry-rum</c> must not be ignored: it would turn a preview into a real write.</summary>
    [Theory]
    [InlineData("cs", File, "do it", "--method", "M", "--dry-rum")]
    [InlineData("sql", "/w/src/A.sql", "do it", "--dry-rum")]
    [InlineData("doctor", "--sanitze")]
    [InlineData("report", "--sinse", "2026-10-01")]
    public void UnknownOption_Exit4_NamesTheOption(params string[] args)
    {
        var result = Build(CancellationToken.None).Run(args);

        Assert.Equal(4, result.ExitCode);
        var bad = args.Single(a => a.StartsWith("--", StringComparison.Ordinal) && a is not "--method");
        Assert.Contains($"Unknown option '{bad[2..]}'", result.Output);
    }

    [Theory]
    [InlineData("cs", File, "do it", "--method", "M", "--pause-on-exit")]
    [InlineData("sql", "/w/src/A.sql", "do it", "--pause-on-exit")]
    [InlineData("doctor", "--pause-on-exit")]
    [InlineData("report", "--pause-on-exit")]
    public void PauseOnExit_AcceptedByEveryCommand(params string[] args)
    {
        // The cancelled token ends cs/sql/doctor with 2 right after parsing; report has no cancellable work and ends with 0.
        var result = Build(Cancelled()).Run(args);

        Assert.True(result.ExitCode is 0 or 2, $"exit {result.ExitCode}: {result.Output}");
    }

    [Fact]
    public void Doctor_HiddenOfflineSelftest_StillParses()
    {
        var result = Build(Cancelled()).Run("doctor", "--offline-selftest");

        // On failure the output names the self-test row that failed (a Windows-only flake must be diagnosable from the log).
        Assert.True(result.ExitCode != 4, $"exit {result.ExitCode}: {result.Output}");
    }

    [Fact]
    public void ArgumentsAfterDoubleDash_AreNotOptions()
    {
        var result = Build(Cancelled()).Run("cs", File, "do it", "--method", "M", "--", "--whatever");

        Assert.Equal(2, result.ExitCode);
    }

    /// <summary>
    /// A value option written without its value is an argument error (exit 4) naming the option: under strict parsing
    /// Spectre otherwise fills it with its internal "__default_command" token, so <c>report --sanitize --out</c> wrote
    /// to <c>./__default_command/</c> and <c>sql --object</c> preflighted an object of that name.
    /// </summary>
    [Theory]
    [InlineData("--out", "report", "--sanitize", "--out")]
    [InlineData("--out", "doctor", "--sanitize", "--out")]
    [InlineData("--since", "report", "--since")]
    [InlineData("--project", "doctor", "--project")]
    [InlineData("--object", "sql", "/w/src/A.sql", "do it", "--object")]
    [InlineData("--line", "sql", "/w/src/A.sql", "do it", "--line")]
    [InlineData("--line", "cs", File, "do it", "--line")]
    [InlineData("--method", "cs", File, "do it", "--method")]
    [InlineData("--class", "cs", File, "do it", "--method", "M", "--class")]
    [InlineData("--max-retries", "cs", File, "do it", "--method", "M", "--max-retries")]
    [InlineData("--config", "cs", File, "do it", "--method", "M", "--config")]
    [InlineData("--model", "cs", File, "do it", "--method", "M", "--model")]
    [InlineData("--out", "report", "--out", "--sanitize")]
    [InlineData("--object", "sql", "/w/src/A.sql", "do it", "--object", "--dry-run")]
    public void ValueOptionWithoutValue_Exit4_NamesTheOption(string option, params string[] args)
    {
        var console = new TestConsole();

        var exit = CliApp.RejectOptionWithoutValue(args, console);

        Assert.Equal(4, exit);
        Assert.Contains($"{option} 옵션에 값이 없습니다", console.Output);
        Assert.Equal(option, CliApp.FindOptionWithoutValue(args));
    }

    [Theory]
    [InlineData("report", "--sanitize", "--out=/w/export")]
    [InlineData("cs", File, "do it", "--method", "M", "--", "--line")]
    [InlineData("frobnicate", "--out")]
    [InlineData("cs", File, "do it", "--method", "M", "--dry-run")]
    public void ValueOptionCheck_LeavesOtherLinesToSpectre(params string[] args) =>
        Assert.Null(CliApp.FindOptionWithoutValue(args));

    /// <summary>Program.cs runs the check before Spectre, so every command in <see cref="CliApp.Configure"/> must be in the map.</summary>
    [Fact]
    public void ValueOptionCheck_KnowsEveryCommand()
    {
        foreach (var command in new[] { "cs", "sql", "doctor", "report" })
        {
            var help = Build(CancellationToken.None).Run(command, "--help");
            Assert.Equal(0, help.ExitCode);
        }
        Assert.Equal(["cs", "doctor", "report", "sql"], CliApp.CommandSettings.Keys.Order().ToArray());
    }

    /// <summary>Command lines from README and deploy/*.md still parse under strict parsing (cancelled token: no real work runs).</summary>
    [Theory]
    [InlineData("cs", File, "null 검사 추가", "--line", "1", "--pause-on-exit")]
    [InlineData("cs", File, "do it", "--method", "M", "--test", "--verbose")]
    [InlineData("cs", File, "do it", "--line", "1", "--dry-run")]
    [InlineData("cs", File, "do it", "--class", "A", "--method", "M", "--yes", "--max-retries", "1", "--think", "off", "--model", "m", "--endpoint", "http://localhost:11434", "--config", "/w/pelly.json")]
    [InlineData("cs", File, "do it", "--method", "M", "--strict-baseline", "--warn-as-error")]
    [InlineData("sql", "/w/src/A.sql", "do it", "--object", "dbo.P")]
    [InlineData("sql", "/w/src/A.sql", "do it", "--line", "1", "--yes", "--dry-run")]
    [InlineData("doctor", "--sanitize", "--out", "/w/export")]
    [InlineData("doctor", "--project", "/w/src/A.csproj", "--config", "/w/pelly.json", "--model", "m", "--endpoint", "http://localhost:11434")]
    [InlineData("report", "--since", "2026-10-01", "--sanitize", "--out", "/w/export")]
    public void DocumentedCommandLines_Parse(params string[] args)
    {
        Assert.Null(CliApp.FindOptionWithoutValue(args));
        var result = Build(Cancelled()).Run(args);

        Assert.True(result.ExitCode != 4, $"exit 4: {result.Output}");
    }
}
