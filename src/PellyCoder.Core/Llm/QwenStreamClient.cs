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
    TimeSpan Total);

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

    /// <param name="attempt">1-based attempt number.</param>
    public async Task<GenerationResult> GenerateEditAsync(string basePrompt, int attempt, CancellationToken ct)
    {
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
            var (raw, usage) = await RunAsync(req, ct);
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

        var codeReq = new LlmRequest(basePrompt + prefill + SearchMarker, sampling.Code, budget.CodeOutput,
            [template.EndOfTurn], llm.NumCtx, llm.KeepAlive);
        var (code, codeUsage) = await RunAsync(codeReq, ct);

        return new GenerationResult(SearchMarker + code, useThink, thinkText, thinkTokens, thinkTruncated, thinkUsage, codeUsage);
    }

    private async Task<(string Text, LlmUsage Usage)> RunAsync(LlmRequest request, CancellationToken ct)
    {
        var start = time.GetTimestamp();
        TimeSpan? ttft = null;
        var sb = new StringBuilder();
        LlmChunk? last = null;

        await foreach (var chunk in backend.GenerateAsync(request, ct))
        {
            if (ttft is null && chunk.Text.Length > 0)
                ttft = time.GetElapsedTime(start);
            sb.Append(chunk.Text);
            if (chunk.Done)
            {
                last = chunk;
                break;
            }
        }

        var total = time.GetElapsedTime(start);
        var usage = new LlmUsage(last?.PromptEvalCount, last?.PromptEvalCachedCount, last?.EvalCount,
            last?.DoneReason, ttft ?? total, total);
        return (sb.ToString(), usage);
    }
}
