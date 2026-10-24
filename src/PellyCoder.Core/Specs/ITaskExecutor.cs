using PellyCoder.Core.Fsm;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Specs;

/// <summary>Per-run options <c>pelly apply</c> hands to every task.</summary>
public sealed record TaskRunOptions(bool Yes, bool DryRun, int? MaxRetries, bool Verbose);

/// <summary>Runs one spec task and reports the detailed outcome.</summary>
public interface ITaskExecutor
{
    Task<RunResult> ExecuteAsync(SpecTask task, Workspace ws, TaskRunOptions o, CancellationToken ct);
}
