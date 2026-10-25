using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Logging;

namespace PellyCoder.Core.Specs;

/// <summary>
/// Assembles the <see cref="SpecRunner"/> of one <c>pelly apply</c> from the resolved options: the one composition shared by
/// the CLI and the eval benchmark, so both run a spec with the same group build, feedback, retry and retention wiring.
/// </summary>
public static class SpecRunnerFactory
{
    /// <param name="run">Runs one task request (the v1 orchestrator in the CLI).</param>
    /// <param name="impact">The start-of-apply impact search; null runs none.</param>
    /// <param name="isWindows">Platform probe of the group build; defaults to <see cref="OperatingSystem.IsWindows"/>.</param>
    public static SpecRunner Create(IFileSystem fs, PellyOptions options, IUserInteraction ui, TimeProvider time, IProcessRunner processes,
        Func<RunRequest, CancellationToken, Task<RunResult>> run, IImpactFinder? impact, Func<bool>? isWindows = null)
    {
        var executor = new FsmTaskExecutor(run);
        var groups = new GroupVerifier(
            fs, new BuildVerifier(processes, options.Build), new BuildToolResolver(fs, processes, options.Build), isWindows ?? OperatingSystem.IsWindows);
        return new SpecRunner(
            fs, new FileGuard(fs, time), executor, ui, time, options.Sql.CompatLevel,
            (ws, id) => new RunLogger(fs, time).Prune(ws, options.Log.RetentionDays, id),
            groups, new FeedbackBuilder(QwenTokenCounter.Shared, options.Budget.Feedback), options.Retry.Max, impact);
    }
}
