using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Builds;

public sealed class BuildToolResolverTests
{
    private static readonly string VsWhere = Path.Combine(
        Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)",
        @"Microsoft Visual Studio\Installer\vswhere.exe");

    private const string FoundMsBuild = @"C:\VS\MSBuild\Current\Bin\MSBuild.exe";
    private const string Doctor = "MSBuild.exe를 찾을 수 없습니다. Visual Studio Build Tools를 설치하거나 build.msbuildPath를 설정하세요 (pelly doctor)";

    private static ProcessResult Out(string text, int exit = 0) =>
        new(exit, text, "", System.Text.Encoding.UTF8.GetBytes(text), TimeSpan.Zero, false);

    private static MockFileSystem Files(params string[] paths) =>
        new(paths.ToDictionary(p => p, _ => new MockFileData("x")));

    private static BuildToolResolver Make(MockFileSystem fs, ScriptedProcessRunner runner, BuildOptions? o = null, bool windows = true) =>
        new(fs, runner, o ?? new BuildOptions(), () => windows);

    [Fact]
    public async Task Sdk_UsesDotnet_WithoutRunningAnything()
    {
        var runner = new ScriptedProcessRunner();
        var r = await Make(Files(), runner).ResolveAsync(ProjectStyle.Sdk, TestContext.Current.CancellationToken);
        Assert.Equal(new BuildTool(BuildToolKind.Dotnet, "dotnet"), r.Tool);
        Assert.Null(r.Error);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Legacy_OnNonWindows_IsErrorWithWindowsMessage()
    {
        var r = await Make(Files(), new ScriptedProcessRunner(), windows: false)
            .ResolveAsync(ProjectStyle.Legacy, TestContext.Current.CancellationToken);
        Assert.Null(r.Tool);
        Assert.Equal("레거시 .NET Framework 프로젝트는 Windows에서만 빌드 검증할 수 있습니다", r.Error);
    }

    [Fact]
    public async Task Legacy_ConfiguredMsbuildPath_Wins()
    {
        var path = TestPaths.P(@"C:\custom\MSBuild.exe");
        var runner = new ScriptedProcessRunner();
        var r = await Make(Files(path), runner, new BuildOptions { MsbuildPath = path })
            .ResolveAsync(ProjectStyle.Legacy, TestContext.Current.CancellationToken);
        Assert.Equal(new BuildTool(BuildToolKind.MsBuild, path), r.Tool);
        Assert.Empty(runner.Calls);
    }

    [Fact]
    public async Task Legacy_VswhereFirstLine_IsUsed()
    {
        var found = TestPaths.P(FoundMsBuild);
        var runner = new ScriptedProcessRunner();
        runner.Enqueue(Out(found + "\r\nC:\\other\\MSBuild.exe\r\n"));
        var r = await Make(Files(VsWhere, found), runner).ResolveAsync(ProjectStyle.Legacy, TestContext.Current.CancellationToken);

        Assert.Equal(new BuildTool(BuildToolKind.MsBuild, found), r.Tool);
        var call = Assert.Single(runner.Calls);
        Assert.Equal(VsWhere, call.FileName);
        Assert.Equal(["-latest", "-products", "*", "-requires", "Microsoft.Component.MSBuild", "-find", @"MSBuild\**\Bin\MSBuild.exe"], call.Arguments);
        Assert.Equal(TimeSpan.FromSeconds(10), call.Timeout);
    }

    [Fact]
    public async Task Legacy_VswhereMissingOrEmpty_IsErrorWithDoctorHint()
    {
        var ct = TestContext.Current.CancellationToken;

        // vswhere.exe itself missing: never run.
        var none = new ScriptedProcessRunner();
        var r1 = await Make(Files(), none).ResolveAsync(ProjectStyle.Legacy, ct);
        Assert.Equal(Doctor, r1.Error);
        Assert.Empty(none.Calls);

        // Empty output, non-zero exit, and start failure all give the same error.
        var empty = new ScriptedProcessRunner();
        empty.Enqueue(Out(""));
        Assert.Equal(Doctor, (await Make(Files(VsWhere), empty).ResolveAsync(ProjectStyle.Legacy, ct)).Error);

        var failed = new ScriptedProcessRunner();
        failed.Enqueue(Out("C:\\x\\MSBuild.exe", exit: 1));
        Assert.Equal(Doctor, (await Make(Files(VsWhere), failed).ResolveAsync(ProjectStyle.Legacy, ct)).Error);

        var thrown = new ScriptedProcessRunner();
        thrown.EnqueueThrow(new ProcessStartException(VsWhere, new IOException("boom")));
        Assert.Equal(Doctor, (await Make(Files(VsWhere), thrown).ResolveAsync(ProjectStyle.Legacy, ct)).Error);

        // vswhere points at a file that does not exist.
        var ghost = new ScriptedProcessRunner();
        ghost.Enqueue(Out(TestPaths.P(FoundMsBuild)));
        Assert.Equal(Doctor, (await Make(Files(VsWhere), ghost).ResolveAsync(ProjectStyle.Legacy, ct)).Error);
    }

    [Fact]
    public async Task Legacy_ConfiguredPathMissing_FallsBackToVswhere()
    {
        var found = TestPaths.P(FoundMsBuild);
        var runner = new ScriptedProcessRunner();
        runner.Enqueue(Out(found));
        var r = await Make(Files(VsWhere, found), runner, new BuildOptions { MsbuildPath = TestPaths.P(@"C:\nope\MSBuild.exe") })
            .ResolveAsync(ProjectStyle.Legacy, TestContext.Current.CancellationToken);
        Assert.Equal(new BuildTool(BuildToolKind.MsBuild, found), r.Tool);
        Assert.Single(runner.Calls);
    }

    [Fact]
    public async Task None_ThrowsArgumentException() =>
        await Assert.ThrowsAsync<ArgumentException>(() =>
            Make(Files(), new ScriptedProcessRunner()).ResolveAsync(ProjectStyle.None, TestContext.Current.CancellationToken));
}
