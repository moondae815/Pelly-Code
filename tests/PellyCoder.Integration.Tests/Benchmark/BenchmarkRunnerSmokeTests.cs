using System.IO.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Processes;
using PellyCoder.Eval;
using PellyCoder.Integration.Tests.Corpus;
using PellyCoder.Integration.Tests.E2E;

namespace PellyCoder.Integration.Tests.Benchmark;

/// <summary>
/// The benchmark runner end to end without a model: a canned correct block for cs-modern-001, then the real copy, restore,
/// orchestrator run, build and independent re-verification. Runs in CI's unit jobs on macOS and Windows.
/// </summary>
public sealed class BenchmarkRunnerSmokeTests(ITestOutputHelper output) : IDisposable
{
    // Response text after the prefilled "<<<<<<< SEARCH\n" marker; the corpus .cs files are LF on every OS (.gitattributes).
    private const string ShippedGuard =
        "            // 배송 완료된 주문은 반품 절차를 따라야 한다.\n"
        + "            if (order.Status == OrderStatus.Delivered)\n"
        + "=======\n"
        + "            // 배송 중인 주문은 취소할 수 없다.\n"
        + "            if (order.Status == OrderStatus.Shipped)\n"
        + "            {\n"
        + "                throw new InvalidOperationException(\"배송 중인 주문은 취소할 수 없습니다.\");\n"
        + "            }\n"
        + "\n"
        + "            // 배송 완료된 주문은 반품 절차를 따라야 한다.\n"
        + "            if (order.Status == OrderStatus.Delivered)\n"
        + ">>>>>>> REPLACE\n";

    private readonly string _out = Path.Combine(Path.GetTempPath(), "pelly-bench-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_out, recursive: true); }
        catch (IOException) { /* temp folder, best effort: build servers may still hold files */ }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Runner_CannedBackend_CsModern001_Passes()
    {
        var evalRoot = Directory.GetParent(CorpusPaths.Root)!.FullName;
        var suite = BenchmarkSuite.Load(Path.Combine(evalRoot, "benchmark")).Suite!;
        var fs = new FileSystem();
        var task = suite.Resolve(BenchmarkTask.Load(Path.Combine(suite.TasksDir, "cs-modern-001.yaml")), fs, out var error);
        Assert.True(task is not null, error);
        var options = Assert.IsType<PellyOptions>(
            PellyOptionsLoader.Load(fs, null, null, new Dictionary<string, string?>(), new Dictionary<string, string?>()).Options);
        var backend = new CannedLlmBackend([ShippedGuard]);
        var log = new List<string>();
        var runner = new BenchmarkRunner(new RunnerSetup(fs, new SystemProcessRunner(TimeProvider.System), backend, options,
            TimeProvider.System, suite, Sql: null, SqlSkipReason: "no SQL in this test", log.Add));

        var result = await runner.RunAsync(task, _out, TestContext.Current.CancellationToken);

        foreach (var line in log) output.WriteLine(line);
        Assert.True(result.Status == VerificationResult.Passed, string.Join("; ", result.Reasons) + " / " + result.Independent);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(1, result.Attempts);
        Assert.Equal(VerificationResult.Passed, result.BuildCheck);
        Assert.Equal(VerificationResult.Passed, result.Independent!.Status);
        Assert.False(result.InvariantViolated);
        Assert.Equal(["Ok"], result.EditOutcomes);
        Assert.NotNull(result.WallSeconds);
        Assert.Single(backend.Requests);

        // The run happened in the copy; the corpus is untouched.
        var copied = File.ReadAllText(Path.Combine(_out, "work", task.Task.Id, "Modern", "Shop.Api", "Services", "OrderService.cs"));
        Assert.Contains("OrderStatus.Shipped)\n            {\n                throw", copied.Replace("\r\n", "\n", StringComparison.Ordinal), StringComparison.Ordinal);
        Assert.DoesNotContain("배송 중인 주문은 취소할 수 없습니다", File.ReadAllText(CorpusPaths.Get("Modern/Shop.Api/Services/OrderService.cs")), StringComparison.Ordinal);
    }
}
