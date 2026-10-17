using System.ComponentModel;
using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>Build verification inside the run: baseline started in LOCATE, awaited in APPLY, build step after syntax.</summary>
public sealed class BuildVerificationFlowTests : IDisposable
{
    private static readonly TimeSpan WallClock = TimeSpan.FromSeconds(60);

    private const string CalcSource =
        "namespace Demo;\n" +
        "\n" +
        "public class Calc\n" +
        "{\n" +
        "    public int Divide(int a, int b)\n" +
        "    {\n" +
        "        return a / b;\n" +
        "    }\n" +
        "}\n";

    private const string Original = "        return a / b;";
    private const string Guarded = "        if (b == 0) throw new DivideByZeroException();\n        return a / b;";
    private const string Undefined = "        return a / c;";
    private const string Broken = "        return a / b";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-build-flow-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly ScriptedProcessRunner _processes = new();
    private readonly PellyOptions _options = new();
    private Func<bool>? _isWindows;

    private const string LegacyCsproj =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
        "  <PropertyGroup>\n    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>\n  </PropertyGroup>\n</Project>\n";

    public BuildVerificationFlowTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Demo"));
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), "");
        File.WriteAllText(CsprojPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(CalcPath, CalcSource);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string CalcPath => Path.Combine(_root, "Demo", "Calc.cs");
    private string OtherPath => Path.Combine(_root, "Demo", "Other.cs");
    private string CsprojPath => Path.Combine(_root, "Demo", "Demo.csproj");
    private string BackupDir => Path.Combine(_root, ".pelly", "backup");

    private static RunRequest Req(string path, bool dryRun = false) =>
        new(path, null, "Divide", "Throw DivideByZeroException when b is zero.", true, dryRun, null);

    private static string Block(string search, string replace) => search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    private Task<int> Run(RunRequest request) => RunAsync(request, TestContext.Current.CancellationToken);

    private Task<int> RunAsync(RunRequest request, CancellationToken ct) =>
        FsmOrchestratorFactory.Create(_fs, _backend, _ui, _options, TimeProvider.System, _processes, _isWindows)
            .RunAsync(request, ct)
            .WaitAsync(WallClock, CancellationToken.None);

    /// <summary>A dotnet build output line in the recorded fixture format (absolute path, project suffix).</summary>
    private string Line(string file, int line, string severity, string code, string message) =>
        $"{file}({line},16): {severity} {code}: {message} [{CsprojPath}]";

    private static ProcessResult Build(int exit = 0, bool timedOut = false, params string[] lines)
    {
        var stdout = string.Join("\n", lines);
        return new ProcessResult(timedOut ? -1 : exit, stdout, "", Encoding.UTF8.GetBytes(stdout), TimeSpan.FromSeconds(1), timedOut);
    }

    private static ProcessResult Ok() => Build();

    private ProcessResult Failing(params string[] lines) => Build(1, false, lines);

    private bool HasBackup() => Directory.Exists(BackupDir) && Directory.EnumerateFiles(BackupDir, "*", SearchOption.AllDirectories).Any();

    private JsonElement Summary()
    {
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, ".pelly", "runs"), "summary.json", SearchOption.AllDirectories));
        return JsonDocument.Parse(File.ReadAllText(file)).RootElement;
    }

    [Fact]
    public async Task CleanBaseline_BuildErrorOnAttempt1_FixedOnAttempt2_Exit0()
    {
        _processes.Enqueue(Ok()); // baseline
        _processes.Enqueue(Failing(Line(CalcPath, 7, "error", "CS0103", "The name 'c' does not exist in the current context")));
        _processes.Enqueue(Ok());
        _backend.Enqueue(Block(Original, Undefined));
        _backend.Enqueue("Use b instead of c.</think>", evalCount: 10);
        _backend.Enqueue(Block(Undefined, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
        Assert.Equal(3, _processes.Calls.Count);
        var prompt = _backend.Requests[1].RawPrompt;
        Assert.Contains("BUILD FAILED (1 new error)", prompt);
        Assert.Contains("CS0103", prompt);
        Assert.Contains("Demo/Calc.cs(7): CS0103", prompt);
        var codes = Summary().GetProperty("errorCodes").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("CS0103", codes);
        Assert.All(_processes.Calls, c => Assert.Equal(["build", CsprojPath], c.Arguments.Take(2)));
    }

    [Fact]
    public async Task BaselineHasErrors_EditStillSucceeds_WarnsOnce()
    {
        _processes.Enqueue(Failing(Line(OtherPath, 3, "error", "CS0246", "The type or namespace name 'Missing' could not be found")));
        // Attempt 1 is a syntax error (no build); attempt 2 builds with the same old error on another line.
        _processes.Enqueue(Failing(Line(OtherPath, 4, "error", "CS0246", "The type or namespace name 'Missing' could not be found")));
        _backend.Enqueue(Block(Original, Broken));
        _backend.Enqueue("Add the semicolon.</think>", evalCount: 10);
        _backend.Enqueue(Block(Broken, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
        Assert.Equal(2, _processes.Calls.Count);
        Assert.Single(_ui.Warnings, w => w == "원본 상태에서 이미 빌드 오류가 1개 있습니다 — 새로 생긴 오류만 검사합니다");
    }

    [Fact]
    public async Task StrictBaseline_WithBaselineErrors_Exit4_FileUntouched()
    {
        _options.Build.StrictBaseline = true;
        var originalBytes = File.ReadAllBytes(CalcPath);
        _processes.Enqueue(Failing(
            Line(OtherPath, 3, "error", "CS0246", "The type or namespace name 'Missing' could not be found"),
            Line(OtherPath, 5, "error", "CS0103", "The name 'x' does not exist in the current context")));
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.False(HasBackup());
        Assert.Single(_processes.Calls);
        Assert.Contains("원본 상태에서 이미 빌드 오류가 2개 있습니다 (--strict-baseline)", _ui.Warnings);
    }

    [Fact]
    public async Task BuildTimeout_Exit4_Restores_NoOrphan()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _processes.Enqueue(Ok());
        _processes.Enqueue(Build(timedOut: true));
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Equal(0, _processes.Pending);
        Assert.Equal(2, _processes.Calls.Count);
        Assert.Contains("빌드가 120초 안에 끝나지 않았습니다 (build.timeoutSeconds)", _ui.Warnings);
    }

    [Fact]
    public async Task BaselineTimeout_Exit4_FileUntouched()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _processes.Enqueue(Build(timedOut: true));
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.False(HasBackup());
        Assert.Contains("원본 상태 빌드가 120초 안에 끝나지 않았습니다 (build.timeoutSeconds)", _ui.Warnings);
    }

    [Fact]
    public async Task BaselineToolFailsToStart_Exit4_NamesTool()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _processes.EnqueueThrow(new ProcessStartException("dotnet", new Win32Exception(2)));
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.False(HasBackup());
        Assert.Contains(_ui.Warnings, w => w.StartsWith("빌드 도구를 실행할 수 없습니다: dotnet", StringComparison.Ordinal));
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("예기치 않은 오류", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CancelDuringBaseline_KillsBuild_FileUntouched()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _processes.EnqueueDelay(TimeSpan.FromMinutes(5), Ok());
        _backend.Enqueue(Block(Original, Guarded));
        _ui.OnStatus = (phase, _) =>
        {
            if (phase == RunPhase.Apply) cts.CancelAfter(TimeSpan.FromMilliseconds(200)); // while APPLY awaits the baseline
        };

        var exit = await RunAsync(Req(CalcPath), cts.Token);

        Assert.Equal(2, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.False(HasBackup());
        Assert.Equal(1, _processes.CancelledCount);
        Assert.Equal(0, _processes.Pending);
        Assert.Equal("Cancelled", Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task EarlyFailure_CancelsRunningBaseline_BeforeReturning()
    {
        _processes.EnqueueDelay(TimeSpan.FromMinutes(5), Ok());
        _backend.EnqueueException(new LlmUnavailableException("server went away"));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Equal(1, _processes.CancelledCount);
        Assert.Equal(0, _processes.Pending);
    }

    [Fact]
    public async Task DryRun_RunsNoBuild()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, dryRun: true));

        Assert.Equal(0, exit);
        Assert.Empty(_processes.Calls);
        Assert.Single(_ui.Diffs);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
    }

    [Fact]
    public async Task ToolUnresolvable_Exit4_BeforeAnyWrite()
    {
        _isWindows = () => true; // legacy on "Windows" with no build.msbuildPath and no usable vswhere
        _processes.WhenEmpty(Build(exit: 1)); // vswhere, if the machine has one (real Windows), finds nothing
        File.WriteAllText(CsprojPath, LegacyCsproj);
        var originalBytes = File.ReadAllBytes(CalcPath);

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.All(_processes.Calls, c => Assert.EndsWith("vswhere.exe", c.FileName, StringComparison.OrdinalIgnoreCase));
        Assert.False(HasBackup());
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Contains(_ui.Warnings, w => w.StartsWith("MSBuild.exe를 찾을 수 없습니다", StringComparison.Ordinal));
        Assert.Equal("Legacy", Summary().GetProperty("projectStyle").GetString());
    }

    [Fact]
    public async Task LegacyOffWindows_SyntaxOnly_WithNotice()
    {
        _isWindows = () => false;
        File.WriteAllText(CsprojPath, LegacyCsproj);
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Empty(_processes.Calls);
        Assert.Contains("레거시 .NET Framework 프로젝트는 Windows에서만 빌드 검증합니다 — 구문 검사만 합니다", _ui.Warnings);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
        Assert.Equal("Legacy", Summary().GetProperty("projectStyle").GetString());
    }

    [Fact]
    public async Task LegacyOnWindows_BuildsWithConfiguredMsBuild()
    {
        _isWindows = () => true;
        File.WriteAllText(CsprojPath, LegacyCsproj);
        _options.Build.MsbuildPath = CsprojPath; // any existing file stands in for MSBuild.exe
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(2, _processes.Calls.Count);
        Assert.All(_processes.Calls, c =>
        {
            Assert.Equal(CsprojPath, c.FileName);
            Assert.Equal([CsprojPath, "/t:Build"], c.Arguments.Take(2));
        });
    }

    [Fact]
    public async Task BaselineCannotCompile_Netsdk1004_Exit4_FileUntouched()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        const string Targets = "/usr/local/share/dotnet/sdk/10.0.302/Sdks/Microsoft.NET.Sdk/targets/Microsoft.PackageDependencyResolution.targets";
        const string Message = "Assets file '/tmp/obj/project.assets.json' not found. Run a NuGet package restore to generate this file.";
        _processes.Enqueue(Failing(
            Line(OtherPath, 3, "error", "CS0246", "The type or namespace name 'Missing' could not be found"),
            Line(Targets, 266, "error", "NETSDK1004", Message)));
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.False(HasBackup());
        Assert.Single(_processes.Calls);
        Assert.Contains($"빌드 검증을 할 수 없습니다: NETSDK1004 {Message}. 프로젝트를 복원(dotnet restore 또는 Visual Studio 복원)하고 빌드되는지 먼저 확인하세요",
            _ui.Warnings);
        Assert.DoesNotContain(_ui.Warnings, w => w.StartsWith("원본 상태에서 이미 빌드 오류가", StringComparison.Ordinal));
        Assert.Equal("BuildUnverifiable", Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task BaselineSyntheticBuildError_Exit4()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _processes.Enqueue(Failing("something the parser does not understand"));
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.False(HasBackup());
        Assert.Contains("빌드 검증을 할 수 없습니다: BUILD build failed with exit code 1. 프로젝트를 복원(dotnet restore 또는 Visual Studio 복원)하고 빌드되는지 먼저 확인하세요",
            _ui.Warnings);
        Assert.Equal("BuildUnverifiable", Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task NoCsproj_SyntaxOnly_WithNotice()
    {
        File.Delete(CsprojPath);
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Empty(_processes.Calls);
        Assert.Contains("csproj가 없어 빌드 검증을 건너뜁니다 (구문 검사만 합니다)", _ui.Warnings);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
    }

    [Fact]
    public async Task SyntaxErrorShortCircuits_NoBuildRun()
    {
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _backend.Enqueue(Block(Original, Broken));
        _backend.Enqueue("Add the semicolon.</think>", evalCount: 10);
        _backend.Enqueue(Block(Broken, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Contains("SYNTAX CHECK FAILED", _backend.Requests[1].RawPrompt);
        Assert.Equal(2, _processes.Calls.Count); // baseline + the build of attempt 2 only
    }

    [Fact]
    public async Task NewWarning_IsNotice_NotFailure()
    {
        _processes.Enqueue(Ok());
        _processes.Enqueue(Build(0, false, Line(CalcPath, 7, "warning", "CS0168", "The variable 'e' is declared but never used")));
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Single(_backend.Requests);
        Assert.Contains("새 경고 1개: CS0168", _ui.Warnings);
    }

    [Fact]
    public async Task WarnAsError_NewWarningFails()
    {
        _options.Build.WarnAsError = true;
        _processes.Enqueue(Ok());
        _processes.Enqueue(Build(0, false, Line(CalcPath, 7, "warning", "CS0168", "The variable 'e' is declared but never used")));
        _processes.Enqueue(Ok());
        _backend.Enqueue(Block(Original, Undefined));
        _backend.Enqueue("Remove the unused variable.</think>", evalCount: 10);
        _backend.Enqueue(Block(Undefined, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        var prompt = _backend.Requests[1].RawPrompt;
        Assert.Contains("BUILD FAILED (1 new error)", prompt);
        Assert.Contains("CS0168", prompt);
        Assert.Equal("-clp:NoSummary", _processes.Calls[1].Arguments[^1]);
    }
}
