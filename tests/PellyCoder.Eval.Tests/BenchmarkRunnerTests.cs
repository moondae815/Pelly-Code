using System.IO.Abstractions;
using System.Runtime.CompilerServices;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Processes;
using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

/// <summary>
/// The runner on a throwaway eval root with one plain .cs file (no csproj: syntax-only verification, no restore, no
/// build process), a canned model and a substitute independent verifier.
/// </summary>
public sealed class BenchmarkRunnerTests : IDisposable
{
    private const string Source = "namespace Demo;\n\npublic class Calc\n{\n    public int Add(int a, int b)\n    {\n        return a + b;\n    }\n}\n";
    private const string Edit = "        return a + b;\n=======\n        return checked(a + b);\n>>>>>>> REPLACE\n";

    private readonly string _root = Path.Combine(Path.GetTempPath(), "pelly-eval-runner-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { /* temp folder, best effort */ }
    }

    [Fact]
    public async Task Runner_VerifierThrows_KeepsExitCodeAndAttempts()
    {
        var suite = PlainSuite();
        var task = new BenchmarkTask
        {
            Id = "cs-modern-901", Kind = "cs", File = "Plain/Calc.cs", Method = "Add", Request = "오버플로를 검사해줘",
            Judge = new BenchmarkJudge { Build = true, MustContain = ["checked("] },
        };
        var fs = new FileSystem();
        var options = Assert.IsType<PellyOptions>(
            PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), new Dictionary<string, string?>()).Options);
        var runner = new BenchmarkRunner(new RunnerSetup(fs, new SystemProcessRunner(TimeProvider.System), new OneShotBackend(Edit),
            options, TimeProvider.System, suite, null, "no SQL", _ => { }, Verifier: new ThrowingVerifier()));

        var result = await runner.RunAsync(Resolve(suite, task, fs), Path.Combine(_root, "out"), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(["Ok"], result.EditOutcomes);
        Assert.Equal(VerificationResult.Skipped, result.Independent!.Status);
        Assert.Equal("독립 검증 오류: verifier exploded", result.Independent.Detail);
        Assert.False(result.InvariantViolated);
        Assert.Equal(VerificationResult.Passed, result.Status);
    }

    // Exit 0, the independent build passed, a behaviour judge failed: the model's miss (Judge), not an invariant violation.
    // A judge that could not run (tooling) is its own failure type; an independent build failure is the violation.
    [Theory]
    [InlineData("judge", "Judge", false)]
    [InlineData("tooling", "JudgeTooling", false)]
    [InlineData("build", "InvariantViolated", true)]
    public async Task Runner_FailureTypeFollowsTheBuildVerdict(string kind, string failureType, bool violation)
    {
        var suite = PlainSuite();
        var task = new BenchmarkTask
        {
            Id = "cs-modern-901", Kind = "cs", File = "Plain/Calc.cs", Method = "Add", Request = "오버플로를 검사해줘",
            Judge = new BenchmarkJudge { Build = true, Tests = ["Shop.Api.JudgeTests.T.cs_modern_901_X"] },
        };
        var verdict = kind switch
        {
            "judge" => new VerificationResult(VerificationResult.Failed, "판정 테스트 실패: x", VerificationResult.Passed),
            "tooling" => new VerificationResult(VerificationResult.Skipped, "판정 도구 오류: 복원 실패", VerificationResult.Passed),
            _ => VerificationResult.Fail("빌드 오류: CS0103"),
        };
        var fs = new FileSystem();
        var options = Assert.IsType<PellyOptions>(
            PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), new Dictionary<string, string?>()).Options);
        var runner = new BenchmarkRunner(new RunnerSetup(fs, new SystemProcessRunner(TimeProvider.System), new OneShotBackend(Edit),
            options, TimeProvider.System, suite, null, "no SQL", _ => { }, Verifier: new FixedVerifier(verdict)));

        var result = await runner.RunAsync(Resolve(suite, task, fs), Path.Combine(_root, "out"), TestContext.Current.CancellationToken);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(VerificationResult.Failed, result.Status);
        Assert.Equal(failureType, result.FailureType);
        Assert.Equal(violation, result.InvariantViolated);
    }

    [Fact]
    public async Task Runner_KeepWorkFailed_DeletesPassedTaskFolder_KeepsFailed()
    {
        var suite = PlainSuite();
        var fs = new FileSystem();
        var outDir = Path.Combine(_root, "out");
        var passed = await RunWithAsync(suite, fs, KeepWork.Failed, "cs-modern-901", VerificationResult.Pass, outDir);
        var failed = await RunWithAsync(suite, fs, KeepWork.Failed, "cs-modern-902", VerificationResult.Fail("빌드 오류: CS0103"), outDir);

        Assert.Equal(VerificationResult.Passed, passed.Status);
        Assert.Equal(VerificationResult.Failed, failed.Status);
        Assert.False(Directory.Exists(Path.Combine(outDir, "work", "cs-modern-901")));
        Assert.True(Directory.Exists(Path.Combine(outDir, "work", "cs-modern-902")));
        Assert.Equal(1, passed.Attempts);
    }

    [Fact]
    public async Task Runner_KeepWorkNone_DeletesBoth_AfterArtifactsAreRead()
    {
        var suite = PlainSuite();
        var fs = new FileSystem();
        var outDir = Path.Combine(_root, "out");
        var passed = await RunWithAsync(suite, fs, KeepWork.None, "cs-modern-901", VerificationResult.Pass, outDir);
        var failed = await RunWithAsync(suite, fs, KeepWork.None, "cs-modern-902", VerificationResult.Fail("빌드 오류: CS0103"), outDir);

        Assert.False(Directory.Exists(Path.Combine(outDir, "work", "cs-modern-901")));
        Assert.False(Directory.Exists(Path.Combine(outDir, "work", "cs-modern-902")));
        Assert.All(new[] { passed, failed }, r => { Assert.Equal(1, r.Attempts); Assert.NotNull(r.TtftSeconds); });
    }

    private async Task<TaskResult> RunWithAsync(BenchmarkSuite suite, IFileSystem fs, KeepWork keep, string id, VerificationResult verdict, string outDir)
    {
        var task = new BenchmarkTask
        {
            Id = id, Kind = "cs", File = "Plain/Calc.cs", Method = "Add", Request = "오버플로를 검사해줘",
            Judge = new BenchmarkJudge { Build = true, MustContain = ["checked("] },
        };
        var options = Assert.IsType<PellyOptions>(
            PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), new Dictionary<string, string?>()).Options);
        var runner = new BenchmarkRunner(new RunnerSetup(fs, new SystemProcessRunner(TimeProvider.System), new OneShotBackend(Edit),
            options, TimeProvider.System, suite, null, "no SQL", _ => { }, Verifier: new FixedVerifier(verdict), KeepWork: keep));
        return await runner.RunAsync(Resolve(suite, task, fs), outDir, TestContext.Current.CancellationToken);
    }

    /// <summary>A suite with one root <c>Plain</c> holding <c>Calc.cs</c>.</summary>
    private BenchmarkSuite PlainSuite()
    {
        var dir = Path.Combine(_root, "suite");
        Directory.CreateDirectory(Path.Combine(dir, "Plain"));
        File.WriteAllText(Path.Combine(dir, "Plain", "Calc.cs"), Source);
        File.WriteAllText(Path.Combine(dir, BenchmarkSuite.FileName),
            """{ "formatVersion": 1, "name": "plain", "roots": [ { "name": "Plain", "path": "Plain" } ] }""");
        var load = BenchmarkSuite.Load(dir);
        return load.Suite ?? throw new InvalidDataException(string.Join("; ", load.Errors));
    }

    private static SuiteTask Resolve(BenchmarkSuite suite, BenchmarkTask task, IFileSystem fs) =>
        suite.Resolve(task, fs, out var error) ?? throw new InvalidDataException(error);

    private sealed class FixedVerifier(VerificationResult verdict) : IIndependentVerifier
    {
        public Task<VerificationResult> VerifyCsAsync(BenchmarkTask task, string file, string corpusRoot, CancellationToken ct) => Task.FromResult(verdict);

        public Task<VerificationResult> VerifySqlAsync(BenchmarkTask task, string file, CancellationToken ct) => Task.FromResult(verdict);
    }

    private sealed class ThrowingVerifier : IIndependentVerifier
    {
        public Task<VerificationResult> VerifyCsAsync(BenchmarkTask task, string file, string corpusRoot, CancellationToken ct) =>
            throw new InvalidOperationException("verifier exploded");

        public Task<VerificationResult> VerifySqlAsync(BenchmarkTask task, string file, CancellationToken ct) =>
            throw new InvalidOperationException("verifier exploded");
    }

    /// <summary>Answers the first generate request with <paramref name="text"/>; any further request fails the run.</summary>
    private sealed class OneShotBackend(string text) : ILlmBackend
    {
        private int _calls;

        public async IAsyncEnumerable<LlmChunk> GenerateAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
        {
            if (Interlocked.Increment(ref _calls) > 1) throw new InvalidOperationException("only one response");
            await Task.Yield();
            yield return new LlmChunk(text, false, null, null, null, null, null);
            yield return new LlmChunk("", true, 100, 0, 10, TimeSpan.FromMilliseconds(1), "stop");
        }

        public Task<LlmServerStatus> GetStatusAsync(CancellationToken ct) =>
            Task.FromResult(new LlmServerStatus(true, "canned", true, true, "qwen3.5", TimeSpan.Zero, null));
    }
}
