using System.IO.Abstractions;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.CharTests;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Tests.Fakes;

namespace PellyCoder.Core.Tests.CharTests;

/// <summary>
/// <c>pelly test</c> end to end with a scripted model and a simulated build/test process: verdicts 4-7, retries, approval, and the
/// guarantee that the target file ends byte-identical and the test file and csproj are back as they were unless kept.
/// </summary>
public sealed class CharTestOrchestratorTests : IDisposable
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
        "\n" +
        "    private int Hidden(int a)\n" +
        "    {\n" +
        "        return a;\n" +
        "    }\n" +
        "}\n";

    private const string TestSource =
        "using Xunit;\n" +
        "\n" +
        "namespace Demo.Tests;\n" +
        "\n" +
        "public class CalcTests\n" +
        "{\n" +
        "    [Fact]\n" +
        "    public void Existing_Works()\n" +
        "    {\n" +
        "        Assert.Equal(2, new Demo.Calc().Divide(4, 2));\n" +
        "    }\n" +
        "}\n";

    private const string SdkCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n</Project>\n";

    private const string SdkTestCsproj =
        "<Project Sdk=\"Microsoft.NET.Sdk\">\n" +
        "  <PropertyGroup>\n    <TargetFramework>net8.0</TargetFramework>\n  </PropertyGroup>\n" +
        "  <ItemGroup>\n    <PackageReference Include=\"xunit\" Version=\"2.9.3\" />\n  </ItemGroup>\n" +
        "  <ItemGroup>\n    <ProjectReference Include=\"..\\Demo\\Demo.csproj\" />\n  </ItemGroup>\n</Project>\n";

    private const string LegacyCsproj =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
        "  <PropertyGroup>\r\n    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>\r\n  </PropertyGroup>\r\n" +
        "  <ItemGroup>\r\n    <Compile Include=\"Calc.cs\" />\r\n  </ItemGroup>\r\n</Project>\r\n";

    private const string LegacyTestCsproj =
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n" +
        "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\">\r\n" +
        "  <PropertyGroup>\r\n    <TargetFrameworkVersion>v4.8</TargetFrameworkVersion>\r\n  </PropertyGroup>\r\n" +
        "  <ItemGroup>\r\n    <Compile Include=\"Properties\\AssemblyInfo.cs\" />\r\n  </ItemGroup>\r\n" +
        "  <ItemGroup>\r\n    <ProjectReference Include=\"..\\Demo\\Demo.csproj\" />\r\n  </ItemGroup>\r\n</Project>\r\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-chartest-" + Guid.NewGuid().ToString("N"));
    private readonly FileSystem _fs = new();
    private readonly ScriptedLlmBackend _backend = new();
    private readonly ScriptedUserInteraction _ui = new();
    private readonly ScriptedProcessRunner _processes = new();
    private readonly PellyOptions _options = new();
    private Func<bool>? _isWindows;

    /// <summary>Decides whether a new test passes: (test name, target mutated, index of this test run starting at 0).</summary>
    private Func<string, bool, int, bool> _passes = (name, mutated, _) => !mutated;

    /// <summary>Build output for a build call, or null for a clean build: (csproj built, target mutated).</summary>
    private Func<string, bool, string?> _buildError = (_, _) => null;

    /// <summary>Runs when a test process starts, with whether the target is mutated (<see cref="_testRuns"/> is this run's index).</summary>
    private Action<bool>? _onTestRun;

    private int _testRuns;
    private readonly List<string> _testFileAtRuns = [];

    public CharTestOrchestratorTests()
    {
        _options.Llm.ThinkMode = ThinkMode.Off;
        Directory.CreateDirectory(Path.Combine(_root, "Demo"));
        Directory.CreateDirectory(Path.Combine(_root, "Demo.Tests"));
        WriteSln("Demo.Tests");
        File.WriteAllText(CsprojPath, SdkCsproj);
        File.WriteAllText(TestCsprojPath, SdkTestCsproj);
        File.WriteAllText(CalcPath, CalcSource);
        File.WriteAllText(TestPath, TestSource);
        for (var i = 0; i < 40; i++) _processes.Enqueue(Simulate);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }

    private string CalcPath => Path.Combine(_root, "Demo", "Calc.cs");
    private string CsprojPath => Path.Combine(_root, "Demo", "Demo.csproj");
    private string TestCsprojPath => Path.Combine(_root, "Demo.Tests", "Demo.Tests.csproj");
    private string TestPath => Path.Combine(_root, "Demo.Tests", "CalcTests.cs");

    private void WriteSln(params string[] testProjects)
    {
        var sb = new StringBuilder();
        sb.Append("Project(\"{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}\") = \"Demo\", \"Demo\\Demo.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\nEndProject\n");
        var n = 2;
        foreach (var name in testProjects)
            sb.Append($"Project(\"{{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}}\") = \"{name}\", \"{name}\\{name}.csproj\", \"{{22222222-2222-2222-2222-22222222222{n++}}}\"\nEndProject\n");
        File.WriteAllText(Path.Combine(_root, "Demo.sln"), sb.ToString());
    }

    private static CharTestRequest Req(string path, int count = 3, bool yes = true, bool dryRun = false, int maxRetries = 2,
        string method = "Divide", bool verbose = false, string? testProject = null) =>
        new(path, null, method, null, null, count, testProject, yes, dryRun, maxRetries, verbose);

    private Task<int> Run(CharTestRequest request) => RunAsync(request, TestContext.Current.CancellationToken);

    private Task<int> RunAsync(CharTestRequest request, CancellationToken ct) =>
        FsmOrchestratorFactory.CreateCharTest(_fs, _backend, _ui, _options, TimeProvider.System, _processes, _isWindows)
            .RunAsync(request, ct)
            .WaitAsync(WallClock, CancellationToken.None);

    private static ProcessResult Result(int exit = 0, string stdout = "") =>
        new(exit, stdout, "", Encoding.UTF8.GetBytes(stdout), TimeSpan.FromSeconds(1), false);

    private static string? TrxArg(ProcessSpec spec)
    {
        var arg = spec.Arguments.FirstOrDefault(a => a.EndsWith(".trx", StringComparison.Ordinal));
        if (arg is null) return null;
        var eq = arg.LastIndexOf('=');
        return eq >= 0 ? arg[(eq + 1)..] : arg;
    }

    /// <summary>
    /// A build returns <see cref="_buildError"/>; a test run writes a TRX with the existing test passing and every test method
    /// currently in the test file judged by <see cref="_passes"/>.
    /// </summary>
    private Task<ProcessResult> Simulate(ProcessSpec spec, CancellationToken ct)
    {
        var mutated = File.ReadAllText(CalcPath).Contains("pelly-mutation", StringComparison.Ordinal);
        var trx = TrxArg(spec);
        if (trx is null)
        {
            var project = spec.Arguments.FirstOrDefault(a => a.EndsWith(".csproj", StringComparison.Ordinal)) ?? "";
            return Task.FromResult(_buildError(project, mutated) is { } error ? Result(1, error) : Result());
        }

        _onTestRun?.Invoke(mutated);
        ct.ThrowIfCancellationRequested();
        var index = _testRuns++;
        var testFile = File.Exists(TestPath) ? File.ReadAllText(TestPath) : "";
        _testFileAtRuns.Add(testFile);
        var names = Regex.Matches(testFile, @"public (?:async Task|void) (\w+)\(").Select(m => m.Groups[1].Value).ToList();
        var results = names.Select(n => (Name: "Demo.Tests.CalcTests." + n, Passed: n == "Existing_Works" || _passes(n, mutated, index))).ToList();
        File.WriteAllText(trx, Trx(results));
        return Task.FromResult(Result(results.All(r => r.Passed) ? 0 : 1));
    }

    private static string Trx(IEnumerable<(string Name, bool Passed)> results)
    {
        var sb = new StringBuilder("<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>");
        foreach (var (name, passed) in results)
        {
            sb.Append($"<UnitTestResult testName=\"{SecurityElement.Escape(name)}\" outcome=\"{(passed ? "Passed" : "Failed")}\"");
            sb.Append(passed ? " />" : "><Output><ErrorInfo><Message>Assert.Equal() Failure: Values differ\nExpected: 4\nActual:   3</Message></ErrorInfo></Output></UnitTestResult>");
        }
        sb.Append("</Results><ResultSummary outcome=\"Completed\" /></TestRun>");
        return sb.ToString();
    }

    private static string Members(params string[] names) =>
        "### MEMBERS\n" + string.Join("\n", names.Select(n =>
            $"[Fact]\npublic void {n}()\n{{\n    var result = new Demo.Calc().Divide(6, 2);\n    Assert.Equal(3, result);\n}}\n"));

    private static readonly string[] Three = ["Divide_Six_Two", "Divide_Nine_Three", "Divide_Two_Two"];

    private bool PendingExists() =>
        Directory.Exists(Path.Combine(_root, ".pelly", "runs"))
        && Directory.EnumerateFiles(Path.Combine(_root, ".pelly", "runs"), CharTestPending.FileName, SearchOption.AllDirectories).Any();

    private JsonElement Summary()
    {
        var file = Assert.Single(Directory.GetFiles(Path.Combine(_root, ".pelly", "runs"), "summary.json", SearchOption.AllDirectories));
        return JsonDocument.Parse(File.ReadAllText(file)).RootElement;
    }

    private string Prompt(int i) => _backend.Requests[i].RawPrompt;

    [Fact]
    public async Task HappyPath_KeepsTests_Exit0_TargetUnchanged()
    {
        var calcBytes = File.ReadAllBytes(CalcPath);
        _backend.Enqueue(Members(Three));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(calcBytes, File.ReadAllBytes(CalcPath));
        var test = File.ReadAllText(TestPath);
        Assert.All(Three, n => Assert.Contains("public void " + n + "()", test));
        Assert.StartsWith(TestSource[..TestSource.LastIndexOf("}\n", StringComparison.Ordinal)], test, StringComparison.Ordinal);
        Assert.Single(_backend.Requests);
        Assert.Contains("[READ-ONLY: target]", Prompt(0));
        Assert.EndsWith("### USINGS\n", Prompt(0));
        Assert.Contains("C# language version: 12", Prompt(0));
        Assert.Contains("[READ-ONLY: example test]\n[Fact]\npublic void Existing_Works()", Prompt(0));
        Assert.Equal(4, _testRuns); // baseline, current, rerun, mutation
        Assert.False(PendingExists());
        Assert.Contains(_ui.Diffs, d => d.Path == TestPath);
        Assert.Contains(_ui.Statuses, s => s.Message.Contains("변이 검출 3/3 ✓", StringComparison.Ordinal));
    }

    [Fact]
    public async Task StaticRejection_RetriesWithFeedback_FileNeverWritten()
    {
        var testBytes = File.ReadAllBytes(TestPath);
        _backend.Enqueue("### MEMBERS\n[Fact]\npublic void Divide_Nothing()\n{\n    Assert.True(true);\n}\n");
        _backend.Enqueue(Members(Three));
        byte[]? beforeSecond = null;
        _backend.BeforeRespond = _ =>
        {
            if (_backend.Requests.Count == 2) beforeSecond = File.ReadAllBytes(TestPath);
            return Task.CompletedTask;
        };

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        Assert.Equal(2, _backend.Requests.Count);
        Assert.Contains("TEST REJECTED", Prompt(1));
        Assert.Equal(testBytes, beforeSecond);
        Assert.Equal(4, _testRuns); // the rejected attempt never ran the tests
    }

    [Fact]
    public async Task FailsOnCurrent_SecondAttemptFixes_Exit0()
    {
        _passes = (name, mutated, _) => !mutated && !name.Contains("Wrong", StringComparison.Ordinal);
        _backend.Enqueue(Members("Divide_Six_Two", "Divide_Wrong_Value"));
        _backend.Enqueue(Members("Divide_Six_Two", "Divide_Right_Value"));

        var exit = await Run(Req(CalcPath, count: 2));

        Assert.Equal(0, exit);
        Assert.Contains("TESTS FAIL ON CURRENT CODE", Prompt(1));
        Assert.Contains("Divide_Wrong_Value", Prompt(1));
        var test = File.ReadAllText(TestPath);
        Assert.DoesNotContain("Divide_Wrong_Value", test);
        Assert.Contains("Divide_Right_Value", test);
        // The second attempt inserted into the original file again, not into the first attempt's output.
        Assert.Single(Regex.Matches(test, "Divide_Six_Two"));
        Assert.False(PendingExists());
    }

    [Fact]
    public async Task Survivor_Retries_ThenExhausted_Exit1_Restored()
    {
        var calcBytes = File.ReadAllBytes(CalcPath);
        var testBytes = File.ReadAllBytes(TestPath);
        _passes = (name, mutated, _) => !mutated || name == "Divide_Survives";
        _backend.Enqueue(Members("Divide_Six_Two", "Divide_Survives"));
        _backend.Enqueue(Members("Divide_Six_Two", "Divide_Survives"));

        var exit = await Run(Req(CalcPath, count: 2, maxRetries: 1));

        Assert.Equal(1, exit);
        Assert.Equal(2, _backend.Requests.Count);
        Assert.Contains("TESTS SURVIVE MUTATION", Prompt(1));
        Assert.Equal(calcBytes, File.ReadAllBytes(CalcPath));
        Assert.Equal(testBytes, File.ReadAllBytes(TestPath));
        Assert.False(PendingExists());
        Assert.Contains(_ui.Statuses, s => s.Message.Contains("변이 검출 1/2 ✗", StringComparison.Ordinal));
        Assert.Equal("CharTestSurvivesMutation", Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task MutationUnbuildable_Exit4_NoRetry_Restored()
    {
        var calcBytes = File.ReadAllBytes(CalcPath);
        var testBytes = File.ReadAllBytes(TestPath);
        _buildError = (project, mutated) => mutated
            ? $"{CalcPath}(5,16): error CS0161: 'Calc.Divide(int, int)': not all code paths return a value [{CsprojPath}]"
            : null;
        _backend.Enqueue(Members(Three));
        _backend.Enqueue(Members(Three));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Single(_backend.Requests);
        Assert.Equal(calcBytes, File.ReadAllBytes(CalcPath));
        Assert.Equal(testBytes, File.ReadAllBytes(TestPath));
        Assert.False(PendingExists());
        Assert.Equal("CharTestMutationUnbuildable", Summary().GetProperty("failureKind").GetString());
        Assert.Contains(_ui.Warnings, w => w.Contains("CS0161", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Reject_RestoresTestFileBytes()
    {
        var crlfBom = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(TestSource.Replace("\n", "\r\n"))).ToArray();
        File.WriteAllBytes(TestPath, crlfBom);
        _ui.IsInteractive = true;
        _ui.ConfirmAnswers.Enqueue(false);
        _backend.Enqueue(Members(Three));
        byte[]? written = null;
        _onTestRun = _ => { if (_testRuns == 1) written = File.ReadAllBytes(TestPath); }; // the first run after the write

        var exit = await Run(Req(CalcPath, yes: false));

        Assert.Equal(2, exit);
        Assert.Equal(crlfBom, File.ReadAllBytes(TestPath));
        // While judged, the written file kept the BOM and CRLF line breaks.
        Assert.NotNull(written);
        Assert.Equal(Encoding.UTF8.GetPreamble(), written![..3]);
        var text = Encoding.UTF8.GetString(written[3..]);
        Assert.Contains("Divide_Six_Two", text);
        Assert.DoesNotMatch("[^\r]\n", text);
        Assert.False(PendingExists());
    }

    [Fact]
    public async Task NewFile_Rejected_FileDeleted_CsprojRestored()
    {
        _isWindows = () => true;
        var msbuild = Path.Combine(_root, "msbuild.exe");
        File.WriteAllText(msbuild, "");
        _options.Build.MsbuildPath = msbuild;
        _options.Test.Command = "vstest.console.exe {dll} /logger:trx;LogFileName={trx}";
        File.WriteAllText(CsprojPath, LegacyCsproj);
        File.WriteAllText(TestCsprojPath, LegacyTestCsproj);
        File.WriteAllText(Path.Combine(_root, "Demo.Tests", "packages.config"),
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<packages>\n  <package id=\"MSTest.TestFramework\" version=\"2.2.10\" />\n</packages>\n");
        File.Delete(TestPath);
        var csprojBytes = File.ReadAllBytes(TestCsprojPath);
        var calcBytes = File.ReadAllBytes(CalcPath);
        _ui.IsInteractive = true;
        _ui.ConfirmAnswers.Enqueue(false);
        _backend.Enqueue("### MEMBERS\n" + string.Join("\n", Three.Select(n =>
            $"[TestMethod]\npublic void {n}()\n{{\n    var result = new Demo.Calc().Divide(6, 2);\n    Assert.AreEqual(3, result);\n}}\n")));
        string? csprojDuringRun = null;
        _onTestRun = _ => { if (_testRuns == 1) csprojDuringRun = File.ReadAllText(TestCsprojPath); };

        var exit = await Run(Req(CalcPath, yes: false));

        Assert.Equal(2, exit);
        Assert.False(File.Exists(TestPath));
        Assert.Equal(csprojBytes, File.ReadAllBytes(TestCsprojPath));
        Assert.Equal(calcBytes, File.ReadAllBytes(CalcPath));
        Assert.Contains("<Compile Include=\"CalcTests.cs\" />", csprojDuringRun);
        Assert.Contains(_ui.Diffs, d => d.Path == TestCsprojPath);
        Assert.Contains(_ui.Diffs, d => d.Path == TestPath && d.After.Contains("[TestClass]", StringComparison.Ordinal));
        Assert.False(PendingExists());
        Assert.True(Summary().GetProperty("charTest").GetProperty("newFile").GetBoolean());
    }

    [Fact]
    public async Task DryRun_Exit0_Restored()
    {
        var testBytes = File.ReadAllBytes(TestPath);
        var calcBytes = File.ReadAllBytes(CalcPath);
        _backend.Enqueue(Members(Three));

        var exit = await Run(Req(CalcPath, dryRun: true));

        Assert.Equal(0, exit);
        Assert.Equal(testBytes, File.ReadAllBytes(TestPath));
        Assert.Equal(calcBytes, File.ReadAllBytes(CalcPath));
        Assert.Contains(_ui.Diffs, d => d.Path == TestPath && d.After.Contains("Divide_Six_Two", StringComparison.Ordinal));
        Assert.Equal(4, _testRuns);
        Assert.False(PendingExists());
        Assert.Equal(0, Summary().GetProperty("charTest").GetProperty("kept").GetInt32());
    }

    [Fact]
    public async Task CancelDuringMutation_RestoresTarget()
    {
        var calcBytes = File.ReadAllBytes(CalcPath);
        var testBytes = File.ReadAllBytes(TestPath);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        string? calcDuringMutation = null;
        _onTestRun = mutated =>
        {
            if (!mutated) return;
            calcDuringMutation = File.ReadAllText(CalcPath);
            cts.Cancel();
        };
        _backend.Enqueue(Members(Three));

        var exit = await RunAsync(Req(CalcPath), cts.Token);

        Assert.Equal(2, exit);
        Assert.Contains("pelly-mutation", calcDuringMutation);
        Assert.Equal(calcBytes, File.ReadAllBytes(CalcPath));
        Assert.Equal(testBytes, File.ReadAllBytes(TestPath));
        Assert.False(PendingExists());
    }

    [Fact]
    public async Task NoTestProject_Exit3()
    {
        WriteSln();

        var exit = await Run(Req(CalcPath));

        Assert.Equal(3, exit);
        Assert.Empty(_backend.Requests);
        Assert.Contains(_ui.Warnings, w => w.Contains("테스트 프로젝트가 없습니다", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoTestProjects_NoOption_Exit3()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Demo.IntegrationTests"));
        File.WriteAllText(Path.Combine(_root, "Demo.IntegrationTests", "Demo.IntegrationTests.csproj"), SdkTestCsproj);
        WriteSln("Demo.Tests", "Demo.IntegrationTests");

        var exit = await Run(Req(CalcPath));

        Assert.Equal(3, exit);
        Assert.Empty(_backend.Requests);
        Assert.Contains(_ui.Warnings, w => w.Contains("--test-project", StringComparison.Ordinal) && w.Contains("Demo.IntegrationTests", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwoTestProjects_WithOption_UsesIt()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Demo.IntegrationTests"));
        File.WriteAllText(Path.Combine(_root, "Demo.IntegrationTests", "Demo.IntegrationTests.csproj"), SdkTestCsproj);
        WriteSln("Demo.Tests", "Demo.IntegrationTests");
        _backend.Enqueue(Members(Three));

        var exit = await Run(Req(CalcPath, testProject: TestCsprojPath));

        Assert.Equal(0, exit);
        Assert.Contains("Divide_Six_Two", File.ReadAllText(TestPath));
    }

    [Fact]
    public async Task OtherTestingPlatform_Exit4()
    {
        File.WriteAllText(TestCsprojPath, SdkTestCsproj.Replace("<ItemGroup>",
            "<PropertyGroup>\n    <IsTestingPlatformApplication>true</IsTestingPlatformApplication>\n  </PropertyGroup>\n  <ItemGroup>", StringComparison.Ordinal));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.Contains(_ui.Warnings, w => w.Contains("테스트별 결과를 읽을 수 없어", StringComparison.Ordinal));
    }

    [Fact]
    public async Task BaselineUnusable_Exit4_ModelNotCalled()
    {
        _buildError = (project, _) => project == TestCsprojPath
            ? $"{TestPath}(10,9): error CS0103: The name 'Missing' does not exist in the current context [{TestCsprojPath}]"
            : null;

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.Equal("CharTestBaselineUnusable", Summary().GetProperty("failureKind").GetString());
    }

    [Fact]
    public async Task PrivateTarget_Exit3()
    {
        var exit = await Run(Req(CalcPath, method: "Hidden"));

        Assert.Equal(3, exit);
        Assert.Empty(_backend.Requests);
        Assert.Contains(_ui.Warnings, w => w.Contains("부를 수 없는 메서드", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Summary_HasCharTestBlockAndKind()
    {
        _backend.Enqueue(Members(Three));

        var exit = await Run(Req(CalcPath, verbose: true));

        Assert.Equal(0, exit);
        var summary = Summary();
        Assert.Equal("CSharpTest", summary.GetProperty("kind").GetString());
        Assert.Equal("cs-test-v1", summary.GetProperty("promptVersion").GetString());
        var block = summary.GetProperty("charTest");
        Assert.Equal(3, block.GetProperty("kept").GetInt32());
        Assert.Equal(3, block.GetProperty("requested").GetInt32());
        Assert.Equal(3, block.GetProperty("generated").GetInt32());
        Assert.Equal(3, block.GetProperty("passedOnCurrent").GetInt32());
        Assert.True(block.GetProperty("stableOnRerun").GetBoolean());
        Assert.Equal(3, block.GetProperty("killedByMutation").GetInt32());
        Assert.Equal("XunitV2", block.GetProperty("framework").GetString());
        Assert.False(block.GetProperty("newFile").GetBoolean());
        var shown = Assert.Single(_ui.RunSummaries);
        Assert.Equal(3, shown.Summary.CharTest!.Kept);
    }

    [Fact]
    public async Task Marker_DuringMutation_RecordsTarget()
    {
        string? marker = null;
        _onTestRun = mutated =>
        {
            if (!mutated) return;
            var file = Directory.EnumerateFiles(Path.Combine(_root, ".pelly", "runs"), CharTestPending.FileName, SearchOption.AllDirectories).Single();
            marker = File.ReadAllText(file);
        };
        _backend.Enqueue(Members(Three));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
        var files = JsonSerializer.Deserialize<List<PendingFile>>(marker!)!;
        var target = Assert.Single(files, f => f.Role == "target");
        Assert.True(target.Mutated);
        Assert.Equal(CalcPath, target.Path);
        Assert.True(File.Exists(target.BackupPath));
        Assert.Contains(files, f => f.Role == "test" && f.Path == TestPath && f.BackupPath is not null);
        Assert.False(PendingExists());
    }

    [Fact]
    public async Task ExternalChangeDuringMutation_Exit4_MarkerKept()
    {
        _onTestRun = mutated =>
        {
            if (mutated) File.AppendAllText(CalcPath, "// edited in the IDE\n");
        };
        _backend.Enqueue(Members(Three));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.True(PendingExists());
        Assert.Contains("edited in the IDE", File.ReadAllText(CalcPath)); // never overwritten
        Assert.Contains(_ui.Warnings, w => w.Contains("미완료 표시", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyOffWindows_Exit4()
    {
        _isWindows = () => false;
        File.WriteAllText(CsprojPath, LegacyCsproj);
        File.WriteAllText(TestCsprojPath, LegacyTestCsproj);

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.Contains(_ui.Warnings, w => w.Contains("레거시 테스트는 Windows에서만 실행합니다", StringComparison.Ordinal));
        Assert.DoesNotContain(_ui.Warnings, w => w.Contains("건너뜁니다", StringComparison.Ordinal));
    }

    [Fact]
    public async Task LegacyCommandWithoutTrx_Exit4_BeforeBaseline()
    {
        _isWindows = () => true;
        var msbuild = Path.Combine(_root, "msbuild.exe");
        File.WriteAllText(msbuild, "");
        _options.Build.MsbuildPath = msbuild;
        _options.Test.Command = "vstest.console.exe {dll}";
        File.WriteAllText(CsprojPath, LegacyCsproj);
        File.WriteAllText(TestCsprojPath, LegacyTestCsproj);

        var exit = await Run(Req(CalcPath));

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.Contains(_ui.Warnings, w => w.Contains("테스트별 결과를 읽을 수 없어", StringComparison.Ordinal));
    }

    [Fact]
    public async Task InternalTarget_WithInternalsVisibleTo_Proceeds()
    {
        File.WriteAllText(CalcPath, CalcSource.Replace("public int Divide", "internal int Divide", StringComparison.Ordinal));
        File.WriteAllText(CsprojPath, SdkCsproj.Replace("</Project>",
            "  <ItemGroup>\n    <InternalsVisibleTo Include=\"Demo.Tests\" />\n  </ItemGroup>\n</Project>", StringComparison.Ordinal));
        _backend.Enqueue(Members(Three));

        var exit = await Run(Req(CalcPath));

        Assert.Equal(0, exit);
    }

    [Fact]
    public async Task HintTooLong_Exit4_ModelNotCalled()
    {
        var request = Req(CalcPath) with { Hint = string.Join(" ", Enumerable.Repeat("refund cancel length", 200)) };

        var exit = await Run(request);

        Assert.Equal(4, exit);
        Assert.Empty(_backend.Requests);
        Assert.Contains(_ui.Warnings, w => w.Contains("힌트가 너무 깁니다", StringComparison.Ordinal));
    }
}
