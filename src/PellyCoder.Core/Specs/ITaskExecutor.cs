using PellyCoder.Core.Fsm;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Specs;

/// <summary>
/// Per-run options <c>pelly apply</c> hands to every task. <paramref name="Group"/> runs the task in group mode;
/// <paramref name="MemberOverride"/> names the target member instead of the spec's symbol (a group re-run after a rename).
/// </summary>
public sealed record TaskRunOptions(bool Yes, bool DryRun, int? MaxRetries, bool Verbose, GroupRun? Group = null, string? MemberOverride = null);

/// <summary>Runs one spec task and reports the detailed outcome.</summary>
public interface ITaskExecutor
{
    Task<RunResult> ExecuteAsync(SpecTask task, Workspace ws, TaskRunOptions o, CancellationToken ct);
}
