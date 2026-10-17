using System.Runtime.CompilerServices;
using System.Text;
using PellyCoder.Core.Abstractions;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>
/// Test decorator over a real backend: the n-th generate request (1-based, in send order — THINK, code and the LOCATE
/// pick all count) has its response text replaced by <c>rewrite(n, request, text)</c>. Every response is buffered to its
/// done chunk first (so nothing reaches the caller while the model is still generating): one the rewrite leaves unchanged
/// is then replayed chunk by chunk as the inner backend sent it; a changed one is sent as one text chunk followed by the
/// inner done chunk (counters kept, text emptied). Client-measured TTFT therefore includes the whole generation.
/// <see cref="GetStatusAsync"/> is delegated untouched.
/// </summary>
public sealed class CorruptingBackend : ILlmBackend
{
    private readonly ILlmBackend _inner;
    private readonly Func<int, LlmRequest, string, string> _rewrite;
    private readonly List<LlmRequest> _requests = [];
    private readonly Lock _gate = new();
    private int _rewritten;

    public CorruptingBackend(ILlmBackend inner, Func<int, string, string> rewrite)
        : this(inner, (n, _, text) => rewrite(n, text))
    {
    }

    /// <summary>Overload whose rewrite also sees the request, to pick the one to corrupt by its content (ruling R6).</summary>
    public CorruptingBackend(ILlmBackend inner, Func<int, LlmRequest, string, string> rewrite)
    {
        _inner = inner;
        _rewrite = rewrite;
    }

    /// <summary>Every request sent so far, in send order (request n is <c>Requests[n - 1]</c>).</summary>
    public IReadOnlyList<LlmRequest> Requests
    {
        get { lock (_gate) return [.. _requests]; }
    }

    /// <summary>How many responses the rewrite actually changed.</summary>
    public int RewrittenCount => Volatile.Read(ref _rewritten);

    public async IAsyncEnumerable<LlmChunk> GenerateAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        int n;
        lock (_gate)
        {
            _requests.Add(request);
            n = _requests.Count;
        }

        var chunks = new List<LlmChunk>();
        var text = new StringBuilder();
        await foreach (var chunk in _inner.GenerateAsync(request, ct))
        {
            chunks.Add(chunk);
            text.Append(chunk.Text);
            if (chunk.Done) break;
        }

        var original = text.ToString();
        var rewritten = _rewrite(n, request, original);
        if (rewritten == original)
        {
            foreach (var chunk in chunks) yield return chunk;
            yield break;
        }

        Interlocked.Increment(ref _rewritten);
        var done = chunks.Count > 0 && chunks[^1].Done
            ? chunks[^1] with { Text = "" }
            : new LlmChunk("", true, null, null, null, null, null);
        yield return new LlmChunk(rewritten, false, null, null, null, null, null);
        yield return done;
    }

    public Task<LlmServerStatus> GetStatusAsync(CancellationToken ct) => _inner.GetStatusAsync(ct);
}
