using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Tests.Testing;

public class TestRunnerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pelly-testrunner-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly ScriptedProcessRunner _procs = new();
    private static readonly BuildTool Dotnet = new(BuildToolKind.Dotnet, "dotnet");
    private static readonly BuildTool MsBuild = new(BuildToolKind.MsBuild, @"C:\msbuild\MSBuild.exe");

    public TestRunnerTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private static ProcessResult Ok(string stdout = "", int exit = 0) =>
        new(exit, stdout, "", System.Text.Encoding.UTF8.GetBytes(stdout), TimeSpan.FromMilliseconds(5), false);

    private static ProcessResult TimedOut() => new(-1, "", "", [], TimeSpan.FromSeconds(1), true);

    private string Proj => Path.Combine(_dir, "T.Tests", "T.Tests.csproj");
    private string Trx => Path.Combine(_dir, ".pelly", "test.trx");

    private TestRunner Runner(TestOptions? test = null) => new(_procs, new BuildOptions(), test ?? new TestOptions());

    [Fact]
    public async Task Runner_Sdk_CommandLinesAndTrxPath()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Proj)!);
        File.WriteAllText(Proj, "<Project Sdk=\"Microsoft.NET.Sdk\" />"); // plain VSTest-style project, no Testing Platform signal
        _procs.Enqueue(Ok());
        _procs.Enqueue(Ok(exit: 1), () => File.WriteAllText(Trx, TrxParserTests.Fixture("modern-fail.trx")));

        var run = await Runner().RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.Equal(["build", Proj, "--no-restore", "--nologo", "-v", "q", "-clp:NoSummary;ErrorsOnly"], _procs.Calls[0].Arguments);
        Assert.Equal("dotnet", _procs.Calls[1].FileName);
        Assert.Equal(["test", Proj, "--no-build", "--nologo", "--logger", $"trx;LogFileName={Trx}"], _procs.Calls[1].Arguments);
        Assert.Equal("en", _procs.Calls[1].Environment!["DOTNET_CLI_UI_LANGUAGE"]);
        Assert.Equal("1033", _procs.Calls[1].Environment!["VSLANG"]);
        Assert.Equal(TimeSpan.FromSeconds(300), _procs.Calls[1].Timeout);
        Assert.Equal(3, run.Results.Count);
        Assert.True(run.PerTestResults);
        Assert.Equal(1, run.ExitCode);
        Assert.Null(run.Error);
    }

    [Fact]
    public async Task Runner_XunitV3Project_RunsDotnetRunWithResultTrx()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Proj)!);
        File.WriteAllText(Proj, """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit.v3.mtp-v2" Version="4.0.2" /></ItemGroup></Project>""");
        _procs.Enqueue(Ok());
        _procs.Enqueue(Ok(), () => File.WriteAllText(Trx, TrxParserTests.Fixture("modern-pass.trx")));

        var run = await Runner().RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.Equal(["run", "--project", Proj, "--no-build", "--", "-result-trx", Trx], _procs.Calls[1].Arguments);
        Assert.True(run.PerTestResults);
        Assert.Equal(3, run.Results.Count);
    }

    private const string XunitV3Ref = """<ItemGroup><PackageReference Include="xunit.v3.mtp-v2" Version="4.0.2" /></ItemGroup>""";

    [Fact]
    public async Task Runner_Properties_PassedToTheBuild()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Proj)!);
        File.WriteAllText(Proj, $"""<Project Sdk="Microsoft.NET.Sdk">{XunitV3Ref}</Project>""");
        _procs.Enqueue(Ok());
        _procs.Enqueue(Ok(), () => File.WriteAllText(Trx, TrxParserTests.Fixture("modern-pass.trx")));

        var run = await Runner().RunAsync(Dotnet, Proj, _dir, Trx, new Dictionary<string, string> { ["JudgeTargetRoot"] = "/w/Modern" },
            TestContext.Current.CancellationToken);

        Assert.Equal(["build", Proj, "--no-restore", "--nologo", "-v", "q", "-clp:NoSummary;ErrorsOnly", "-p:JudgeTargetRoot=/w/Modern"],
            _procs.Calls[0].Arguments);
        Assert.Equal(["run", "--project", Proj, "--no-build", "--", "-result-trx", Trx], _procs.Calls[1].Arguments);
        Assert.True(run.PerTestResults);
    }

    [Fact]
    public async Task MultiTarget_DotnetRunGetsFirstFramework()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Proj)!);
        File.WriteAllText(Proj, $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks> net10.0 ; net9.0 </TargetFrameworks></PropertyGroup>{XunitV3Ref}</Project>""");
        _procs.Enqueue(Ok());
        _procs.Enqueue(Ok(), () => File.WriteAllText(Trx, TrxParserTests.Fixture("modern-pass.trx")));

        var run = await Runner().RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.Equal(["run", "--project", Proj, "--no-build", "--framework", "net10.0", "--", "-result-trx", Trx], _procs.Calls[1].Arguments);
        Assert.True(run.PerTestResults);
        Assert.Null(run.Error);
    }

    [Fact]
    public async Task MultiTarget_FromDirectoryBuildProps_OtherTestingPlatform()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Proj)!);
        File.WriteAllText(Path.Combine(_dir, "Directory.Build.props"),
            """<Project><PropertyGroup><TargetFrameworks>net9.0;net10.0</TargetFrameworks></PropertyGroup></Project>""");
        File.WriteAllText(Proj, """<Project Sdk="MSTest.Sdk/3.8.0" />""");
        _procs.Enqueue(Ok());
        _procs.Enqueue(Ok());

        await Runner().RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.Equal(["run", "--project", Proj, "--no-build", "--framework", "net9.0"], _procs.Calls[1].Arguments);
    }

    [Fact]
    public async Task MultiTarget_CsprojSingleFrameworkWinsOverProps_NoFrameworkArgument()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Proj)!);
        File.WriteAllText(Path.Combine(_dir, "Directory.Build.props"),
            """<Project><PropertyGroup><TargetFrameworks>net9.0;net10.0</TargetFrameworks></PropertyGroup></Project>""");
        File.WriteAllText(Proj, $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup>{XunitV3Ref}</Project>""");
        _procs.Enqueue(Ok());
        _procs.Enqueue(Ok(), () => File.WriteAllText(Trx, TrxParserTests.Fixture("modern-pass.trx")));

        await Runner().RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.Equal(["run", "--project", Proj, "--no-build", "--", "-result-trx", Trx], _procs.Calls[1].Arguments);
    }

    [Fact]
    public async Task MultiTarget_VsTestPathUnchanged()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Proj)!);
        File.WriteAllText(Proj, """<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net10.0;net48</TargetFrameworks></PropertyGroup></Project>""");
        _procs.Enqueue(Ok());
        _procs.Enqueue(Ok(), () => File.WriteAllText(Trx, TrxParserTests.Fixture("modern-pass.trx")));

        await Runner().RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.Equal(["test", Proj, "--no-build", "--nologo", "--logger", $"trx;LogFileName={Trx}"], _procs.Calls[1].Arguments);
    }

    [Theory]
    // Measured (macOS, .NET 10 only): the host cannot find the first TFM's runtime (exit 150) ...
    [InlineData(150, "You must install or update .NET to run this application.\n\nApp: /w/T/bin/Debug/net9.0/T")]
    // ... and dotnet run cannot launch a .NET Framework TFM without mono.
    [InlineData(1, "Unhandled exception: An error occurred trying to start process 'mono' with working directory '/w'. No such file or directory")]
    public async Task MultiTarget_FirstFrameworkCannotRun_ReportsError(int exit, string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Proj)!);
        File.WriteAllText(Proj, $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net9.0;net10.0</TargetFrameworks></PropertyGroup>{XunitV3Ref}</Project>""");
        _procs.Enqueue(Ok());
        _procs.Enqueue(new ProcessResult(exit, "", output, [], TimeSpan.FromMilliseconds(5), false));

        var run = await Runner().RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.Equal("멀티 타깃 테스트 프로젝트를 실행할 수 없습니다 (net9.0)", run.Error);
        Assert.False(run.ReportMissing);
        Assert.False(run.PerTestResults);
    }

    [Fact]
    public async Task MultiTarget_CrashWithoutLaunchFailure_StaysReportMissing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Proj)!);
        File.WriteAllText(Proj, $"""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFrameworks>net10.0;net9.0</TargetFrameworks></PropertyGroup>{XunitV3Ref}</Project>""");
        _procs.Enqueue(Ok());
        _procs.Enqueue(Ok("Stack overflow.", exit: 134));

        var run = await Runner().RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.True(run.ReportMissing);
        Assert.DoesNotContain("멀티 타깃", run.Error);
    }

    [Theory]
    [InlineData("""<Project Sdk="MSTest.Sdk/3.8.0" />""")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><IsTestingPlatformApplication>true</IsTestingPlatformApplication></PropertyGroup></Project>""")]
    [InlineData("""<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="Microsoft.Testing.Platform" Version="1" /></ItemGroup></Project>""")]
    public async Task Runner_OtherTestingPlatformProject_RunsWithoutReport_ExitCodeOnly(string csproj)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Proj)!);
        File.WriteAllText(Proj, csproj);
        _procs.Enqueue(Ok());
        _procs.Enqueue(Ok(exit: 2));

        var run = await Runner().RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.Equal(["run", "--project", Proj, "--no-build"], _procs.Calls[1].Arguments);
        Assert.False(run.PerTestResults);
        Assert.Equal(2, run.ExitCode);
        Assert.Null(run.Error);
    }

    [Fact]
    public async Task Runner_TestBuildErrors_SkipTestRun()
    {
        _procs.Enqueue(Ok("T.cs(3,5): error CS0103: The name 'x' does not exist [" + Proj + "]", exit: 1));

        var run = await Runner().RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.Single(_procs.Calls);
        Assert.Equal("CS0103", Assert.Single(run.BuildErrors).Code);
        Assert.Empty(run.Results);
    }

    [Fact]
    public async Task Runner_ProcessRanButWroteNoReport_IsReportMissing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Proj)!);
        File.WriteAllText(Proj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        _procs.Enqueue(Ok());
        _procs.Enqueue(Ok(exit: 134)); // the host died: no TRX

        var run = await Runner().RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.True(run.ReportMissing);
        Assert.Equal(134, run.ExitCode);
        Assert.NotNull(run.Error);
        Assert.False(run.PerTestResults);
    }

    [Fact]
    public async Task Runner_CannotStartProcess_IsNotReportMissing()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Proj)!);
        File.WriteAllText(Proj, "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        _procs.Enqueue(Ok());
        _procs.EnqueueThrow(new ProcessStartException("dotnet", new IOException("nf")));

        var run = await Runner().RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.False(run.ReportMissing);
        Assert.NotNull(run.Error);
    }

    [Fact]
    public async Task Runner_Timeout_Reported()
    {
        _procs.Enqueue(Ok());
        _procs.Enqueue(TimedOut());

        var run = await Runner(new TestOptions { TimeoutSeconds = 7 }).RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.True(run.TimedOut);
        Assert.Equal(TimeSpan.FromSeconds(7), _procs.Calls[1].Timeout);
        Assert.Empty(run.Results);
    }

    [Fact]
    public async Task Runner_LegacyPlaceholders_Substituted()
    {
        var projDir = Path.Combine(_dir, "Shop.Tests");
        Directory.CreateDirectory(projDir);
        var proj = Path.Combine(projDir, "Shop.Tests.csproj");
        File.WriteAllText(proj, """<Project xmlns="http://schemas.microsoft.com/developer/msbuild/2003"><PropertyGroup><AssemblyName>Custom.Name</AssemblyName></PropertyGroup></Project>""");
        _procs.Enqueue(Ok());
        _procs.Enqueue(Ok(), () => File.WriteAllText(Trx, TrxParserTests.Fixture("modern-pass.trx")));
        var test = new TestOptions { Command = "\"C:\\Program Files\\runner.exe\" {dll} --proj {project} /logger:trx;LogFileName={trx}" };

        var run = await Runner(test).RunAsync(MsBuild, proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.Equal(@"C:\msbuild\MSBuild.exe", _procs.Calls[0].FileName);
        var call = _procs.Calls[1];
        Assert.Equal(@"C:\Program Files\runner.exe", call.FileName);
        Assert.Equal(
            [Path.Combine(projDir, "bin", "Debug", "Custom.Name.dll"), "--proj", proj, "/logger:trx;LogFileName=" + Trx],
            call.Arguments);
        Assert.True(run.PerTestResults);
        Assert.Equal(3, run.Results.Count);
    }

    [Fact]
    public async Task Runner_LegacyWithoutTrx_PerTestFalse()
    {
        var projDir = Path.Combine(_dir, "Shop.Tests");
        Directory.CreateDirectory(projDir);
        var proj = Path.Combine(projDir, "Shop.Tests.csproj");
        File.WriteAllText(proj, "<Project />");
        _procs.Enqueue(Ok());
        _procs.Enqueue(Ok(exit: 2));

        var run = await Runner(new TestOptions { Command = "nunit3-console {dll}" }).RunAsync(MsBuild, proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.False(run.PerTestResults);
        Assert.Equal(2, run.ExitCode);
        Assert.Empty(run.Results);
        Assert.Null(run.Error);
        Assert.Equal(Path.Combine(projDir, "bin", "Debug", "Shop.Tests.dll"), _procs.Calls[1].Arguments[0]);
    }

    [Fact]
    public async Task Runner_LegacyWithoutCommand_ReportsError()
    {
        var run = await Runner().RunAsync(MsBuild, Proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.Equal("레거시 테스트 프로젝트는 test.command 설정이 필요합니다", run.Error);
        Assert.Empty(_procs.Calls);
    }

    [Fact]
    public async Task Runner_ProcessStartFailure_MapsToError_ForBuildAndTestSteps()
    {
        _procs.EnqueueThrow(new ProcessStartException("dotnet", new IOException()));
        var build = await Runner().RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);
        Assert.Contains("dotnet", build.Error);

        _procs.Enqueue(Ok());
        _procs.EnqueueThrow(new ProcessStartException("dotnet", new IOException()));
        var tests = await Runner().RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);
        Assert.Contains("dotnet", tests.Error);
    }

    [Fact]
    public async Task Runner_MissingOrMalformedTrx_ReportsError()
    {
        _procs.Enqueue(Ok());
        _procs.Enqueue(Ok());
        var missing = await Runner().RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);
        Assert.NotNull(missing.Error);
        Assert.False(missing.PerTestResults);

        _procs.Enqueue(Ok());
        _procs.Enqueue(Ok(), () => File.WriteAllText(Trx, "<TestRun><oops"));
        var bad = await Runner().RunAsync(Dotnet, Proj, _dir, Trx, TestContext.Current.CancellationToken);
        Assert.NotNull(bad.Error);
        Assert.Empty(bad.Results);
    }

    [Fact]
    public async Task Runner_LegacyBlankProgram_ReportsError()
    {
        var run = await Runner(new TestOptions { Command = "\"\" {dll}" }).RunAsync(MsBuild, Proj, _dir, Trx, TestContext.Current.CancellationToken);

        Assert.NotNull(run.Error);
        Assert.Empty(_procs.Calls);
    }
}
