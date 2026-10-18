using System.Runtime.CompilerServices;
using PellyCoder.Core.Abstractions;

namespace PellyCoder.Core.Tests.Fakes;

/// <summary>Fake <see cref="ILlmBackend"/> that replays queued responses in order and records what it was asked.</summary>
public sealed class ScriptedLlmBackend : ILlmBackend
{
    private readonly Queue<object> _queue = new();
    private readonly List<LlmRequest> _requests = [];
    private readonly List<CancellationToken> _tokens = [];

    public IReadOnlyList<LlmRequest> Requests => _requests;
    public IReadOnlyList<CancellationToken> ReceivedTokens => _tokens;

    /// <summary>Runs before a response is streamed (e.g. to simulate an external file edit or advance a fake clock).</summary>
    public Func<LlmRequest, Task>? BeforeRespond { get; set; }

    public LlmServerStatus Status { get; set; } =
        new(true, "0.40.0", true, true, "qwen3.5", TimeSpan.FromMilliseconds(1), null);

    public void Enqueue(string text, string doneReason = "stop", int evalCount = 0, int promptEvalCount = 0, int promptEvalCachedCount = 0,
        TimeSpan? loadDuration = null, TimeSpan? promptEvalDuration = null) =>
        _queue.Enqueue(new Scripted(text, doneReason, evalCount, promptEvalCount, promptEvalCachedCount, loadDuration, promptEvalDuration));

    public void EnqueueException(Exception ex) => _queue.Enqueue(ex);

    public async IAsyncEnumerable<LlmChunk> GenerateAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        _requests.Add(request);
        _tokens.Add(ct);
        if (_queue.Count == 0)
            throw new InvalidOperationException("ScriptedLlmBackend: no response queued.");
        var next = _queue.Dequeue();
        if (next is Exception ex)
            throw ex;
        var s = (Scripted)next;
        if (BeforeRespond is not null)
            await BeforeRespond(request);
        foreach (var piece in Split(s.Text, 4))
        {
            ct.ThrowIfCancellationRequested();
            yield return new LlmChunk(piece, false, null, null, null, null, null);
        }
        yield return new LlmChunk("", true, s.PromptEvalCount, s.PromptEvalCachedCount, s.EvalCount, s.PromptEvalDuration, s.DoneReason, s.LoadDuration);
    }

    public int StatusCalls { get; private set; }

    public Task<LlmServerStatus> GetStatusAsync(CancellationToken ct)
    {
        StatusCalls++;
        return Task.FromResult(Status);
    }

    private static IEnumerable<string> Split(string text, int parts)
    {
        if (text.Length == 0)
            yield break;
        var size = Math.Max(1, (text.Length + parts - 1) / parts);
        for (var i = 0; i < text.Length; i += size)
            yield return text.Substring(i, Math.Min(size, text.Length - i));
    }

    private sealed record Scripted(string Text, string DoneReason, int EvalCount, int PromptEvalCount, int PromptEvalCachedCount,
        TimeSpan? LoadDuration, TimeSpan? PromptEvalDuration);
}
