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

public class EditCommandTests
{
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

    private static (CommandAppTester App, FakeRunner Runner) Build(string file, string? pellyJson = null)
    {
        var fs = new MockFileSystem();
        fs.AddFile(file, new MockFileData("x"));
        fs.AddDirectory("/w/.git");
        if (pellyJson is not null) fs.AddFile("/w/pelly.json", new MockFileData(pellyJson));
        var runner = new FakeRunner();
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fs);
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton<IRunner>(runner);
        services.AddSingleton<IUserInteraction>(new FakeUi(false));
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

    [Fact]
    public void Edit_CsFile_RunsAsCs()
    {
        var (app, runner) = Build("/w/src/OrderService.cs");

        var result = app.Run("edit", "/w/src/OrderService.cs", "요청", "--method", "Cancel", "--test");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.CSharp, runner.Request!.Kind);
        Assert.Equal("Cancel", runner.Request.MethodName);
        Assert.True(runner.Request.RunTests);
        Assert.Contains("pelly cs", result.Output);
    }

    [Fact]
    public void Edit_SqlFile_RunsAsSql()
    {
        var (app, runner) = Build("/w/db/usp_X.sql");

        var result = app.Run("edit", "/w/db/usp_X.sql", "요청", "--object", "dbo.usp_X");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.Sql, runner.Request!.Kind);
        Assert.Equal("dbo.usp_X", runner.Request.ObjectName);
        Assert.Contains("pelly sql", result.Output);
    }

    [Theory]
    [InlineData("/w/site/list.asp")]
    [InlineData("/w/site/inc/func.inc")]
    [InlineData("/w/site/LIST.ASP")]
    public void Edit_AspFile_RunsAsAsp(string file)
    {
        var (app, runner) = Build(file);

        var result = app.Run("edit", file, "요청", "--lines", "3-9");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.Asp, runner.Request!.Kind);
        Assert.Equal(new TextSpanInfo(3, 9), runner.Request.Lines);
        Assert.Contains("pelly asp", result.Output);
    }

    [Fact]
    public void Edit_UpperCaseCsExtension_RunsAsCs()
    {
        var (app, runner) = Build("/w/src/A.CS");

        var result = app.Run("edit", "/w/src/A.CS", "요청", "--line", "4");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.CSharp, runner.Request!.Kind);
        Assert.Equal(4, runner.Request.Line);
    }

    [Fact]
    public void Edit_ExtensionFromAspConfig_RunsAsAsp()
    {
        var (app, runner) = Build("/w/site/page.html", """{ "asp": { "extensions": [".asp", ".html"] } }""");

        var result = app.Run("edit", "/w/site/page.html", "요청", "--lines", "1-2");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(EditKind.Asp, runner.Request!.Kind);
    }

    [Fact]
    public void Edit_UnknownExtension_Exit4()
    {
        var (app, runner) = Build("/w/site/readme.txt");

        var result = app.Run("edit", "/w/site/readme.txt", "요청");

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, runner.Calls);
        Assert.Contains("지원하지 않는 파일 형식입니다: .txt", result.Output);
    }

    [Theory]
    [InlineData("/w/db/usp_X.sql", "--method", "M", "--method", ".sql")]
    [InlineData("/w/db/usp_X.sql", "--test", null, "--test", ".sql")]
    [InlineData("/w/src/A.cs", "--lines", "1-3", "--lines", ".cs")]
    [InlineData("/w/src/A.cs", "--object", "dbo.X", "--object", ".cs")]
    [InlineData("/w/site/a.asp", "--class", "C", "--class", ".asp")]
    [InlineData("/w/site/a.asp", "--strict-baseline", null, "--strict-baseline", ".asp")]
    public void Edit_OptionForAnotherLanguage_Exit4(string file, string option, string? value, string named, string ext)
    {
        var (app, runner) = Build(file);
        string[] args = value is null ? ["edit", file, "요청", option] : ["edit", file, "요청", option, value];

        var result = app.Run(args);

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, runner.Calls);
        Assert.Contains($"{named}은(는) {ext} 파일에 쓸 수 없습니다", result.Output);
    }

    [Fact]
    public void Edit_SharedOptions_PassedThrough()
    {
        var (app, runner) = Build("/w/site/a.asp");

        var result = app.Run("edit", "/w/site/a.asp", "요청", "--proc", "P", "--yes", "--dry-run", "--max-retries", "2", "--verbose");

        Assert.Equal(0, result.ExitCode);
        var request = runner.Request!;
        Assert.Equal("P", request.MethodName);
        Assert.True(request.Yes);
        Assert.True(request.DryRun);
        Assert.True(request.Verbose);
        Assert.Equal(2, request.MaxRetries);
    }

    [Fact]
    public void Edit_TargetCommandValidationStillApplies()
    {
        var (app, runner) = Build("/w/site/a.asp");

        var result = app.Run("edit", "/w/site/a.asp", "요청", "--lines", "9-3");

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, runner.Calls);
        Assert.Contains("--lines는 A-B 형식입니다", result.Output);
    }
}
