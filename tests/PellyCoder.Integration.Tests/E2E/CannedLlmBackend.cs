using System.Runtime.CompilerServices;
using PellyCoder.Core.Abstractions;

namespace PellyCoder.Integration.Tests.E2E;

/// <summary>
/// Offline <see cref="ILlmBackend"/> for integration runs against a real SQL Server: the n-th generate request (1-based, in
/// send order) gets the n-th canned text, streamed as a few text chunks and a done chunk, like an NDJSON response. A request
/// past the end of the list fails the run with <see cref="InvalidOperationException"/>. The server status is always
/// "reachable, model installed".
/// </summary>
public sealed class CannedLlmBackend(IReadOnlyList<string> responses) : ILlmBackend
{
    private readonly List<LlmRequest> _requests = [];
    private readonly Lock _gate = new();

    /// <summary>Every request sent so far, in send order.</summary>
    public IReadOnlyList<LlmRequest> Requests
    {
        get { lock (_gate) return [.. _requests]; }
    }

    public async IAsyncEnumerable<LlmChunk> GenerateAsync(LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
    {
        int n;
        lock (_gate)
        {
            _requests.Add(request);
            n = _requests.Count;
        }
        if (n > responses.Count)
            throw new InvalidOperationException($"CannedLlmBackend: request {n} has no canned response ({responses.Count} given).");

        var text = responses[n - 1];
        var size = Math.Max(1, (text.Length + 3) / 4);
        for (var i = 0; i < text.Length; i += size)
        {
            ct.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return new LlmChunk(text.Substring(i, Math.Min(size, text.Length - i)), false, null, null, null, null, null);
        }
        yield return new LlmChunk("", true, 100, 0, Math.Max(1, text.Length / 4), TimeSpan.FromMilliseconds(1), "stop");
    }

    public Task<LlmServerStatus> GetStatusAsync(CancellationToken ct) =>
        Task.FromResult(new LlmServerStatus(true, "canned", true, true, "qwen3.5", TimeSpan.Zero, null));
}
