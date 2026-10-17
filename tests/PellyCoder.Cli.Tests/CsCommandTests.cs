using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Cli;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Doctor;
using PellyCoder.Core.Fsm;
using Spectre.Console.Cli;
using Spectre.Console.Cli.Testing;

namespace PellyCoder.Cli.Tests;

public class CsCommandTests
{
    private const string File = "/w/src/A.cs";

    private sealed class FakeRunner(int exitCode) : IRunner
    {
        public RunRequest? Request;
        public PellyOptions? Options;
        public int Calls;

        public Task<int> RunAsync(RunRequest request, PellyOptions options, CancellationToken ct)
        {
            Calls++;
            Request = request;
            Options = options;
            return Task.FromResult(exitCode);
        }
    }

    private sealed class FakeDoctor(params DoctorCheck[] checks) : IDoctorRunner
    {
        public Task<IReadOnlyList<DoctorCheck>> RunAsync(PellyOptions options, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DoctorCheck>>(checks);
    }

    private sealed record Harness(CommandAppTester App, FakeRunner Runner, IFileSystem Fs);

    private static Harness Build(int exitCode = 0, string? workspaceConfig = null, DoctorCheck[]? doctor = null, bool interactive = false)
    {
        var fs = new MockFileSystem();
        fs.AddFile(File, new MockFileData("class A { void M() {} }"));
        fs.AddDirectory("/w/.git");
        if (workspaceConfig is not null) fs.AddFile("/w/pelly.json", new MockFileData(workspaceConfig));

        var runner = new FakeRunner(exitCode);
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fs);
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton<IRunner>(runner);
        services.AddSingleton<IUserInteraction>(new FakeUi(interactive));
        services.AddSingleton<IDoctorRunner>(new FakeDoctor(doctor ?? []));
        services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
        services.AddSingleton(typeof(CancellationToken), CancellationToken.None);

        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);
        return new Harness(app, runner, fs);
    }

    [Fact]
    public void NoMethodNoLine_PassesNullsToRunner()
    {
        var h = Build();

        var result = h.App.Run("cs", File, "do it");

        Assert.Equal(0, result.ExitCode);
        Assert.Null(h.Runner.Request!.MethodName);
        Assert.Null(h.Runner.Request.Line);
    }

    [Fact]
    public void Line_IsPassedToRunner()
    {
        var h = Build();

        h.App.Run("cs", File, "do it", "--line", "12");

        Assert.Equal(12, h.Runner.Request!.Line);
    }

    [Fact]
    public void Line_WithMethod_Exit4_Message()
    {
        var h = Build();

        var result = h.App.Run("cs", File, "do it", "--line", "3", "--method", "M");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("--line은 --class/--method와 함께 쓸 수 없습니다", result.Output);
        Assert.Equal(0, h.Runner.Calls);
    }

    [Fact]
    public void Line_WithClass_Exit4_Message()
    {
        var h = Build();

        var result = h.App.Run("cs", File, "do it", "--line", "3", "--class", "C");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("--line은 --class/--method와 함께 쓸 수 없습니다", result.Output);
    }

    [Fact]
    public void Line_Zero_Exit4()
    {
        var h = Build();

        var result = h.App.Run("cs", File, "do it", "--line", "0");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("--line은 1 이상이어야 합니다", result.Output);
        Assert.Equal(0, h.Runner.Calls);
    }

    [Fact]
    public void MaxRetriesNegative_Exit4()
    {
        var h = Build();

        var result = h.App.Run("cs", File, "do it", "--max-retries", "-1");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("--max-retries는 0 이상이어야 합니다", result.Output);
        Assert.Equal(0, h.Runner.Calls);
    }

    [Fact]
    public void RequestOmitted_Interactive_PromptsAndPasses()
    {
        var h = Build(interactive: true);
        h.App.Console.Input.PushTextWithEnter("add-check");

        var result = h.App.Run("cs", File);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("수정 요청을 입력하세요:", result.Output);
        Assert.Equal("add-check", h.Runner.Request!.Request);
    }

    [Fact]
    public void RequestOmitted_Interactive_EmptyInput_Exit4()
    {
        var h = Build(interactive: true);
        h.App.Console.Input.PushTextWithEnter("");

        var result = h.App.Run("cs", File);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("요청문(REQUEST)이 비어 있습니다", result.Output);
    }

    [Fact]
    public void RequestOmitted_NonInteractive_Exit4_Message()
    {
        var h = Build(interactive: false);

        var result = h.App.Run("cs", File);

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("요청문(REQUEST)이 필요합니다 (비대화형 실행)", result.Output);
        Assert.Equal(0, h.Runner.Calls);
    }

    [Fact]
    public void StrictBaseline_And_WarnAsError_OverrideOptions()
    {
        var h = Build();

        h.App.Run("cs", File, "x", "--strict-baseline", "--warn-as-error");

        Assert.True(h.Runner.Options!.Build.StrictBaseline);
        Assert.True(h.Runner.Options.Build.WarnAsError);
    }

    [Fact]
    public void BuildFlags_AbsentKeepConfig()
    {
        var h = Build(workspaceConfig: """{ "build": { "warnAsError": true } }""");

        h.App.Run("cs", File, "x");

        Assert.True(h.Runner.Options!.Build.WarnAsError);
        Assert.False(h.Runner.Options.Build.StrictBaseline);
    }

    [Fact]
    public void Cs_EmptyRequest_Exit4()
    {
        var h = Build();

        var result = h.App.Run("cs", File, "   ", "--method", "M");

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, h.Runner.Calls);
    }

    [Fact]
    public void Cs_MissingFile_Exit4_NamesPath()
    {
        var h = Build();

        var result = h.App.Run("cs", "/w/src/Nope.cs", "do it", "--method", "M");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("Nope.cs", result.Output);
    }

    [Fact]
    public void Cs_PassesOptionsToRunner()
    {
        var h = Build();

        var result = h.App.Run("cs", File, "add null check", "--yes", "--dry-run", "--max-retries", "2", "--class", "C", "--method", "M");

        Assert.Equal(0, result.ExitCode);
        var r = h.Runner.Request!;
        // The command normalizes through the same file system, so a drive letter on Windows is expected too.
        Assert.Equal(h.Fs.Path.GetFullPath(File), r.FilePath);
        Assert.Equal("add null check", r.Request);
        Assert.Equal("C", r.ClassName);
        Assert.Equal("M", r.MethodName);
        Assert.True(r.Yes);
        Assert.True(r.DryRun);
        Assert.Equal(2, r.MaxRetries);
    }

    [Fact]
    public void Cs_Defaults_AreNotYesNotDryRunNullRetries()
    {
        var h = Build();

        h.App.Run("cs", File, "x", "--method", "M");

        var r = h.Runner.Request!;
        Assert.False(r.Yes);
        Assert.False(r.DryRun);
        Assert.Null(r.MaxRetries);
        Assert.Null(r.ClassName);
    }

    [Fact]
    public void Cs_ThinkOption_OverridesConfig()
    {
        var h = Build(workspaceConfig: """{ "llm": { "thinkMode": "off" } }""");

        h.App.Run("cs", File, "x", "--method", "M", "--think", "always", "--model", "m:1", "--endpoint", "http://h:1");

        Assert.Equal(ThinkMode.Always, h.Runner.Options!.Llm.ThinkMode);
        Assert.Equal("m:1", h.Runner.Options.Llm.Model);
        Assert.Equal("http://h:1", h.Runner.Options.Llm.Endpoint);
    }

    [Fact]
    public void Cs_WorkspaceConfig_IsRead()
    {
        var h = Build(workspaceConfig: """{ "llm": { "model": "from-file" } }""");

        h.App.Run("cs", File, "x", "--method", "M");

        Assert.Equal("from-file", h.Runner.Options!.Llm.Model);
    }

    [Fact]
    public void Cs_InvalidConfig_Exit4_ListsKeys()
    {
        var h = Build(workspaceConfig: """{ "llm": { "bogusKey": 1 } }""");

        var result = h.App.Run("cs", File, "x", "--method", "M");

        Assert.Equal(4, result.ExitCode);
        Assert.Contains("llm:bogusKey", result.Output);
        Assert.Equal(0, h.Runner.Calls);
    }

    [Fact]
    public void Cs_ReturnsRunnerExitCode()
    {
        var h = Build(exitCode: 3);

        var result = h.App.Run("cs", File, "x", "--method", "M");

        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public void Version_StillWorks()
    {
        var h = Build();

        var result = h.App.Run("--version");

        Assert.Contains("0.1.0", result.Output);
    }

    [Theory]
    [InlineData(DoctorStatus.Fail, 4)]
    [InlineData(DoctorStatus.Warn, 0)]
    [InlineData(DoctorStatus.Ok, 0)]
    public void Doctor_ExitCodeFollowsFail(DoctorStatus status, int expected)
    {
        var h = Build(doctor: [new DoctorCheck("Ollama 서버", status, "detail-x", "action-y")]);

        var result = h.App.Run("doctor");

        Assert.Equal(expected, result.ExitCode);
        Assert.Contains("Ollama 서버", result.Output);
        Assert.Contains("detail-x", result.Output);
    }
}
