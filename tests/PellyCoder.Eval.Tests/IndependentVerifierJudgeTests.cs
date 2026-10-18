using System.IO.Abstractions;
using System.Net;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

/// <summary>
/// The behaviour judge of <see cref="IndependentVerifier"/> on a throwaway eval root and work copy, with a scripted
/// process runner: builds and restores succeed, each <c>dotnet run … -result-trx</c> writes a TRX with the given outcomes.
/// </summary>
public sealed class IndependentVerifierJudgeTests : IDisposable
{
    private const string JudgeTest = "Shop.Api.JudgeTests.OrderServiceJudgeTests.CancelAsync_WhenShipped_Throws";
    private const string PinnedTest = "Shop.Api.Tests.OrderServiceTests.CancelAsync_WhenShipped_CurrentlyCancels";
    private const string XunitV3 = """<Project Sdk="Microsoft.NET.Sdk"><ItemGroup><PackageReference Include="xunit.v3.mtp-v2" Version="4.0.2" /></ItemGroup></Project>""";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-eval-judge-" + Guid.NewGuid().ToString("N")[..8]);
    private readonly string _evalRoot;
    private readonly string _workRoot;
    private readonly string _corpusRoot;
    private readonly string _file;
    private readonly FakeProcesses _procs = new();

    public IndependentVerifierJudgeTests()
    {
        _evalRoot = Path.Combine(_root, "eval");
        Write(Path.Combine(_evalRoot, "benchmark", "judges", "Modern", "Shop.Api.JudgeTests", "Shop.Api.JudgeTests.csproj"), XunitV3);
        Write(Path.Combine(_evalRoot, "benchmark", "judges", "Modern", "Shop.Api.JudgeTests", "JudgeFakes.cs"), "// fakes");

        _workRoot = Path.Combine(_root, "out", "work", "cs-modern-901");
        _corpusRoot = Path.Combine(_workRoot, "Modern");
        Write(Path.Combine(_corpusRoot, "Shop.Api", "Shop.Api.csproj"), """<Project Sdk="Microsoft.NET.Sdk" />""");
        _file = Path.Combine(_corpusRoot, "Shop.Api", "Services", "OrderService.cs");
        Write(_file, "namespace Shop.Api.Services { class OrderService { } }");
        Write(Path.Combine(_corpusRoot, "Shop.Api.Tests", "Shop.Api.Tests.csproj"), XunitV3);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp folder, best effort */ }
    }

    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static BenchmarkTask Task(List<string>? tests, List<string>? failing) => new()
    {
        Id = "cs-modern-901", Kind = "cs", File = "corpus/Modern/Shop.Api/Services/OrderService.cs", Method = "CancelAsync", Request = "r",
        Judge = new BenchmarkJudge { Build = true, Tests = tests, FailingTests = failing },
    };

    private Task<VerificationResult> VerifyAsync(BenchmarkTask task) =>
        new IndependentVerifier(new FileSystem(), _procs, new PellyOptions(), null, () => false, _evalRoot)
            .VerifyCsAsync(task, _file, _corpusRoot, TestContext.Current.CancellationToken);

    [Fact]
    public async Task Judge_TestsPassAndFailingTestsFail_Passed()
    {
        _procs.Outcomes["Shop.Api.JudgeTests"] = [(JudgeTest, "Passed")];
        _procs.Outcomes["Shop.Api.Tests"] = [(PinnedTest, "Failed"), ("Shop.Api.Tests.OrderServiceTests.Other", "Passed")];

        var result = await VerifyAsync(Task([JudgeTest], [PinnedTest]));

        Assert.Equal(VerificationResult.Passed, result.Status);
        Assert.Contains("판정 테스트 통과 1건", result.Detail);
        Assert.Contains("수정 전 동작 테스트 실패 확인 1건", result.Detail);
    }

    [Fact]
    public async Task Judge_ListedTestMissingFromTrx_Failed()
    {
        _procs.Outcomes["Shop.Api.JudgeTests"] = [("Shop.Api.JudgeTests.OrderServiceJudgeTests.Something_Else", "Passed")];
        _procs.Outcomes["Shop.Api.Tests"] = [("Shop.Api.Tests.OrderServiceTests.Other", "Passed")];

        var tests = await VerifyAsync(Task([JudgeTest], null));
        var failing = await VerifyAsync(Task(null, [PinnedTest]));

        Assert.Equal(VerificationResult.Failed, tests.Status);
        Assert.Contains("결과에 없음", tests.Detail);
        Assert.Contains(JudgeTest, tests.Detail);
        Assert.Equal(VerificationResult.Failed, failing.Status);
        Assert.Contains("결과에 없음", failing.Detail);
        Assert.Contains(PinnedTest, failing.Detail);
    }

    [Fact]
    public async Task Judge_FailingTestStillPasses_Failed()
    {
        _procs.Outcomes["Shop.Api.JudgeTests"] = [(JudgeTest, "Passed")];
        _procs.Outcomes["Shop.Api.Tests"] = [(PinnedTest, "Passed")];

        var result = await VerifyAsync(Task([JudgeTest], [PinnedTest]));

        Assert.Equal(VerificationResult.Failed, result.Status);
        Assert.Contains("수정 전 동작 테스트가 여전히 통과", result.Detail);
        Assert.Contains(PinnedTest, result.Detail);
    }

    [Fact]
    public async Task Judge_JudgeTestFails_FailedWithMessage()
    {
        _procs.Outcomes["Shop.Api.JudgeTests"] = [(JudgeTest, "Failed")];

        var result = await VerifyAsync(Task([JudgeTest], null));

        Assert.Equal(VerificationResult.Failed, result.Status);
        Assert.Contains("판정 테스트 실패", result.Detail);
        Assert.Contains("boom", result.Detail);
    }

    [Fact]
    public async Task Copy_JudgeProjectNextToWork_TargetRootPassed()
    {
        _procs.Outcomes["Shop.Api.JudgeTests"] = [(JudgeTest, "Passed")];

        var result = await VerifyAsync(Task([JudgeTest], null));

        Assert.Equal(VerificationResult.Passed, result.Status);
        var copied = Path.Combine(_workRoot, "judges", "Modern", "Shop.Api.JudgeTests", "Shop.Api.JudgeTests.csproj");
        Assert.True(File.Exists(copied));
        Assert.True(File.Exists(Path.Combine(_workRoot, "judges", "Modern", "Shop.Api.JudgeTests", "JudgeFakes.cs")));
        var property = "-p:JudgeTargetRoot=" + _corpusRoot;
        var judgeCalls = _procs.Calls.Where(c => c.Arguments.Contains(copied)).ToList();
        Assert.Contains(judgeCalls, c => c.Arguments[0] == "restore" && c.Arguments.Contains(property));
        Assert.Contains(judgeCalls, c => c.Arguments[0] == "build" && c.Arguments.Contains(property));
        Assert.Contains(_procs.Calls, c => c.Arguments.Take(3).SequenceEqual(["run", "--project", copied]));
        // The original judge project is never built in place.
        Assert.DoesNotContain(_procs.Calls, c => c.Arguments.Any(a => a.StartsWith(Path.Combine(_evalRoot, "benchmark"), StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Judge_NoTestsListed_DoesNotCopyOrRun()
    {
        var result = await VerifyAsync(Task(null, null));

        Assert.Equal(VerificationResult.Passed, result.Status);
        Assert.False(Directory.Exists(Path.Combine(_workRoot, "judges")));
        Assert.DoesNotContain(_procs.Calls, c => c.Arguments[0] is "run" or "restore");
    }

    /// <summary>Every process succeeds; a <c>dotnet run --project P … -result-trx T</c> writes T from <see cref="Outcomes"/>[name of P].</summary>
    private sealed class FakeProcesses : IProcessRunner
    {
        public Dictionary<string, List<(string Name, string Outcome)>> Outcomes { get; } = [];
        public List<ProcessSpec> Calls { get; } = [];

        public Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
        {
            Calls.Add(spec);
            var args = spec.Arguments;
            if (args.Count > 0 && args[0] == "run" && args.ToList().IndexOf("-result-trx") is var i and >= 0)
            {
                var project = Path.GetFileNameWithoutExtension(args[2]);
                var sb = new StringBuilder("<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\"><Results>");
                foreach (var (name, outcome) in Outcomes.GetValueOrDefault(project) ?? [])
                    sb.Append($"<UnitTestResult testName=\"{WebUtility.HtmlEncode(name)}\" outcome=\"{outcome}\">"
                        + (outcome == "Passed" ? "" : "<Output><ErrorInfo><Message>boom</Message></ErrorInfo></Output>") + "</UnitTestResult>");
                sb.Append("</Results></TestRun>");
                File.WriteAllText(args[i + 1], sb.ToString());
            }
            return System.Threading.Tasks.Task.FromResult(new ProcessResult(0, "", "", [], TimeSpan.FromMilliseconds(1), false));
        }
    }
}
