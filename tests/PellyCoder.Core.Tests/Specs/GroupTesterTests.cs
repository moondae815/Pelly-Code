using System.IO.Abstractions;
using System.Security;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Tests.Fakes;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Specs;

/// <summary><see cref="GroupTester"/> over scripted builds and test runs that write TRX reports (V2-2 §5.3 compile RED, §7.3).</summary>
public sealed class GroupTesterTests : IDisposable
{
    private const string SdkProject = "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>";

    // Cancel_works spans lines 5-9, Other_works 11-12, Old_behavior 14-15.
    private const string OrderTests =
        "namespace App.Tests;\n" +
        "\n" +
        "public class OrderTests\n" +
        "{\n" +
        "    [Fact]\n" +
        "    public void Cancel_works()\n" +
        "    {\n" +
        "        new App.Order().Cancel(true);\n" +
        "    }\n" +
        "\n" +
        "    [Fact]\n" +
        "    public void Other_works() { }\n" +
        "\n" +
        "    [Fact]\n" +
        "    public void Old_behavior() { }\n" +
        "}\n";

    private const string ThingTests =
        "namespace Other.Tests;\n\npublic class ThingTests\n{\n    [Fact]\n    public void Thing_works() { }\n}\n";

    private const string Cancel = "OrderTests.Cancel_works";
    private const string CancelFull = "App.Tests.OrderTests.Cancel_works";
    private const string OtherFull = "App.Tests.OrderTests.Other_works";
    private const string OldFull = "App.Tests.OrderTests.Old_behavior";

    private readonly string _root = Directory.CreateTempSubdirectory("pelly-group-tester-").FullName;
    private readonly FileSystem _fs = new();
    private readonly ScriptedProcessRunner _processes = new();
    private readonly TestOptions _testOptions = new();
    private bool _windows = true;

    public GroupTesterTests()
    {
        Make("src/App/App.csproj", SdkProject);
        Make("src/App/Order.cs", "namespace App;\n\npublic class Order { public void Cancel() { } }\n");
        Make("tests/App.Tests/App.Tests.csproj", SdkProject);
        Make("tests/App.Tests/OrderTests.cs", OrderTests);
        Make("tests/Other.Tests/Other.Tests.csproj", SdkProject);
        Make("tests/Other.Tests/ThingTests.cs", ThingTests);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (IOException) { }
    }

