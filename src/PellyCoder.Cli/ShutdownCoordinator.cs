namespace PellyCoder.Cli;

/// <summary>
/// Turns every way the process is asked to stop (Ctrl+C, SIGTERM, SIGHUP, closing the console window) into a
/// cancellation of the shared token, so the orchestrator rolls back an unapproved patch before the process ends.
/// A termination signal handler blocks in <see cref="RequestShutdownAndWait"/> until the command finished
/// (<see cref="SignalCompleted"/>, called after the rollback), bounded by the completion wait.
/// Safe to call from signal threads at any time, also after <see cref="Dispose"/>, and any number of times.
/// </summary>
public sealed class ShutdownCoordinator : IDisposable
{
    /// <summary>
    /// Windows terminates the process about 5 s after CTRL_CLOSE_EVENT whatever the handler does, so wait less.
    /// Windows 미확인: the console-close path is not verified on real Windows.
    /// </summary>
    public static readonly TimeSpan DefaultCompletionWait = TimeSpan.FromSeconds(4.5);

    private readonly TimeSpan _completionWait;
    private readonly CancellationTokenSource _cts = new();

    // Never disposed: a signal thread may still be waiting on it while the main thread disposes the coordinator.
    private readonly ManualResetEventSlim _completed = new(false);
    private volatile bool _disposed;

    public ShutdownCoordinator() : this(DefaultCompletionWait) { }

    public ShutdownCoordinator(TimeSpan completionWait)
    {
        _completionWait = completionWait;
        Token = _cts.Token;
    }

    /// <summary>Token for the command; captured at construction so it stays readable after dispose.</summary>
    public CancellationToken Token { get; }

    /// <summary>Cancels the shared token without waiting (Ctrl+C: the process keeps running and rolls back itself).</summary>
    public void RequestCancel()
    {
        if (_disposed) return;
        try
        {
            _cts.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Disposed between the check and the call: the command has already finished.
        }
    }

    /// <summary>Cancels and blocks until the command signalled completion; false when the wait timed out.</summary>
    public bool RequestShutdownAndWait()
    {
        RequestCancel();
        return _completed.Wait(_completionWait);
    }

    /// <summary>Called once the command returned (after any rollback); releases blocked signal handlers.</summary>
    public void SignalCompleted() => _completed.Set();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _completed.Set();
        _cts.Dispose();
    }
}
