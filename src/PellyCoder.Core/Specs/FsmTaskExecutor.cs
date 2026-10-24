using System.IO.Abstractions;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Specs;

/// <summary>Turns a spec task into a <see cref="RunRequest"/> and runs it through the FSM as a host run.</summary>
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
            request = new RunRequest(file, cls, member, null, task.Request, o.Yes, o.DryRun, o.MaxRetries,
                Verbose: o.Verbose, Host: host);
        }
        return run(request, ct);
    }
}
