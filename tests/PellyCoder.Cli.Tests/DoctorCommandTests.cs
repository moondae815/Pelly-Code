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
            Task.FromResult<IReadOnlyList<DoctorCheck>>([new DoctorCheck("Ollama 서버", DoctorStatus.Ok, "last-row-detail", "SECRETACTION",
                new Dictionary<string, string> { ["ollamaVersion"] = "0.9.1", ["latencyMs"] = "7" })]);
    }

    private sealed class CountingDoctor : IDoctorRunner
    {
        public int Calls { get; private set; }

        public Task<IReadOnlyList<DoctorCheck>> RunAsync(PellyOptions options, DoctorContext context, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<DoctorCheck>>([]);
        }
    }

    private static CommandAppTester Build() => Build(out _);

    private static CommandAppTester Build(out MockFileSystem fs) => Build(fs = new MockFileSystem());

    private static CommandAppTester Build(MockFileSystem fs)
    {
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

    [Fact]
    public void Command_Sanitize_WritesTwoFiles()
    {
        var app = Build(out var fs);
        var result = app.Run("doctor", "--sanitize", "--out", "/out");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("last-row-detail", result.Output);
        var files = fs.Directory.GetFiles("/out").Order(StringComparer.Ordinal).ToList();
        Assert.Equal(2, files.Count);
        Assert.Matches(@"pelly-doctor-\d{8}-\d{6}\.json$", files[0]);
        Assert.Matches(@"pelly-doctor-\d{8}-\d{6}\.md$", files[1]);
        foreach (var f in files)
        {
            var text = fs.File.ReadAllText(f);
            Assert.DoesNotContain("last-row-detail", text);
            Assert.DoesNotContain("SECRETACTION", text);
            Assert.Contains("0.9.1", text);
        }
    }

    [Fact]
    public void Sanitize_InvalidOutPath_Exit4()
    {
        var result = Build().Run("doctor", "--sanitize", "--out", "bad\0path");
        Assert.Equal(4, CliApp.NormalizeExitCode(result.ExitCode));
        Assert.Contains("저장할 수 없습니다", result.Output);
    }

    [Fact]
    public void Out_WithoutSanitize_IsRejected()
    {
        var result = Build().Run("doctor", "--out", "/out");
        Assert.Equal(4, CliApp.NormalizeExitCode(result.ExitCode));
        Assert.Contains("--out은 --sanitize와 함께 쓰세요", result.Output);
    }

    [Fact]
    public void Sanitize_SameSecond_DoesNotOverwrite()
    {
        var app = Build(out var fs);
        Assert.Equal(0, app.Run("doctor", "--sanitize", "--out", "/out").ExitCode);
        var second = Build(fs);
        Assert.Equal(0, second.Run("doctor", "--sanitize", "--out", "/out").ExitCode);
        Assert.Equal(4, fs.Directory.GetFiles("/out").Length);
    }

    [Fact]
    public void Command_OfflineSelfTest_Exit0_NoBackendCreated()
    {
        var fs = new MockFileSystem();
        fs.Directory.SetCurrentDirectory("/");
        var doctor = new CountingDoctor();
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fs);
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton<IUserInteraction>(new FakeUi(false));
        services.AddSingleton<IDoctorRunner>(doctor);
        services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
        services.AddSingleton(typeof(CancellationToken), CancellationToken.None);
        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);

        var result = app.Run("doctor", "--offline-selftest");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(0, doctor.Calls);
        foreach (var name in new[] { "토크나이저", "Roslyn", "ScriptDom", "인코딩", "리소스" })
            Assert.Contains(name, result.Output);
    }

    [Fact]
    public void Help_DoesNotListOfflineSelfTest()
    {
        var result = Build().Run("doctor", "--help");

        Assert.DoesNotContain("offline-selftest", result.Output);
    }
}
