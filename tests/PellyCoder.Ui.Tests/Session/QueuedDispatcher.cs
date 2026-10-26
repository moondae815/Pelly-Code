using System.Collections.Concurrent;
using PellyCoder.Ui.Session;

namespace PellyCoder.Ui.Tests.Session;

/// <summary>Queues posted actions until <see cref="Drain"/>, like a real UI thread that has not run them yet.</summary>
public sealed class QueuedDispatcher : IUiDispatcher
{
    private readonly ConcurrentQueue<Action> _queue = new();

    public int Count => _queue.Count;

    public void Post(Action action) => _queue.Enqueue(action);

    public void Drain()
    {
        while (_queue.TryDequeue(out var action)) action();
    }
}
