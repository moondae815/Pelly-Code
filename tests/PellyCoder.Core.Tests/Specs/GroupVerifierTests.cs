using System.ComponentModel;
using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Specs;

public sealed class GroupVerifierTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";
    private static readonly Workspace Ws = new(Root, Path.Combine(Root, ".pelly"));

    private const string SdkProject = "<Project Sdk=\"Microsoft.NET.Sdk\"></Project>";
    private const string LegacyProject =
        "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"></Project>";

    private readonly MockFileSystem _fs = new();
    private readonly ScriptedProcessRunner _processes = new();

    private static string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static string App => P("App/App.csproj");
    private static string Lib => P("Lib/Lib.csproj");
    private static string Legacy => P("Old/Old.csproj");

    public GroupVerifierTests()
    {
        _fs.AddFile(App, new MockFileData(SdkProject));
        _fs.AddFile(Lib, new MockFileData(SdkProject));
        _fs.AddFile(Legacy, new MockFileData(LegacyProject));
    }

    private GroupVerifier Verifier(bool windows = false)
    {
        var options = new BuildOptions();
        return new GroupVerifier(_fs, new BuildVerifier(_processes, options),
            new BuildToolResolver(_fs, _processes, options, () => windows), () => windows);
    }

    private static string Line(string csproj, string file, int line, string code, string message) =>
        $"{P(file)}({line},16): error {code}: {message} [{csproj}]";

    private static ProcessResult Build(int exit, bool timedOut, params string[] lines)
    {
        var stdout = string.Join("\n", lines);
        return new ProcessResult(timedOut ? -1 : exit, stdout, "", Encoding.UTF8.GetBytes(stdout), TimeSpan.FromSeconds(1), timedOut);
    }

    /// <summary>Queues one handler per expected build that answers by the csproj in the dotnet arguments.</summary>
    private void Script(int builds, Dictionary<string, ProcessResult> byProject)
    {
        for (var i = 0; i < builds; i++)
            _processes.Enqueue((spec, _) => Task.FromResult(byProject.TryGetValue(spec.Arguments[1], out var r) ? r : Build(0, false)));
    }

    [Fact]
    public async Task Builds_every_project_and_merges_errors()
    {
        Script(2, new()
        {
            [Lib] = Build(1, false, Line(Lib, "Lib/A.cs", 3, "CS0103", "The name 'x' does not exist in the current context")),
            [App] = Build(1, false, Line(App, "App/B.cs", 5, "CS0029", "Cannot implicitly convert type 'string' to 'int'")),
        });

        var result = await Verifier().BuildAsync(Ws, [Lib, App], TestContext.Current.CancellationToken);

        Assert.True(result.Verifiable);
        Assert.Null(result.Reason);
        Assert.Null(result.FailureKind);
        Assert.Equal([Lib, App], _processes.Calls.Select(c => c.Arguments[1]));
        Assert.Equal([("Lib/A.cs", "CS0103"), ("App/B.cs", "CS0029")], result.Errors.Select(e => (e.File, e.Code)));
    }

    [Fact]
    public async Task Same_error_reported_by_two_project_builds_counts_once()
    {
        var shared = Line(Lib, "Lib/A.cs", 3, "CS0103", "The name 'x' does not exist in the current context");
        Script(2, new() { [Lib] = Build(1, false, shared), [App] = Build(1, false, shared) });

        var result = await Verifier().BuildAsync(Ws, [Lib, App], TestContext.Current.CancellationToken);

        Assert.Equal("CS0103", Assert.Single(result.Errors).Code);
    }

    [Fact]
    public async Task Only_new_errors_count()
    {
        const string undefined = "The name 'x' does not exist in the current context";
        Script(1, new() { [App] = Build(1, false, Line(App, "App/B.cs", 5, "CS0103", undefined)) });
        Script(1, new()
        {
            [App] = Build(1, false,
                Line(App, "App/B.cs", 7, "CS0103", undefined),
                Line(App, "App/B.cs", 9, "CS0029", "Cannot implicitly convert type 'string' to 'int'")),
        });
        var verifier = Verifier();

        var baseline = await verifier.BuildAsync(Ws, [App], TestContext.Current.CancellationToken);
        var current = await verifier.BuildAsync(Ws, [App], TestContext.Current.CancellationToken);

        Assert.True(baseline.Verifiable);
        Assert.Equal("CS0029", Assert.Single(GroupVerifier.NewErrors(baseline, current)).Code);
    }

    [Fact]
    public async Task Non_compiler_baseline_error_is_unverifiable()
    {
        Script(2, new()
        {
            [Lib] = Build(1, false, $"/usr/share/dotnet/sdk/Microsoft.PackageDependencyResolution.targets(266,5): error NETSDK1004: Assets file not found. [{Lib}]"),
        });

        var result = await Verifier().BuildAsync(Ws, [Lib, App], TestContext.Current.CancellationToken);

        Assert.False(result.Verifiable);
        Assert.Equal("GroupUnverifiable", result.FailureKind);
        Assert.Empty(result.Errors);
        Assert.Contains("그룹 빌드 검증 불가 (Lib/Lib.csproj): NETSDK1004 Assets file not found.", result.Reason);
        Assert.DoesNotContain("검증을 할 수 없습니다", result.Reason); // "검증 불가" already says it
        Assert.Contains("dotnet restore", result.Reason);
    }

    [Fact]
    public async Task Locked_output_baseline_error_keeps_its_hint()
    {
        Script(1, new()
        {
            [App] = Build(1, false, $"/usr/share/dotnet/sdk/Microsoft.Common.CurrentVersion.targets(4,5): error MSB3027: Could not copy App.dll. [{App}]"),
        });

        var result = await Verifier().BuildAsync(Ws, [App], TestContext.Current.CancellationToken);

        Assert.False(result.Verifiable);
        Assert.Contains("그룹 빌드 검증 불가 (App/App.csproj): 빌드 출력 파일이 잠겨 있습니다: MSB3027", result.Reason);
        Assert.Contains("실행 중인 프로그램", result.Reason);
        Assert.DoesNotContain("검증을 할 수 없습니다", result.Reason);
    }

    [Fact]
    public async Task Missing_build_tool_is_unverifiable_before_building()
    {
        // Windows, a legacy project, no build.msbuildPath and no vswhere: MSBuild cannot be found.
        var result = await Verifier(windows: true).BuildAsync(Ws, [App, Legacy], TestContext.Current.CancellationToken);

        Assert.False(result.Verifiable);
        Assert.Equal("GroupUnverifiable", result.FailureKind);
        Assert.Contains("그룹 빌드 검증 불가 (Old/Old.csproj)", result.Reason);
        Assert.Contains("MSBuild.exe를 찾을 수 없습니다", result.Reason);
        Assert.Empty(result.Errors);
        Assert.DoesNotContain(_processes.Calls, c => c.Arguments.Count > 0 && c.Arguments[0] == "build");
    }

    [Fact]
    public async Task Failed_build_without_parsed_error_is_unverifiable()
    {
        Script(1, new() { [App] = Build(1, false) });

        var result = await Verifier().BuildAsync(Ws, [App], TestContext.Current.CancellationToken);

        Assert.False(result.Verifiable);
        Assert.Contains("BUILD", result.Reason);
    }

    [Fact]
    public async Task Legacy_in_scope_off_windows_is_unverifiable()
    {
        var result = await Verifier(windows: false).BuildAsync(Ws, [App, Legacy], TestContext.Current.CancellationToken);

        Assert.False(result.Verifiable);
        Assert.Equal("GroupUnverifiable", result.FailureKind);
        Assert.Contains("Old/Old.csproj", result.Reason);
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public async Task Unreadable_project_is_unverifiable_before_building()
    {
        _fs.AddFile(Lib, new MockFileData("<Project"));

        var result = await Verifier().BuildAsync(Ws, [App, Lib], TestContext.Current.CancellationToken);

        Assert.False(result.Verifiable);
        Assert.Equal("GroupUnverifiable", result.FailureKind);
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public async Task Timeout_is_unverifiable()
    {
        Script(1, new() { [App] = Build(0, true) });

        var result = await Verifier().BuildAsync(Ws, [App, Lib], TestContext.Current.CancellationToken);

        Assert.False(result.Verifiable);
        Assert.Equal("GroupUnverifiable", result.FailureKind);
        Assert.Contains("App/App.csproj", result.Reason);
        Assert.Contains($"{new BuildOptions().TimeoutSeconds}초 안에", result.Reason);
        Assert.Single(_processes.Calls);
    }

    [Fact]
    public async Task Build_that_cannot_start_is_unverifiable()
    {
        _processes.EnqueueThrow(new ProcessStartException("dotnet", new Win32Exception(2)));

        var result = await Verifier().BuildAsync(Ws, [App], TestContext.Current.CancellationToken);

        Assert.False(result.Verifiable);
        Assert.Equal("GroupUnverifiable", result.FailureKind);
        Assert.Contains("dotnet", result.Reason);
    }

    private static string MsBuild => P("tools/MSBuild.exe");

    /// <summary>A verifier on Windows with MSBuild configured, so legacy projects build.</summary>
    private GroupVerifier WindowsVerifier()
    {
        _fs.AddFile(MsBuild, new MockFileData(""));
        var options = new BuildOptions { MsbuildPath = MsBuild };
        return new GroupVerifier(_fs, new BuildVerifier(_processes, options), new BuildToolResolver(_fs, _processes, options, () => true), () => true);
    }

    [Fact]
    public async Task Group_builds_ask_both_tools_for_full_paths()
    {
        Script(2, []);

        var result = await WindowsVerifier().BuildAsync(Ws, [App, Legacy], TestContext.Current.CancellationToken);

        Assert.True(result.Verifiable, result.Reason);
        Assert.Equal("dotnet", _processes.Calls[0].FileName);
        Assert.Contains("-p:GenerateFullPaths=true", _processes.Calls[0].Arguments);
        Assert.Equal(MsBuild, _processes.Calls[1].FileName);
        Assert.Contains("/p:GenerateFullPaths=true", _processes.Calls[1].Arguments);
    }

    [Fact]
    public async Task Project_relative_error_path_is_resolved_against_the_project_folder()
    {
        // Legacy MSBuild without full paths prints the file relative to the project.
        _fs.AddFile(P("Old/Services/OrderService.cs"), new MockFileData("class OrderService { }"));
        _processes.Enqueue((_, _) => Task.FromResult(Build(1, false,
            $@"Services\OrderService.cs(12,5): error CS0029: Cannot implicitly convert type 'string' to 'int' [{Legacy}]")));
        var result = await WindowsVerifier().BuildAsync(Ws, [Legacy], TestContext.Current.CancellationToken);

        Assert.True(result.Verifiable, result.Reason);
        var error = Assert.Single(result.Errors);
        Assert.Equal(("Old/Services/OrderService.cs", 12, "CS0029"), (error.File, error.Line, error.Code));
    }

    [Fact]
    public async Task Relative_error_path_prefers_the_project_folder_over_the_workspace_root()
    {
        // MSBuild prints relative paths from the project folder: a same-named file under the root is a different file.
        _fs.AddFile(P("Services/OrderService.cs"), new MockFileData("class Other { }"));
        _fs.AddFile(P("Old/Services/OrderService.cs"), new MockFileData("class OrderService { }"));
        _processes.Enqueue((_, _) => Task.FromResult(Build(1, false,
            $@"Services\OrderService.cs(12,5): error CS0029: Cannot implicitly convert type 'string' to 'int' [{Legacy}]")));
        var result = await WindowsVerifier().BuildAsync(Ws, [Legacy], TestContext.Current.CancellationToken);

        Assert.True(result.Verifiable, result.Reason);
        Assert.Equal("Old/Services/OrderService.cs", Assert.Single(result.Errors).File);
    }

    [Fact]
    public async Task Full_error_path_stays_relative_to_the_workspace_root_even_when_the_project_folder_has_the_same_path()
    {
        _fs.AddFile(P("App/B.cs"), new MockFileData("class B { }"));
        _fs.AddFile(P("App/App/B.cs"), new MockFileData("class Nested { }"));
        Script(1, new() { [App] = Build(1, false, Line(App, "App/B.cs", 5, "CS0029", "Cannot implicitly convert type 'string' to 'int'")) });

        var result = await Verifier().BuildAsync(Ws, [App], TestContext.Current.CancellationToken);

        Assert.Equal("App/B.cs", Assert.Single(result.Errors).File);
    }

    [Fact]
    public async Task Workspace_relative_error_path_stays_as_it_is()
    {
        _fs.AddFile(P("App/B.cs"), new MockFileData("class B { }"));
        Script(1, new() { [App] = Build(1, false, Line(App, "App/B.cs", 5, "CS0029", "Cannot implicitly convert type 'string' to 'int'")) });

        var result = await Verifier().BuildAsync(Ws, [App], TestContext.Current.CancellationToken);

        Assert.Equal("App/B.cs", Assert.Single(result.Errors).File);
    }
}