    private string P(string rel) => Path.GetFullPath(Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar)));

    private void Make(string rel, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(P(rel))!);
        File.WriteAllText(P(rel), text, new UTF8Encoding(false));
    }

    private Workspace Ws => new(_root, Path.Combine(_root, ".pelly"));

    private string App => P("src/App/App.csproj");
    private string AppTests => P("tests/App.Tests/App.Tests.csproj");
    private string OtherTests => P("tests/Other.Tests/Other.Tests.csproj");

    private GroupTester Tester()
    {
        var build = new BuildOptions();
        return new GroupTester(_fs, new TestRunner(_processes, build, _testOptions),
            new BuildToolResolver(_fs, _processes, build, () => _windows), _testOptions, () => _windows);
    }

    private GroupTestPlan Plan(string[] tests, string[]? red = null, string[]? green = null, string[]? breaks = null) =>
        new(tests, red ?? tests, green ?? tests, breaks ?? [])
        {
            ReportPrefix = Path.Combine(_root, ".pelly", "runs", "apply-1", "group-tests", "G1"),
        };

    private static ProcessResult Result(int exit, params string[] lines)
    {
        var stdout = string.Join("\n", lines);
        return new ProcessResult(exit, stdout, "", Encoding.UTF8.GetBytes(stdout), TimeSpan.FromSeconds(1), false);
    }

    private string BuildError(string file, int line, string code = "CS1501") =>
        $"{P(file)}({line},13): error {code}: No overload for method 'Cancel' takes 1 arguments [{AppTests}]";

    private static string Trx(params (string Name, bool Passed)[] results)
    {
        var sb = new StringBuilder("<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>");
        foreach (var (name, passed) in results)
        {
            sb.Append($"<UnitTestResult testName=\"{SecurityElement.Escape(name)}\" outcome=\"{(passed ? "Passed" : "Failed")}\"");
            sb.Append(passed ? " />" : "><Output><ErrorInfo><Message>Assert.True() Failure</Message></ErrorInfo></Output></UnitTestResult>");
        }
        sb.Append($"</Results><ResultSummary outcome=\"{(results.All(r => r.Passed) ? "Completed" : "Failed")}\" /></TestRun>");
        return sb.ToString();
    }

    /// <summary>One project's test run: its build (clean, or the given error lines), then, when it built, a TRX report.</summary>
    private void Script(string? trx, params string[] buildErrors)
    {
        _processes.Enqueue(buildErrors.Length == 0 ? Result(0) : Result(1, buildErrors));
        if (buildErrors.Length > 0 || trx is null) return;
        _processes.Enqueue((spec, _) =>
        {
            const string Logger = "trx;LogFileName=";
            var path = spec.Arguments.Single(a => a.StartsWith(Logger, StringComparison.Ordinal))[Logger.Length..];
            File.WriteAllText(path, trx);
            return Task.FromResult(Result(trx.Contains("outcome=\"Failed\"", StringComparison.Ordinal) ? 1 : 0));
        });
    }

    private Task<GroupTestBaseline> Baseline(GroupTester tester, IReadOnlyList<string> scope, GroupTestPlan plan) =>
        tester.BaselineAsync(Ws, scope, plan, TestContext.Current.CancellationToken);

    private Task<GroupTestVerdict> Verify(GroupTester tester, GroupTestBaseline baseline, GroupTestPlan plan) =>
        tester.VerifyAsync(Ws, baseline, plan, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Baseline_CompileRed_WhenErrorsInsideTest()
    {
        Script(null, BuildError("tests/App.Tests/OrderTests.cs", 8));

        var baseline = await Baseline(Tester(), [App, AppTests], Plan([Cancel]));

        Assert.Null(baseline.Unverifiable);
        Assert.Equal([AppTests], baseline.Projects);
        Assert.Equal([0], baseline.CompileRed);
        Assert.Equal(RedState.RedCompile, baseline.Red[Cancel]);
        Assert.Equal(CancelFull, Assert.Single(baseline.Tests).FullName);
        var notice = Assert.Single(baseline.Notices);
        Assert.StartsWith(GroupTester.StrictNotice + ": ", notice);
        Assert.EndsWith("App.Tests.csproj", notice);
        Assert.Single(_processes.Calls); // the build only: nothing to run
    }

    [Fact]
    public async Task Baseline_UnrelatedCompileError_Unverifiable()
    {
        Script(null, BuildError("tests/App.Tests/OrderTests.cs", 12));

        var baseline = await Baseline(Tester(), [App, AppTests], Plan([Cancel]));

        Assert.Contains("테스트 프로젝트의 컴파일 오류가 지정한 테스트 안에 없습니다", baseline.Unverifiable);
    }

    [Fact]
    public async Task Baseline_ProjectsAreScopeTestsPlusDeclaring()
    {
        Script(Trx((CancelFull, false), (OtherFull, true)));
        Script(Trx(("Other.Tests.ThingTests.Thing_works", false)));

        var baseline = await Baseline(Tester(), [App, AppTests], Plan([Cancel, "ThingTests.Thing_works"]));

        Assert.Null(baseline.Unverifiable);
        Assert.Equal([AppTests, OtherTests], baseline.Projects);
        Assert.Equal(RedState.Red, baseline.Red[Cancel]);
        Assert.Equal(RedState.Red, baseline.Red["ThingTests.Thing_works"]);
        Assert.Empty(baseline.CompileRed);
        Assert.Equal([0, 1], baseline.Tests.Select(t => t.ProjectIndex));
        // Distinct report paths under the apply's group-tests folder.
        var reports = _processes.Calls.Where(c => c.Arguments[0] == "test").Select(c => c.Arguments.Last(a => a.Contains(".trx"))).ToList();
        Assert.Equal(2, reports.Distinct().Count());
        Assert.All(reports, r => Assert.Contains(Path.Combine("apply-1", "group-tests", "G1-baseline-"), r));
    }

    [Fact]
    public async Task Baseline_TestProjectsSetting_IntersectsScope()
    {
        _testOptions.Projects.Add("src/App/App.csproj");
        Script(Trx(("App.SmokeTests.Runs", true)));
        Script(Trx((CancelFull, false)));

        var baseline = await Baseline(Tester(), [App, AppTests], Plan([Cancel]));

        Assert.Null(baseline.Unverifiable);
        Assert.Equal([App, AppTests], baseline.Projects); // App from test.projects, App.Tests declares the test
    }

    [Fact]
    public async Task Baseline_LegacyOffWindows_Unverifiable()
    {
        _windows = false;
        Make("tests/App.Tests/App.Tests.csproj",
            "<Project ToolsVersion=\"15.0\" xmlns=\"http://schemas.microsoft.com/developer/msbuild/2003\"></Project>");

        var baseline = await Baseline(Tester(), [App, AppTests], Plan([Cancel]));

        Assert.Equal("레거시 테스트는 Windows에서만 실행합니다 — 테스트 우선 검증을 할 수 없습니다", baseline.Unverifiable);
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public async Task Baseline_NameNotFound_Unverifiable()
    {
        var baseline = await Baseline(Tester(), [App, AppTests], Plan(["OrderTests.Missing"]));

        Assert.StartsWith("테스트 OrderTests.Missing을(를) 작업 공간에서 찾지 못했습니다", baseline.Unverifiable);
        Assert.Empty(_processes.Calls);
    }

    [Fact]
    public async Task Verify_CompileRedStillNotBuilding_IsTestBuildErrors()
    {
        var tester = Tester();
        var plan = Plan([Cancel]);
        Script(null, BuildError("tests/App.Tests/OrderTests.cs", 8));
        var baseline = await Baseline(tester, [AppTests], plan);
        Script(null, BuildError("tests/App.Tests/OrderTests.cs", 8));

        var verdict = await Verify(tester, baseline, plan);

        Assert.Null(verdict.Unverifiable);
        Assert.Equal("CS1501", Assert.Single(verdict.TestBuildErrors).Code);
        var (project, tests) = Assert.Single(verdict.Unbuilt);
        Assert.EndsWith("App.Tests.csproj", project);
        Assert.Equal([Cancel], tests);
        Assert.Empty(verdict.Green);
        Assert.Empty(verdict.Regressions);
    }

    [Fact]
    public async Task Verify_StrictRegressionInCompileRedProject()
    {
        var tester = Tester();
        var plan = Plan([Cancel]);
        Script(null, BuildError("tests/App.Tests/OrderTests.cs", 8));
        var baseline = await Baseline(tester, [AppTests], plan);
        Script(Trx((CancelFull, true), (OtherFull, false), (OldFull, true)));

        var verdict = await Verify(tester, baseline, plan);

        Assert.Null(verdict.Unverifiable);
        Assert.Empty(verdict.Green);
        Assert.Empty(verdict.TestBuildErrors);
        Assert.Equal(OtherFull, Assert.Single(verdict.Regressions).FullName);
    }

    [Fact]
    public async Task Verify_GreenFailure()
    {
        var tester = Tester();
        var plan = Plan([Cancel]);
        Script(Trx((CancelFull, false), (OtherFull, true)));
        var baseline = await Baseline(tester, [AppTests], plan);
        Script(Trx((CancelFull, false), (OtherFull, true)));

        var verdict = await Verify(tester, baseline, plan);

        Assert.Equal(CancelFull, Assert.Single(verdict.Green).FullName);
        Assert.Empty(verdict.Regressions);
        // Baseline and verify reports never share a path.
        var reports = _processes.Calls.Where(c => c.Arguments[0] == "test").Select(c => c.Arguments.Last(a => a.Contains(".trx"))).ToList();
        Assert.Equal(2, reports.Distinct().Count());
    }

    [Fact]
    public async Task Verify_BreaksExcluded()
    {
        var tester = Tester();
        var plan = Plan([Cancel], breaks: ["OrderTests.Old_behavior"]);
        Script(Trx((CancelFull, false), (OtherFull, true), (OldFull, true)));
        var baseline = await Baseline(tester, [AppTests], plan);
        Script(Trx((CancelFull, true), (OtherFull, true), (OldFull, false)));

        var verdict = await Verify(tester, baseline, plan);

        Assert.Null(verdict.Unverifiable);
        Assert.Empty(verdict.Green);
        Assert.Empty(verdict.Regressions);
        Assert.Equal(1, verdict.BreaksExcluded);
    }

    [Fact]
    public async Task Verify_BreaksExcluded_Names()
    {
        var tester = Tester();
        var plan = Plan([Cancel], breaks: ["OrderTests.Old_behavior"]);
        Script(Trx((CancelFull, false), (OtherFull, true), (OldFull, true)));
        var baseline = await Baseline(tester, [AppTests], plan);
        Script(Trx((CancelFull, true), (OtherFull, true), (OldFull, false)));

        var verdict = await Verify(tester, baseline, plan);

        Assert.Equal([OldFull], verdict.ExcludedNames);
    }

    [Fact]
    public async Task Verify_RedOnlyTestBreaking_IsRegression()
    {
        var tester = Tester();
        // Other_works has the RED role only here (its criterion's GREEN comes in a later task): passing before, failing now is a regression.
        var plan = Plan([Cancel, "OrderTests.Other_works"], red: [Cancel, "OrderTests.Other_works"], green: [Cancel]);
        Script(Trx((CancelFull, false), (OtherFull, true)));
        var baseline = await Baseline(tester, [AppTests], plan);
        Script(Trx((CancelFull, true), (OtherFull, false)));

        var verdict = await Verify(tester, baseline, plan);

        Assert.Empty(verdict.Green);
        Assert.Equal(OtherFull, Assert.Single(verdict.Regressions).FullName);
    }

    [Fact]
    public async Task Verify_Regression()
    {
        var tester = Tester();
        var plan = Plan([Cancel]);
        Script(Trx((CancelFull, false), (OtherFull, true)));
        var baseline = await Baseline(tester, [AppTests], plan);
        Script(Trx((CancelFull, true), (OtherFull, false)));

        var verdict = await Verify(tester, baseline, plan);

        Assert.Empty(verdict.Green);
        Assert.Equal(OtherFull, Assert.Single(verdict.Regressions).FullName);
    }

    [Fact]
    public async Task Group_NoTestChange_Unchanged()
    {
        var tester = Tester();
        var plan = Plan([Cancel]);
        Script(Trx((CancelFull, false), (OtherFull, true)));
        var baseline = await Baseline(tester, [AppTests], plan);
        // A helper elsewhere in the test file is not part of the test's declaration.
        Make("tests/App.Tests/OrderTests.cs", OrderTests.Replace("    public void Old_behavior() { }\n", "    public void Old_behavior() { }\n\n    private static int Helper() => 1;\n"));
        Script(Trx((CancelFull, true), (OtherFull, true)));

        var verdict = await Verify(tester, baseline, plan);

        Assert.Null(verdict.Unverifiable);
        Assert.Empty(verdict.Green);
        Assert.Empty(verdict.Regressions);
        Assert.Null(verdict.ChangedTestFile);
    }

    [Fact]
    public async Task Verify_TestEditedDuringGroup_ChangedTestFile()
    {
        var tester = Tester();
        var plan = Plan([Cancel]);
        Script(Trx((CancelFull, false), (OtherFull, true)));
        var baseline = await Baseline(tester, [AppTests], plan);
        Make("tests/App.Tests/OrderTests.cs", OrderTests.Replace("new App.Order().Cancel(true);", "Assert.True(true);"));
        Script(Trx((CancelFull, true), (OtherFull, true)));

        var verdict = await Verify(tester, baseline, plan);

        Assert.Null(verdict.Unverifiable);
        Assert.Empty(verdict.Green);
        Assert.Equal(Path.Combine("tests", "App.Tests", "OrderTests.cs"), verdict.ChangedTestFile);
    }

    [Fact]
    public async Task Verify_TestEdited_GreenFailureFirst()
    {
        var tester = Tester();
        var plan = Plan([Cancel]);
        Script(Trx((CancelFull, false), (OtherFull, true)));
        var baseline = await Baseline(tester, [AppTests], plan);
        Make("tests/App.Tests/OrderTests.cs", OrderTests.Replace("new App.Order().Cancel(true);", "Assert.True(true);"));
        Script(Trx((CancelFull, false), (OtherFull, true)));

        var verdict = await Verify(tester, baseline, plan);

        Assert.Single(verdict.Green);
        Assert.Null(verdict.ChangedTestFile);
    }

    [Fact]
    public async Task Verify_ExpectedHashFromPlan_ComparedInsteadOfBaseline()
    {
        var tester = Tester();
        var original = TestDeclarationHash.Of(_fs, _root, Cancel)!;
        // Changed before the group (by the criterion's first linked task outside it): the baseline already sees the edit.
        Make("tests/App.Tests/OrderTests.cs", OrderTests.Replace("new App.Order().Cancel(true);", "Assert.True(true);"));
        var plan = Plan([Cancel], red: []) with { ExpectedHashes = new Dictionary<string, string> { [Cancel] = original } };
        Script(Trx((CancelFull, false), (OtherFull, true)));
        var baseline = await Baseline(tester, [AppTests], plan);
        Script(Trx((CancelFull, true), (OtherFull, true)));

        var verdict = await Verify(tester, baseline, plan);

        Assert.Equal(Path.Combine("tests", "App.Tests", "OrderTests.cs"), verdict.ChangedTestFile);
    }
}
