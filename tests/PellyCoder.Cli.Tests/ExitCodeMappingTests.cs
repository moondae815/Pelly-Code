using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Cli;
using PellyCoder.Core.Config;
using PellyCoder.Core.Doctor;
using PellyCoder.Core.Fsm;
using Spectre.Console.Cli.Testing;

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
        public Task<IReadOnlyList<DoctorCheck>> RunAsync(PellyOptions options, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            return Task.FromResult<IReadOnlyList<DoctorCheck>>([]);
        }
    }

    private static CommandAppTester Build(CancellationToken ct)
    {
        var fs = new MockFileSystem();
        fs.AddFile(File, new MockFileData("class A { void M() {} }"));
        fs.AddDirectory("/w/.git");
        fs.Directory.SetCurrentDirectory("/w");

        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fs);
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton<IRunner>(new CancellingRunner());
        services.AddSingleton<IDoctorRunner>(new CancellingDoctor());
        services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
        services.AddSingleton(typeof(CancellationToken), ct);

        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);
        return app;
    }

    private static CancellationToken Cancelled() => new(canceled: true);

    [Fact]
    public void MissingRequestArgument_Exit4()
    {
        var result = Build(CancellationToken.None).Run("cs", File);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("REQUEST", result.Output);
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
}
