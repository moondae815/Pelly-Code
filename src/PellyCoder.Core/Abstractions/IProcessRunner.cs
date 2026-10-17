namespace PellyCoder.Core.Abstractions;

/// <summary>An external process invocation. Arguments are passed as an argument list, never a shell string.</summary>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    TimeSpan Timeout,
    IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>
/// Result of a finished process. <see cref="StdOut"/> is the UTF-8 decoding of <see cref="StdOutBytes"/>
/// (kept raw so callers can re-decode e.g. CP949). On timeout <see cref="ExitCode"/> is -1 and <see cref="TimedOut"/> is true.
/// </summary>
public sealed record ProcessResult(
    int ExitCode,
    string StdOut,
    string StdErr,
    byte[] StdOutBytes,
    TimeSpan Duration,
    bool TimedOut);

/// <summary>Runs external processes. Cancellation kills the whole process tree, then throws <see cref="OperationCanceledException"/>.</summary>
public interface IProcessRunner
{
    Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct);
}

/// <summary>The executable could not be started (typically not found).</summary>
public sealed class ProcessStartException(string fileName, Exception inner)
    : Exception($"프로세스를 시작할 수 없습니다: {fileName}", inner)
{
    public string FileName { get; } = fileName;
}
