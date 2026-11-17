using PellyCode.Core.Abstractions;
using PellyCode.Core.Fsm;
using PellyCode.Core.Models;

namespace PellyCode.Eval;

/// <summary>
/// Runs a script (env <c>PELLY_EVAL_AGENT</c>) in place of the pelly orchestrator, so another coding agent is judged by the same
/// checks. Arguments, in order: work folder, file relative to it, kind (<c>cs</c>|<c>sql</c>), method, line, object, request.
/// The script's output goes to <c>agent.log</c> in the work folder; a timeout returns 124.
/// </summary>
public static class ExternalAgent
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(15);
    public const int TimeoutExitCode = 124;

    public static Func<RunRequest, CancellationToken, Task<int>> Create(string script, IProcessRunner processes, TimeSpan? timeout = null) => async (request, ct) =>
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(request.FilePath))!;
        while (Path.GetFileName(Path.GetDirectoryName(dir)) != "work") dir = Path.GetDirectoryName(dir)!;
        var relative = Path.GetRelativePath(dir, request.FilePath).Replace('\\', '/');
        var spec = new ProcessSpec("/bin/bash",
            [script, dir, relative, request.Kind == EditKind.Sql ? "sql" : "cs", request.MethodName ?? "", request.Line?.ToString() ?? "",
             request.ObjectName ?? "", request.Request],
            dir, timeout ?? DefaultTimeout);
        var result = await processes.RunAsync(spec, ct);
        await File.WriteAllTextAsync(Path.Combine(dir, "agent.log"),
            $"exit={result.ExitCode} timedOut={result.TimedOut} seconds={result.Duration.TotalSeconds:0.0}\n--- stdout\n{result.StdOut}\n--- stderr\n{result.StdErr}", ct);
        return result.TimedOut ? TimeoutExitCode : result.ExitCode;
    };
}
