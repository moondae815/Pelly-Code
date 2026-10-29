using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;

namespace PellyCoder.Core.Llm;

/// <summary>Server-reported counts and client-measured timings for one generate request.</summary>
public sealed record LlmUsage(
    int? PromptEvalCount,
    int? PromptEvalCachedCount,
    int? EvalCount,
    string? DoneReason,
    TimeSpan Ttft,
    TimeSpan Total,
    TimeSpan Queue = default);

/// <summary>Outcome of the optional THINK stage plus the CODE stage.</summary>
public sealed record GenerationResult(
    string Output,
    bool ThinkUsed,
    string? ThinkText,
    int ThinkTokens,
    bool ThinkTruncated,
    LlmUsage? ThinkUsage,
    LlmUsage CodeUsage);

/// <summary>
/// Two-stage generation (design §8.2): an optional bounded THINK stage, then a CODE stage prefilled with
/// the SEARCH marker. Limits are server-side only (D22): the caller's token is passed through untouched
/// and the stream is never cancelled here. <c>stop</c> is sent but not trusted (D23).
/// </summary>
public sealed class QwenStreamClient(
    ILlmBackend backend,
    QwenChatTemplate template,
    LlmOptions llm,
    BudgetOptions budget,
    SamplingSet sampling,
    TimeProvider time)
{
    private const string SearchMarker = "<<<<<<< SEARCH\n";
    private const string ThinkCloseTag = "</think>";

    /// <param name="answerPrefix">Prefilled start of the answer; null means the SEARCH marker.</param>
    /// <param name="attempt">1-based attempt number.</param>
    /// <param name="onWaiting">
    /// Reports only (never cancels): per generate request, if no chunk has arrived 3 s after sending, called with the elapsed
    /// time at 3 s and then every second until the first chunk arrives or the request ends.
    /// </param>
    public async Task<GenerationResult> GenerateEditAsync(string basePrompt, int attempt, CancellationToken ct,
        Action<TimeSpan>? onWaiting = null, string? answerPrefix = null)
    {
        var prefix = answerPrefix ?? SearchMarker;
        var useThink = llm.ThinkMode == ThinkMode.Always || (llm.ThinkMode == ThinkMode.Retry && attempt >= 2);

        string? thinkText = null;
        var thinkTokens = 0;
        var thinkTruncated = false;
        LlmUsage? thinkUsage = null;
        string prefill;

        if (useThink)
        {
            var req = new LlmRequest(basePrompt + template.ThinkOpen, sampling.Think, budget.Think,
                [ThinkCloseTag], llm.NumCtx, llm.KeepAlive);
            var (raw, usage) = await ReadAsync(backend, req, time, ct, onWaiting);
            thinkUsage = usage;
            thinkTokens = usage.EvalCount ?? 0;

            var close = raw.IndexOf(ThinkCloseTag, StringComparison.Ordinal);
            if (close >= 0)
            {
                thinkText = raw[..close];
            }
            else if (usage.DoneReason == "length")
            {
                thinkTruncated = true;
                var nl = raw.LastIndexOf('\n');
                thinkText = nl >= 0 ? raw[..nl] : "";
            }
            else
            {
                thinkText = raw;
            }
            thinkText = thinkText.TrimEnd();
            prefill = template.ThinkOpen + thinkText + template.ThinkClose;
        }
        else
        {
            prefill = template.EmptyThink;
        }

        var codeReq = new LlmRequest(basePrompt + prefill + prefix, sampling.Code, budget.CodeOutput,
            [template.EndOfTurn], llm.NumCtx, llm.KeepAlive);
        var (code, codeUsage) = await ReadAsync(backend, codeReq, time, ct, onWaiting);

        return new GenerationResult(prefix + code, useThink, thinkText, thinkTokens, thinkTruncated, thinkUsage, codeUsage);
    }

    private static readonly TimeSpan WaitingAfter = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan WaitingEvery = TimeSpan.FromSeconds(1);

    /// <summary>Streams one request to the done chunk and collects its text and counters (shared with <see cref="ModelMemberPicker"/>).</summary>
    internal static async Task<(string Text, LlmUsage Usage)> ReadAsync(ILlmBackend backend, LlmRequest request, TimeProvider time,
        CancellationToken ct, Action<TimeSpan>? onWaiting = null)
    {
        var start = time.GetTimestamp();
        TimeSpan? ttft = null;
        var sb = new StringBuilder();
        LlmChunk? last = null;

        var gate = new object();
        var stopped = false;
        ITimer? timer = null;
        if (onWaiting is not null)
        {
            timer = time.CreateTimer(_ =>
            {
                lock (gate)
                {
                    if (stopped) return;
                    try
                    {
                        onWaiting(time.GetElapsedTime(start));
                    }
                    catch (Exception)
                    {
                        // A status callback must never break generation. This runs on a timer thread, so even a cancellation
                        // thrown by the callback must stop here: nothing above it could catch it and the process would die.
                    }
                }
            }, null, WaitingAfter, WaitingEvery);
        }

        try
        {
            await foreach (var chunk in backend.GenerateAsync(request, ct))
            {
                if (ttft is null)
                {
                    // any chunk (even an empty keep-alive line) proves the server is answering
                    StopTimer();
                    if (chunk.Text.Length > 0)
                        ttft = time.GetElapsedTime(start);
                }
                sb.Append(chunk.Text);
                if (chunk.Done)
                {
                    last = chunk;
                    break;
                }
            }
        }
        finally
        {
            StopTimer();
        }

        var total = time.GetElapsedTime(start);
        var first = ttft ?? total;
        // Neither duration reported: the wait cannot be split, so Queue stays 0 and the whole wait is TTFT.
        var queue = TimeSpan.Zero;
        if (last?.LoadDuration is not null || last?.PromptEvalDuration is not null)
        {
            var server = (last.LoadDuration ?? TimeSpan.Zero) + (last.PromptEvalDuration ?? TimeSpan.Zero);
            queue = first - server;
            if (queue < TimeSpan.Zero) queue = TimeSpan.Zero;
        }
        var usage = new LlmUsage(last?.PromptEvalCount, last?.PromptEvalCachedCount, last?.EvalCount,
            last?.DoneReason, first, total, queue);
        return (sb.ToString(), usage);

        void StopTimer()
        {
            lock (gate) stopped = true;
            timer?.Dispose();
        }
    }
}
