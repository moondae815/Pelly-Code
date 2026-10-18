using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Tests.Testing;

namespace PellyCoder.Core.Tests.Fsm;

/// <summary>
/// <c>pelly cs --test</c>: baseline tests start after the baseline build, APPLY waits for them, and VERIFY fails only on
/// tests that newly fail (design §8.9 step 4).
/// </summary>
public sealed class TestFlowTests : IDisposable
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
    private const string Wrong = "        return a * b;";

    private const string FailingTest = "Shop.Api.Tests.OrderServiceTests.CalculateRefundAsync_ShippedOrder_DeductsReturnShippingFee";

    private const string Sln =
        "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Demo\", \"Demo\\Demo.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\n" +
        "EndProject\n" +
        "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Demo.Tests\", \"Demo.Tests\\Demo.Tests.csproj\", \"{22222222-2222-2222-2222-222222222222}\"\n" +
        "EndProject\n";

    private const string LegacyCsproj =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
        "  <PropertyGroup>\n    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>\n  </PropertyGroup>\n</Project>\n";

    private const string LegacyTestCsproj =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\n" +
        "  <PropertyGroup>\n    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>\n  </PropertyGroup>\n" +
        "  <ItemGroup>\n    <ProjectReference Include=\"..\\Demo\\Demo.csproj\" />\n  </ItemGroup>\n</Project>\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-test-flow-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly ScriptedProcessRunner _processes = new();
    private readonly PellyOptions _options = new();
    private Func<bool>? _isWindows;

    public TestFlowTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Demo"));
        Directory.CreateDirectory(Path.Combine(_root, "Demo.Tests"));
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), Sln);
        File.WriteAllText(CsprojPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n");
        File.WriteAllText(TestCsprojPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <ItemGroup>\n    <ProjectReference Include=\"..\\Demo\\Demo.csproj\" />\n  </ItemGroup>\n</Project>\n");
        File.WriteAllText(CalcPath, CalcSource);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string CalcPath => Path.Combine(_root, "Demo", "Calc.cs");
    private string CsprojPath => Path.Combine(_root, "Demo", "Demo.csproj");
    private string TestCsprojPath => Path.Combine(_root, "Demo.Tests", "Demo.Tests.csproj");
    private string TestSourcePath => Path.Combine(_root, "Demo.Tests", "CalcTests.cs");
    private string BackupDir => Path.Combine(_root, ".pelly", "backup");

    private static RunRequest Req(string path, bool runTests = true, bool dryRun = false) =>
        new(path, null, "Divide", null, "Throw DivideByZeroException when b is zero.", true, dryRun, null, RunTests: runTests);

    private static string Block(string search, string replace) => search + "\n=======\n" + replace + "\n>>>>>>> REPLACE\n";

    private Task<int> Run(RunRequest request) => RunAsync(request, TestContext.Current.CancellationToken);

    private Task<int> RunAsync(RunRequest request, CancellationToken ct) =>
        FsmOrchestratorFactory.Create(_fs, _backend, _ui, _options, TimeProvider.System, _processes, _isWindows)
            .RunAsync(request, ct)
            .WaitAsync(WallClock, CancellationToken.None);

    private static ProcessResult Result(int exit = 0, bool timedOut = false, params string[] lines)
    {
        var stdout = string.Join("\n", lines);
        return new ProcessResult(timedOut ? -1 : exit, stdout, "", Encoding.UTF8.GetBytes(stdout), TimeSpan.FromSeconds(1), timedOut);
    }

    private static ProcessResult Ok() => Result();

    /// <summary>The TRX path pelly put in the test command (VSTest logger argument or the legacy {trx} token).</summary>
    private static string TrxPathOf(ProcessSpec spec)
    {
        const string Logger = "trx;LogFileName=";
        return spec.Arguments.Single(a => a.StartsWith(Logger, StringComparison.Ordinal))[Logger.Length..];
    }

    /// <summary>A test run that writes <paramref name="fixture"/> where pelly expects the report and exits with <paramref name="exit"/>.</summary>
    private void EnqueueTests(string fixture, int exit = 0) =>
        _processes.Enqueue((spec, _) =>
        {
            File.WriteAllText(TrxPathOf(spec), TrxParserTests.Fixture(fixture));
            return Task.FromResult(Result(exit));
        });

    private bool HasBackup() => Directory.Exists(BackupDir) && Directory.EnumerateFiles(BackupDir, "*", SearchOption.AllDirectories).Any();

    private JsonElement Summary()
    {
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, ".pelly", "runs"), "summary.json", SearchOption.AllDirectories));
        return JsonDocument.Parse(File.ReadAllText(file)).RootElement;
    }

    private bool IsTestRun(ProcessSpec spec) => spec.Arguments.FirstOrDefault() == "test";

    [Fact]
    public async Task NewFailure_RetryWithTestsFeedback_ThenPasses()
    {
        _processes.Enqueue(Ok());                 // baseline build
        _processes.Enqueue(Ok());                 // baseline: test project build
        EnqueueTests("modern-pass.trx");          // baseline: tests
        _processes.Enqueue(Ok());                 // attempt 1: build
        _processes.Enqueue(Ok());                 // attempt 1: test project build
        EnqueueTests("modern-fail.trx", exit: 1); // attempt 1: one test now fails
        _processes.Enqueue(Ok());                 // attempt 2: build
        _processes.Enqueue(Ok());
        EnqueueTests("modern-pass.trx");
        _backend.Enqueue(Block(Original, Wrong));
        _backend.Enqueue("Restore the division.</think>", evalCount: 10);
        _backend.Enqueue(Block(Wrong, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
        Assert.Equal(9, _processes.Calls.Count);
        var prompt = _backend.Requests[1].RawPrompt;
        Assert.Contains("TESTS FAILED (1 new failure)", prompt);
        Assert.Contains(FailingTest + ": Assert.Equal() Failure: Values differ", prompt);
        var codes = Summary().GetProperty("errorCodes").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("TEST", codes);

        // Baseline and verify reports never share a path.
        var trxPaths = _processes.Calls.Where(IsTestRun).Select(TrxPathOf).ToList();
        Assert.Equal(3, trxPaths.Distinct().Count());
        Assert.All(trxPaths, p => Assert.StartsWith(Path.Combine(_root, ".pelly", "runs"), p, StringComparison.Ordinal));
        Assert.All(_processes.Calls.Where(IsTestRun), c => Assert.Equal(TestCsprojPath, c.Arguments[1]));
    }

    [Fact]
    public async Task PreexistingFailure_IsNotCounted()
    {
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests("modern-fail.trx", exit: 1); // the original code already fails this test
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests("modern-fail.trx", exit: 1);
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Single(_backend.Requests);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
        Assert.Equal(6, _processes.Calls.Count);
    }

    [Fact]
    public async Task TestProjectBuildBreaks_IsFeedback()
    {
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests("modern-pass.trx");
        _processes.Enqueue(Ok()); // the target still builds
        _processes.Enqueue(Result(1, false,
            $"{TestSourcePath}(12,20): error CS1061: 'Calc' does not contain a definition for 'Divide' [{TestCsprojPath}]"));
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests("modern-pass.trx");
        _backend.Enqueue(Block(Original, Wrong));
        _backend.Enqueue("Keep the signature.</think>", evalCount: 10);
        _backend.Enqueue(Block(Wrong, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        var prompt = _backend.Requests[1].RawPrompt;
        Assert.Contains("BUILD FAILED (1 new error)", prompt);
        Assert.Contains("CS1061", prompt);
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("예기치 않은 오류", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TestTimeout_Exit4_Restored()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests("modern-pass.trx");
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _processes.Enqueue(Result(timedOut: true));
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Single(_backend.Requests);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.Equal(0, _processes.Pending);
        Assert.Contains("테스트가 300초 안에 끝나지 않았습니다 (test.timeoutSeconds)", _ui.Warnings);
    }

    [Fact]
    public async Task NoTestProjects_WarnsAndPasses()
    {
        File.Delete(TestCsprojPath);
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(2, _processes.Calls.Count);
        Assert.Contains("테스트 프로젝트를 찾지 못해 테스트를 건너뜁니다 (빌드 결과로만 판정)", _ui.Warnings);
    }

    [Fact]
    public async Task RunTestsOff_NoTestProcess()
    {
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, runTests: false));

        Assert.Equal(0, exit);
        Assert.Equal(2, _processes.Calls.Count);
        Assert.All(_processes.Calls, c => Assert.Equal(["build", CsprojPath], c.Arguments.Take(2)));
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("테스트", StringComparison.Ordinal));
        Assert.Empty(Directory.GetDirectories(Path.Combine(_root, ".pelly", "runs"), "tests", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task DryRun_StartsNoTests()
    {
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath, dryRun: true));

        Assert.Equal(0, exit);
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public async Task ApplyWaitsForBaselineTests()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        var applyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool? untouchedWhileTestsRan = null;
        _ui.OnStatus = (phase, _) =>
        {
            if (phase == RunPhase.Apply) applyStarted.TrySetResult();
        };
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _processes.Enqueue(async (spec, ct) =>
        {
            // The baseline tests finish only after APPLY has started (and had time to write if it did not wait).
            await applyStarted.Task.WaitAsync(ct);
            await Task.Delay(300, ct);
            untouchedWhileTestsRan = File.ReadAllBytes(CalcPath).SequenceEqual(originalBytes) && !HasBackup();
            File.WriteAllText(TrxPathOf(spec), TrxParserTests.Fixture("modern-pass.trx"));
            return Ok();
        });
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests("modern-pass.trx");
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.True(untouchedWhileTestsRan);
        Assert.Equal(CalcSource.Replace(Original, Guarded), File.ReadAllText(CalcPath));
    }

    [Fact]
    public async Task CancelDuringBaselineTests_Exit2_FileUntouched()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _processes.EnqueueDelay(TimeSpan.FromMinutes(5), Ok()); // baseline tests that never finish on their own
        _backend.Enqueue(Block(Original, Guarded));
        _ui.OnStatus = (phase, _) =>
        {
            if (phase == RunPhase.Apply) cts.CancelAfter(TimeSpan.FromMilliseconds(200));
        };

        var exit = await RunAsync(Req(CalcPath), cts.Token);

        Assert.Equal(2, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        Assert.False(HasBackup());
        Assert.Equal(1, _processes.CancelledCount);
        Assert.Equal(0, _processes.Pending);
        Assert.Equal(3, _processes.Calls.Count);
        Assert.Equal("Cancelled", Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task EarlyFailure_StopsBaselineTests()
    {
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _processes.EnqueueDelay(TimeSpan.FromMinutes(5), Ok());
        _backend.EnqueueException(new LlmUnavailableException("server went away"));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Equal(0, _processes.Pending);
    }

    [Fact]
    public async Task LegacyNoTrx_ExitCodeComparison()
    {
        _isWindows = () => true;
        File.WriteAllText(CsprojPath, LegacyCsproj);
        File.WriteAllText(TestCsprojPath, LegacyTestCsproj);
        _options.Build.MsbuildPath = CsprojPath; // any existing file stands in for MSBuild.exe
        _options.Test.Command = "vstest.console.exe {dll}"; // no {trx}: exit code only (R7)
        _processes.Enqueue(Ok());                // baseline build
        _processes.Enqueue(Ok());                // baseline: test project build
        _processes.Enqueue(Ok());                // baseline: tests pass
        _processes.Enqueue(Ok());                // attempt 1: build
        _processes.Enqueue(Ok());
        _processes.Enqueue(Result(exit: 1));     // attempt 1: tests fail
        _processes.Enqueue(Ok());                // attempt 2
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _backend.Enqueue(Block(Original, Wrong));
        _backend.Enqueue("Restore the division.</think>", evalCount: 10);
        _backend.Enqueue(Block(Wrong, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(9, _processes.Calls.Count);
        Assert.Equal("vstest.console.exe", _processes.Calls[2].FileName);
        Assert.Contains("TESTS FAILED (exit 1)", _backend.Requests[1].RawPrompt);
    }

    [Fact]
    public async Task LegacyNoTrx_BaselineAlsoFails_NotComparable_Passes()
    {
        _isWindows = () => true;
        File.WriteAllText(CsprojPath, LegacyCsproj);
        File.WriteAllText(TestCsprojPath, LegacyTestCsproj);
        _options.Build.MsbuildPath = CsprojPath;
        _options.Test.Command = "vstest.console.exe {dll}";
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _processes.Enqueue(Result(exit: 1)); // already failing before the edit
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _processes.Enqueue(Result(exit: 1));
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Single(_backend.Requests);
        Assert.Single(_ui.Warnings, w => w == "테스트가 원래도 실패해 비교할 수 없습니다");
    }

    [Fact]
    public async Task LegacyOffWindows_SkipsTests_WithNotice()
    {
        _isWindows = () => false;
        File.WriteAllText(CsprojPath, LegacyCsproj);
        File.WriteAllText(TestCsprojPath, LegacyTestCsproj);
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Empty(_processes.Calls);
        Assert.Contains("레거시 테스트는 Windows에서만 실행합니다 — 테스트를 건너뜁니다", _ui.Warnings);
        Assert.Single(_ui.Warnings, w => w.StartsWith("레거시 .NET Framework 프로젝트는 Windows에서만 빌드 검증합니다", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BaselineTestsCannotRun_NoticeAndBuildStillVerifies()
    {
        _isWindows = () => true;
        File.WriteAllText(CsprojPath, LegacyCsproj);
        File.WriteAllText(TestCsprojPath, LegacyTestCsproj);
        _options.Build.MsbuildPath = CsprojPath; // no test.command: the baseline tests cannot run
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(2, _processes.Calls.Count); // baseline build and the verify build; no test process
        Assert.Contains("원본 상태 테스트를 실행하지 못해 테스트 판정을 건너뜁니다: 레거시 테스트 프로젝트는 test.command 설정이 필요합니다",
            _ui.Warnings);
    }

    private const string CrashedTest = "Shop.Api.Tests.OrderServiceTests.CancelAsync_WhenShipped_CurrentlyCancels";

    /// <summary>modern-pass.trx without the results of <paramref name="testNames"/> (a report cut short by a crash).</summary>
    private static string PassWithout(params string[] testNames)
    {
        var xml = TrxParserTests.Fixture("modern-pass.trx");
        foreach (var name in testNames)
            xml = System.Text.RegularExpressions.Regex.Replace(xml, "<UnitTestResult testName=\"" + System.Text.RegularExpressions.Regex.Escape(name) + "\"[^>]*/>", "");
        return xml;
    }

    private void EnqueueReport(string trxXml, int exit) =>
        _processes.Enqueue((spec, _) =>
        {
            File.WriteAllText(TrxPathOf(spec), trxXml);
            return Task.FromResult(Result(exit));
        });

    [Fact]
    public async Task TestHostCrash_PartialReport_IsFailure()
    {
        var originalBytes = File.ReadAllBytes(CalcPath);
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueTests("modern-pass.trx");
        for (var attempt = 0; attempt < 2; attempt++)
        {
            _processes.Enqueue(Ok());
            _processes.Enqueue(Ok());
            EnqueueReport(PassWithout(CrashedTest), exit: 1); // every reported test passed, but the host died
        }
        _backend.Enqueue(Block(Original, Wrong));
        _backend.Enqueue("Avoid the crash.</think>", evalCount: 10);
        _backend.Enqueue(Block(Wrong, Guarded));

        var exit = await Run(Req(CalcPath) with { MaxRetries = 1 });

        Assert.Equal(1, exit);
        Assert.Equal(originalBytes, File.ReadAllBytes(CalcPath));
        var prompt = _backend.Requests[1].RawPrompt;
        Assert.Contains("TESTS FAILED (1 new failure)", prompt);
        Assert.Contains(CrashedTest + ": not run (test host may have crashed)", prompt);
        Assert.Equal("TestErrors", Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task TestHostDiedBeforeAnyResult_NoNamedTest_UsesExitCode()
    {
        var empty = PassWithout(
            "Shop.Api.Tests.OrderServiceTests.CalculateRefundAsync_PaidOrder_RefundsItemsAndShippingFee", FailingTest, CrashedTest);
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueReport(empty, exit: 0); // baseline: no tests reported, exit 0
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueReport(empty, exit: 3);
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        EnqueueReport(empty, exit: 0);
        _backend.Enqueue(Block(Original, Wrong));
        _backend.Enqueue("Avoid the crash.</think>", evalCount: 10);
        _backend.Enqueue(Block(Wrong, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Contains("TESTS FAILED (exit 3)", _backend.Requests[1].RawPrompt);
    }

    [Fact]
    public async Task BaselineTestProjectDidNotBuild_NoticeOnce_NotCompared()
    {
        var testError = $"{TestSourcePath}(12,20): error CS0246: The type or namespace name 'Missing' could not be found [{TestCsprojPath}]";
        _processes.Enqueue(Ok());                          // baseline build
        _processes.Enqueue(Result(1, false, testError));   // baseline: the test project does not build
        _processes.Enqueue(Ok());                          // attempt 1: build
        _processes.Enqueue(Result(1, false, testError));   // same error as before: nothing new
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Single(_backend.Requests);
        Assert.Equal(4, _processes.Calls.Count);
        Assert.Single(_ui.Warnings, w => w == "원본 상태에서 테스트 프로젝트가 빌드되지 않아 테스트 판정을 건너뜁니다: Demo.Tests.csproj");
        Assert.DoesNotContain("테스트가 원래도 실패해 비교할 수 없습니다", _ui.Warnings);
    }

    [Fact]
    public async Task BaselineTestProjectDidNotBuild_NowBuildsAndFails_StillNotCompared()
    {
        var testError = $"{TestSourcePath}(12,20): error CS0246: The type or namespace name 'Missing' could not be found [{TestCsprojPath}]";
        _processes.Enqueue(Ok());
        _processes.Enqueue(Result(1, false, testError));
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());                          // the test project builds now
        EnqueueTests("modern-fail.trx", exit: 1);
        _backend.Enqueue(Block(Original, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Single(_ui.Warnings, w => w.StartsWith("원본 상태에서 테스트 프로젝트가 빌드되지 않아", StringComparison.Ordinal));
        Assert.DoesNotContain("테스트가 원래도 실패해 비교할 수 없습니다", _ui.Warnings);
    }

    [Fact]
    public async Task ExitCodeOnlyFailure_InLaterProject_KeepsNamedFailuresOfEarlierProject()
    {
        var secondPath = Path.Combine(_root, "Demo.Tests2", "Demo.Tests2.csproj");
        Directory.CreateDirectory(Path.GetDirectoryName(secondPath)!);
        File.WriteAllText(secondPath,
            "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <IsTestingPlatformApplication>true</IsTestingPlatformApplication>\n  </PropertyGroup>\n" +
            "  <ItemGroup>\n    <ProjectReference Include=\"..\\Demo\\Demo.csproj\" />\n  </ItemGroup>\n</Project>\n");
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), Sln +
            "Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Demo.Tests2\", \"Demo.Tests2\\Demo.Tests2.csproj\", \"{33333333-3333-3333-3333-333333333333}\"\nEndProject\n");
        _processes.Enqueue(Ok());                   // baseline build
        _processes.Enqueue(Ok());                   // baseline: project 1
        EnqueueTests("modern-pass.trx");
        _processes.Enqueue(Ok());                   // baseline: project 2 (exit code only)
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());                   // attempt 1: build
        _processes.Enqueue(Ok());
        EnqueueTests("modern-fail.trx", exit: 1);   // project 1: one named new failure
        _processes.Enqueue(Ok());
        _processes.Enqueue(Result(exit: 1));        // project 2: exit code only failure
        _processes.Enqueue(Ok());                   // attempt 2: everything passes
        _processes.Enqueue(Ok());
        EnqueueTests("modern-pass.trx");
        _processes.Enqueue(Ok());
        _processes.Enqueue(Ok());
        _backend.Enqueue(Block(Original, Wrong));
        _backend.Enqueue("Restore the division.</think>", evalCount: 10);
        _backend.Enqueue(Block(Wrong, Guarded));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(15, _processes.Calls.Count);
        Assert.Equal(["run", "--project", secondPath, "--no-build"], _processes.Calls[9].Arguments);
        var prompt = _backend.Requests[1].RawPrompt;
        Assert.Contains("TESTS FAILED (1 new failure)", prompt);
        Assert.Contains(FailingTest + ": ", prompt);
    }
}
