using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Doctor;
using Spectre.Console.Cli.Testing;

namespace PellyCoder.Cli.Tests;

public class DoctorCommandTests
{
    private sealed class FakeDoctor : IDoctorRunner
    {
        public Task<IReadOnlyList<DoctorCheck>> RunAsync(PellyOptions options, DoctorContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DoctorCheck>>([new DoctorCheck("Ollama 서버", DoctorStatus.Ok, "last-row-detail", null)]);
    }

    private static CommandAppTester Build()
    {
        var fs = new MockFileSystem();
        fs.AddDirectory("/w/.git");
        fs.Directory.SetCurrentDirectory("/w");
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fs);
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton<IUserInteraction>(new FakeUi(false));
        services.AddSingleton<IDoctorRunner>(new FakeDoctor());
        services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
        services.AddSingleton(typeof(CancellationToken), CancellationToken.None);
        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);
        return app;
    }

    [Fact]
    public void Command_PrintsChecklistAfterTable()
    {
        var result = Build().Run("doctor");

        Assert.Equal(0, result.ExitCode);
        var lastRow = result.Output.IndexOf("last-row-detail", StringComparison.Ordinal);
        var heading = result.Output.IndexOf("서버 관리자 확인 사항", StringComparison.Ordinal);
        Assert.True(lastRow >= 0 && heading > lastRow, result.Output);
        Assert.Contains("OLLAMA_KV_CACHE_TYPE", result.Output[heading..]);
    }

    [Fact]
    public void Command_MissingProjectFile_Exit4()
    {
        var result = Build().Run("doctor", "--project", "/w/missing.csproj");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("프로젝트 파일을 찾을 수 없습니다", result.Output);
    }
}
