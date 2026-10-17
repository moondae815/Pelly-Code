using PellyCoder.Core.Abstractions;

namespace PellyCoder.Core.Tests.Fakes;

/// <summary>Fake <see cref="IProcessRunner"/> that replays queued results in order and records every spec it receives.</summary>
public sealed class ScriptedProcessRunner : IProcessRunner
{
    private readonly Queue<Func<CancellationToken, Task<ProcessResult>>> _queue = new();
    private readonly List<ProcessSpec> _calls = [];
    private ProcessResult? _fallback;
    private int _pending;
    private int _cancelled;

    public IReadOnlyList<ProcessSpec> Calls => _calls;

    /// <summary>Calls that have started but not finished (a leftover one would be an orphaned build).</summary>
    public int Pending => Volatile.Read(ref _pending);

    /// <summary>Calls that ended with <see cref="OperationCanceledException"/>.</summary>
    public int CancelledCount => Volatile.Read(ref _cancelled);

    public void Enqueue(ProcessResult result) => _queue.Enqueue(_ => Task.FromResult(result));

    /// <summary>Waits for <paramref name="delay"/> (honouring cancellation) before returning the result.</summary>
    public void EnqueueDelay(TimeSpan delay, ProcessResult result) =>
        _queue.Enqueue(async ct =>
        {
            await Task.Delay(delay, ct);
            return result;
        });

    public void EnqueueThrow(Exception ex) => _queue.Enqueue(_ => Task.FromException<ProcessResult>(ex));

    /// <summary>Result returned when the queue is empty, instead of throwing.</summary>
    public void WhenEmpty(ProcessResult result) => _fallback = result;

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CancellationToken ct)
    {
        _calls.Add(spec);
        Interlocked.Increment(ref _pending);
        try
        {
            if (_queue.Count > 0) return await _queue.Dequeue()(ct);
            if (_fallback is not null) return _fallback;
            throw new InvalidOperationException("ScriptedProcessRunner queue is empty.");
        }
        catch (OperationCanceledException)
        {
            Interlocked.Increment(ref _cancelled);
            throw;
        }
        finally
        {
            Interlocked.Decrement(ref _pending);
        }
    }
}
