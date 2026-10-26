using System.IO.Abstractions;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Specs;

/// <summary>
/// Turns a spec task into a <see cref="RunRequest"/> and runs it through the FSM as a host run. A cs task's linked tests become
/// a test-first run; a sql task never carries them.
/// </summary>
public sealed class FsmTaskExecutor(Func<RunRequest, CancellationToken, Task<RunResult>> run) : ITaskExecutor
{
    private static readonly IPath LocalPath = new FileSystem().Path;

    public Task<RunResult> ExecuteAsync(SpecTask task, Workspace ws, TaskRunOptions o, CancellationToken ct)
    {
        string file = SpecTaskPaths.FullPath(LocalPath, ws.Root, task.Path);
        var host = new ApplyHost(ws);

        RunRequest request;
        if (task.Kind == TaskKind.Sql)
        {
            request = new RunRequest(file, null, null, null, task.Request, o.Yes, o.DryRun, o.MaxRetries,
                ObjectName: task.Symbol, Kind: EditKind.Sql, Verbose: o.Verbose, Host: host);
        }
        else
        {
            string? cls = null, member = null;
            if (task.Symbol is { Length: > 0 } symbol) (cls, member) = SpecSymbol.Split(symbol);
            // Every checked name is on VerifyTests and in TestChecks: a name missing from TestChecks would get no role.
            var tests = o.Tests is { Checks.Count: > 0 } t ? t : null;
            request = new RunRequest(file, cls, o.MemberOverride ?? member, null, task.Request, o.Yes, o.DryRun, o.MaxRetries,
                Verbose: o.Verbose, Host: host, Group: o.Group,
                VerifyTests: tests?.Checks.Keys.ToList(), ExpectBreaks: tests is { Breaks.Count: > 0 } ? tests.Breaks : null,
                TestChecks: tests?.Checks, ExpectedTestHashes: tests?.ExpectedHashes);
        }
        return run(request, ct);
    }
}
