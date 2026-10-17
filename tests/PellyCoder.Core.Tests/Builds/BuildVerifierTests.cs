using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Builds;

public sealed class BuildVerifierTests
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "ws");
    private static readonly string Csproj = Path.Combine(Root, "App", "App.csproj");
    private static readonly BuildTool Dotnet = new(BuildToolKind.Dotnet, "dotnet");
    private static readonly BuildTool MsBuild = new(BuildToolKind.MsBuild, @"C:\VS\MSBuild.exe");

    private static ProcessResult Result(string stdout = "", string stderr = "", int exit = 0, bool timedOut = false, byte[]? bytes = null) =>
        new(exit, stdout, stderr, bytes ?? Encoding.UTF8.GetBytes(stdout), TimeSpan.FromSeconds(1), timedOut);

    [Fact]
    public async Task Sdk_RunsDesignCommandWithEnglishEnv()
    {
        var runner = new ScriptedProcessRunner();
        runner.Enqueue(Result());

        await new BuildVerifier(runner, new BuildOptions()).BuildAsync(Dotnet, Csproj, Root, TestContext.Current.CancellationToken);

        var call = Assert.Single(runner.Calls);
        Assert.Equal("dotnet", call.FileName);
        Assert.Equal(["build", Csproj, "--no-restore", "--nologo", "-v", "q", "-clp:NoSummary;ErrorsOnly"], call.Arguments);
        Assert.Equal("en", call.Environment!["DOTNET_CLI_UI_LANGUAGE"]);
        Assert.Equal("1033", call.Environment["VSLANG"]);
        Assert.Equal(Path.GetDirectoryName(Csproj), call.WorkingDirectory);
    }

    [Fact]
    public async Task Sdk_WarnAsError_DropsErrorsOnly()
    {
        var runner = new ScriptedProcessRunner();
        runner.Enqueue(Result());

        await new BuildVerifier(runner, new BuildOptions { WarnAsError = true })
            .BuildAsync(Dotnet, Csproj, Root, TestContext.Current.CancellationToken);

        Assert.Equal("-clp:NoSummary", runner.Calls[0].Arguments[^1]);
    }

    [Fact]
    public async Task Legacy_RunsMsBuildCommand()
    {
        var runner = new ScriptedProcessRunner();
        runner.Enqueue(Result());

        await new BuildVerifier(runner, new BuildOptions()).BuildAsync(MsBuild, Csproj, Root, TestContext.Current.CancellationToken);

        var call = runner.Calls[0];
        Assert.Equal(MsBuild.Executable, call.FileName);
        Assert.Equal([Csproj, "/t:Build", "/nologo", "/v:q", "/clp:NoSummary;ErrorsOnly", "/nr:true", "/m"], call.Arguments);
        Assert.Equal("en", call.Environment!["DOTNET_CLI_UI_LANGUAGE"]);
        Assert.Equal("1033", call.Environment["VSLANG"]);
    }

    [Fact]
    public async Task Timeout_ReturnsTimedOut()
    {
        var runner = new ScriptedProcessRunner();
        runner.Enqueue(Result(exit: -1, timedOut: true));

        var run = await new BuildVerifier(runner, new BuildOptions { TimeoutSeconds = 7 })
            .BuildAsync(Dotnet, Csproj, Root, TestContext.Current.CancellationToken);

        Assert.True(run.TimedOut);
        Assert.Equal(TimeSpan.FromSeconds(7), runner.Calls[0].Timeout);
        Assert.Empty(run.Errors);
    }

    [Fact]
    public async Task Cp949Output_IsRedecoded()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var line = $"{Path.Combine(Root, "App", "A.cs")}(3,5): error CS1002: 세미콜론이 필요합니다 [{Csproj}]";
        var bytes = Encoding.GetEncoding(949).GetBytes(line);
        var utf8Decoded = Encoding.UTF8.GetString(bytes);
        Assert.Contains('\uFFFD', utf8Decoded);
        var runner = new ScriptedProcessRunner();
        runner.Enqueue(Result(utf8Decoded, exit: 1, bytes: bytes));

        var run = await new BuildVerifier(runner, new BuildOptions()).BuildAsync(Dotnet, Csproj, Root, TestContext.Current.CancellationToken);

        var e = Assert.Single(run.Errors);
        Assert.Contains("세미콜론", e.Message);
        Assert.Equal("App/A.cs", e.File);
    }

    [Fact]
    public async Task NonZeroExitWithoutParsedErrors_IsAFailure()
    {
        var runner = new ScriptedProcessRunner();
        runner.Enqueue(Result("something odd", exit: 3));

        var run = await new BuildVerifier(runner, new BuildOptions()).BuildAsync(Dotnet, Csproj, Root, TestContext.Current.CancellationToken);

        var e = Assert.Single(run.Errors);
        Assert.Equal("BUILD", e.Code);
        Assert.Equal("build failed with exit code 3", e.Message);
        Assert.Equal(3, run.ExitCode);
    }

    [Fact]
    public async Task StderrErrorsAreParsedToo()
    {
        var runner = new ScriptedProcessRunner();
        runner.Enqueue(Result(stderr: "MSBUILD : error MSB1009: Project file does not exist.", exit: 1));

        var run = await new BuildVerifier(runner, new BuildOptions()).BuildAsync(Dotnet, Csproj, Root, TestContext.Current.CancellationToken);

        Assert.Equal("MSB1009", Assert.Single(run.Errors).Code);
    }
    [Fact]
    public async Task SymlinkedWorkspaceRoot_RelativizesCompilerRealPaths()
    {
        if (OperatingSystem.IsWindows())
            Assert.Skip("Symbolic links need extra privileges on Windows; junction/subst handling is Windows 미확인.");
        var ct = TestContext.Current.CancellationToken;
        var temp = Directory.CreateTempSubdirectory("pelly-link-");
        try
        {
            var real = Directory.CreateDirectory(Path.Combine(temp.FullName, "real"));
            Directory.CreateDirectory(Path.Combine(real.FullName, "App"));
            var alias = Path.Combine(temp.FullName, "alias");
            Directory.CreateSymbolicLink(alias, real.FullName);
            var physical = await PhysicalPathAsync(alias, ct);
            Assert.NotEqual(alias, physical);
            var runner = new ScriptedProcessRunner();
            runner.Enqueue(Result($"{physical}/App/A.cs(3,5): error CS0103: The name 'x' does not exist [{physical}/App/App.csproj]", exit: 1));

            var run = await new BuildVerifier(runner, new BuildOptions())
                .BuildAsync(Dotnet, Path.Combine(alias, "App", "App.csproj"), alias, ct);

            Assert.Equal("App/A.cs", Assert.Single(run.Errors).File);
        }
        finally
        {
            temp.Delete(recursive: true);
        }
    }

    /// <summary>The path as the OS reports it with every link resolved (<c>pwd -P</c>), independent of the code under test.</summary>
    private static async Task<string> PhysicalPathAsync(string dir, CancellationToken ct)
    {
        var result = await new PellyCoder.Core.Processes.SystemProcessRunner(TimeProvider.System).RunAsync(
            new ProcessSpec("/bin/sh", ["-c", "cd \"$1\" && pwd -P", "sh", dir], dir, TimeSpan.FromSeconds(10)), ct);
        Assert.Equal(0, result.ExitCode);
        return result.StdOut.Trim();
    }
}
