using PellyCoder.Cli;
using PellyCoder.Core.Config;
using PellyCoder.Core.Fsm;

namespace PellyCoder.Cli.Tests;

public class RunnerTests
{
    private sealed class DetailedOnlyRunner(int exitCode) : IRunner
    {
        public RunRequest? Request;

        public Task<RunResult> RunDetailedAsync(RunRequest request, PellyOptions options, CancellationToken ct)
        {
            Request = request;
            return Task.FromResult(new RunResult(exitCode, "run-1", request.FilePath, null, null, "SyntaxErrors"));
        }
    }

    [Fact]
    public async Task RunAsync_returns_the_exit_code_of_the_detailed_run()
    {
        var runner = new DetailedOnlyRunner(1);
        var request = new RunRequest("/w/A.cs", null, "M", null, "do it", true, false, null);

        var exit = await ((IRunner)runner).RunAsync(request, new PellyOptions(), TestContext.Current.CancellationToken);

        Assert.Equal(1, exit);
        Assert.Same(request, runner.Request);
    }
}
