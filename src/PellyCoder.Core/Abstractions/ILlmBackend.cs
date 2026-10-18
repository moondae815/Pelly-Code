namespace PellyCoder.Core.Abstractions;

/// <summary>Sampling parameters sent with a generation request.</summary>
public sealed record SamplingOptions(
    double Temperature,
    double TopP,
    int TopK,
    double RepeatPenalty,
    double PresencePenalty);

/// <summary>A raw-mode generation request (design §5, §8.2).</summary>
public sealed record LlmRequest(
    string RawPrompt,
    SamplingOptions Sampling,
    int NumPredict,
    IReadOnlyList<string> Stop,
    int NumCtx,
    string KeepAlive);

/// <summary>One streamed piece of a response; the final chunk has <see cref="Done"/> and the counters.</summary>
public sealed record LlmChunk(
    string Text,
    bool Done,
    int? PromptEvalCount,
    int? PromptEvalCachedCount,
    int? EvalCount,
    TimeSpan? PromptEvalDuration,
    string? DoneReason,
    TimeSpan? LoadDuration = null);

/// <summary>Result of the server preflight. <see cref="Renderer"/> replaces the design's ModelTemplate (S1).</summary>
public sealed record LlmServerStatus(
    bool Reachable,
    string? Version,
    bool ModelInstalled,
    bool ModelLoaded,
    string? Renderer,
    TimeSpan Latency,
    string? Error);

public interface ILlmBackend
{
    IAsyncEnumerable<LlmChunk> GenerateAsync(LlmRequest request, CancellationToken ct);

    /// <summary>Never throws except <see cref="OperationCanceledException"/>; failures go into <see cref="LlmServerStatus.Error"/>.</summary>
    Task<LlmServerStatus> GetStatusAsync(CancellationToken ct);
}

public interface IOllamaDiagnostics
{
    /// <summary>Server-side prompt token count via /api/chat (think: true, num_predict: 1), or null if not reported.</summary>
    Task<int?> ChatPromptTokenCountAsync(string system, string user, CancellationToken ct);
}

/// <summary>The LLM server could not be used (down, timed out, or rejected the request). Maps to exit code 4.</summary>
public sealed class LlmUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
