using PellyCoder.Core.Abstractions;

namespace PellyCoder.Core.Tests.Specs.Drafting;

/// <summary>A scripted <see cref="ILlmBackend"/>: the n-th request gets the n-th (text, done reason) as one chunk and a done chunk.</summary>
internal sealed class FakeDraftBackend(params (string Text, string? DoneReason)[] script) : ILlmBackend
{
    public List<LlmRequest> Requests { get; } = [];

    public async IAsyncEnumerable<LlmChunk> GenerateAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var (text, reason) = script[Requests.Count];
        Requests.Add(request);
        await Task.Yield();
        yield return new LlmChunk(text, false, null, null, null, null, null);
        yield return new LlmChunk("", true, 100, null, 7, null, reason);
    }

    public Task<LlmServerStatus> GetStatusAsync(CancellationToken ct) => throw new NotSupportedException();
}
