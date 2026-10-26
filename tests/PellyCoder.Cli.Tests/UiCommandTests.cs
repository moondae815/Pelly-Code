using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using Microsoft.Extensions.DependencyInjection;
using PellyCoder.Cli;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Doctor;
using PellyCoder.Core.Fsm;
using PellyCoder.Ui.Session;
using PellyCoder.Ui.Views;
using Spectre.Console.Cli.Testing;

namespace PellyCoder.Cli.Tests;

public sealed class UiCommandTests
{
    private const string CsFile = "/w/src/A.cs";
    private const string SqlFile = "/w/db/orders.sql";

    private sealed class InlineDispatcher : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class FakeRunner(int exitCode) : IRunner
    {
        public RunRequest? Request;

        public Task<RunResult> RunDetailedAsync(RunRequest request, PellyOptions options, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new RunResult(exitCode, "run-fake", request.FilePath, null, null, null));
        }
    }

    /// <summary>Records every <see cref="RunnerFactory"/> call; each returns the same <see cref="FakeRunner"/>.</summary>
    private sealed class FakeRunnerFactory(int exitCode)
    {
        public readonly FakeRunner Runner = new(exitCode);
        public readonly List<IUserInteraction> Uis = [];

        public IRunner Create(IUserInteraction ui)
        {
            Uis.Add(ui);
            return Runner;
        }
    }

    /// <summary>No window: creates the session with an inline dispatcher, then runs the script (default: close at once).</summary>
    private sealed class FakeLauncher(Func<UiSession, int>? script) : IUiLauncher
    {
        public int Calls;
        public UiLaunch? Launch;
        public UiSession? Session;
        public GuiUserInteraction? Ui;

        public int Run(UiLaunch launch, Func<GuiUserInteraction, UiSession> createSession)
        {
            Calls++;
            Launch = launch;
            Ui = new GuiUserInteraction(new InlineDispatcher());
            Session = createSession(Ui);
            return script is null ? Session.ExitCode : script(Session);
        }
    }

    private sealed class FakeDoctor : IDoctorRunner
    {
        public Task<IReadOnlyList<DoctorCheck>> RunAsync(PellyOptions options, DoctorContext context, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<DoctorCheck>>([]);
    }

    private sealed record Harness(CommandAppTester App, FakeLauncher Launcher, FakeRunnerFactory Factory);

    private static Harness Build(Func<UiSession, int>? script = null, int runExit = 0)
    {
        var fs = new MockFileSystem();
        fs.AddFile(CsFile, new MockFileData("class A { void M() {} }"));
        fs.AddFile(SqlFile, new MockFileData("CREATE PROCEDURE dbo.P AS SELECT 1"));
        fs.AddDirectory("/w/.git");

        var factory = new FakeRunnerFactory(runExit);
        var launcher = new FakeLauncher(script);
        var services = new ServiceCollection();
        services.AddSingleton<IFileSystem>(fs);
        services.AddSingleton<OptionsResolver>();
        services.AddSingleton<IRunner>(factory.Runner);
        services.AddSingleton<RunnerFactory>(factory.Create);
        services.AddSingleton<IUiLauncher>(launcher);
        services.AddSingleton<IUserInteraction>(new FakeUi(false));
        services.AddSingleton<IDoctorRunner>(new FakeDoctor());
        services.AddSingleton(new ConfigSources(null, new Dictionary<string, string?>()));
        services.AddSingleton(typeof(CancellationToken), CancellationToken.None);

        var app = new CommandAppTester(new DiTypeRegistrar(services));
        app.Configure(CliApp.Configure);
        return new Harness(app, launcher, factory);
    }

    /// <summary>What the window does with "실행": takes the initial request and runs it to the end.</summary>
    private static int RunInitialRequest(UiSession session)
    {
        session.Request = "요청";
        session.RunAsync().GetAwaiter().GetResult();
        return session.ExitCode;
    }

    [Fact]
    public void UiCs_PassesTargetAndInitialRequest()
    {
        var h = Build();

        var result = h.App.Run("ui", "cs", CsFile, "요청", "--line", "12");

        Assert.Equal(2, result.ExitCode); // closed without a run
        Assert.Equal(new UiLaunch("Pelly — A.cs", "A.cs 12번 줄", "요청", DryRun: false), h.Launcher.Launch);
        Assert.Equal(SessionState.Idle, h.Launcher.Session!.State);
    }

    [Theory]
    [InlineData(new[] { "--method", "Cancel" }, "A.cs · Cancel")]
    [InlineData(new[] { "--class", "OrderService", "--method", "Cancel" }, "A.cs · OrderService.Cancel")]
    [InlineData(new string[0], "A.cs")]
    public void UiCs_TargetLabel(string[] options, string expected)
    {
        var h = Build();

        h.App.Run(["ui", "cs", CsFile, .. options]);

        Assert.Equal(expected, h.Launcher.Launch!.TargetLabel);
        Assert.Null(h.Launcher.Launch.InitialRequest);
    }

    [Theory]
    [InlineData(new[] { "--object", "dbo.P" }, "orders.sql · dbo.P")]
    [InlineData(new[] { "--line", "3" }, "orders.sql 3번 줄")]
    [InlineData(new string[0], "orders.sql")]
    public void UiSql_TargetLabel(string[] options, string expected)
    {
        var h = Build();

        h.App.Run(["ui", "sql", SqlFile, .. options]);

        Assert.Equal("Pelly — orders.sql", h.Launcher.Launch!.Title);
        Assert.Equal(expected, h.Launcher.Launch.TargetLabel);
    }

    [Fact]
    public void UiCs_DryRun_IsPassedToWindow()
    {
        var h = Build(RunInitialRequest);

        h.App.Run("ui", "cs", CsFile, "--dry-run");

        Assert.True(h.Launcher.Launch!.DryRun);
        Assert.True(h.Factory.Runner.Request!.DryRun);
    }

    [Fact]
    public void UiCs_Yes_IsArgumentError()
    {
        var h = Build(RunInitialRequest);

        var result = h.App.Run("ui", "cs", CsFile, "요청", "--yes");

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(SessionState.Invalid, h.Launcher.Session!.State);
        Assert.Contains("`pelly ui`에서는 --yes를 쓸 수 없습니다 (창에서 승인합니다)", h.Launcher.Session.ArgumentErrors);
        Assert.Contains("`pelly ui`에서는 --yes를 쓸 수 없습니다 (창에서 승인합니다)", result.Output);
        Assert.Empty(h.Factory.Uis);
        Assert.Null(h.Factory.Runner.Request);
    }

    [Fact]
    public void UiCs_PauseOnExit_IsArgumentError()
    {
        var h = Build();

        var result = h.App.Run("ui", "cs", CsFile, "--pause-on-exit");

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(["`pelly ui`에서는 --pause-on-exit가 필요 없습니다 (창이 남습니다)"], h.Launcher.Session!.ArgumentErrors);
    }

    [Fact]
    public void UiCs_AllErrorsAreCollected_FileMissingStillLabelled()
    {
        var h = Build();

        var result = h.App.Run("ui", "cs", "/w/src/Missing.cs", "--line", "0", "--yes", "--pause-on-exit");

        Assert.Equal(4, result.ExitCode);
        var errors = h.Launcher.Session!.ArgumentErrors;
        Assert.Equal(
            [
                "--line은 1 이상이어야 합니다",
                "`pelly ui`에서는 --yes를 쓸 수 없습니다 (창에서 승인합니다)",
                "`pelly ui`에서는 --pause-on-exit가 필요 없습니다 (창이 남습니다)",
            ],
            errors.Take(3));
        Assert.StartsWith("파일을 찾을 수 없습니다: ", errors[3], StringComparison.Ordinal); // full path differs per OS
        Assert.Equal(4, errors.Count);
        Assert.Equal("Pelly — Missing.cs", h.Launcher.Launch!.Title);
        Assert.Equal("Missing.cs 0번 줄", h.Launcher.Launch.TargetLabel);
    }

    [Fact]
    public void UiSql_Yes_IsArgumentError()
    {
        var h = Build();

        var result = h.App.Run("ui", "sql", SqlFile, "--yes");

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(SessionState.Invalid, h.Launcher.Session!.State);
    }

    [Fact]
    public void UiSql_TestOption_IsParseError()
    {
        var h = Build();

        var result = h.App.Run("ui", "sql", SqlFile, "--test");

        Assert.Equal(4, result.ExitCode);
        Assert.Equal(0, h.Launcher.Calls);
    }

    [Fact]
    public void UiCs_MissingValue_IsRejectedBeforeSpectre()
    {
        Assert.Equal("--line", CliApp.FindOptionWithoutValue(["ui", "cs", "a.cs", "--line"]));
        Assert.Equal("--object", CliApp.FindOptionWithoutValue(["ui", "sql", "a.sql", "--object", "--dry-run"]));
        Assert.Null(CliApp.FindOptionWithoutValue(["ui", "cs", "a.cs", "--line", "3"]));
    }

    [Fact]
    public void UiCs_RunUsesGuiInteraction()
    {
        var h = Build(RunInitialRequest);

        h.App.Run("ui", "cs", CsFile, "--method", "M", "--max-retries", "1");

        var ui = Assert.Single(h.Factory.Uis);
        Assert.Same(h.Launcher.Ui, ui);
        var request = h.Factory.Runner.Request!;
        Assert.Equal("요청", request.Request);
        Assert.Equal("M", request.MethodName);
        Assert.Equal(1, request.MaxRetries);
        Assert.False(request.Yes);
        Assert.Equal(CsFile, request.FilePath);
    }

    [Fact]
    public void UiSql_RunBuildsSqlRequest()
    {
        var h = Build(RunInitialRequest);

        h.App.Run("ui", "sql", SqlFile, "--object", "dbo.P");

        var request = h.Factory.Runner.Request!;
        Assert.Equal(PellyCoder.Core.Models.EditKind.Sql, request.Kind);
        Assert.Equal("dbo.P", request.ObjectName);
        Assert.Same(h.Launcher.Ui, Assert.Single(h.Factory.Uis));
    }

    [Fact]
    public void UiCs_ExitCodeIsSessionExitCode()
    {
        var h = Build(RunInitialRequest, runExit: ExitCodes.VerifyFailed);

        var result = h.App.Run("ui", "cs", CsFile);

        Assert.Equal(ExitCodes.VerifyFailed, result.ExitCode);
        Assert.Equal(ExitCodes.VerifyFailed, h.Launcher.Session!.ExitCode);
    }
}
