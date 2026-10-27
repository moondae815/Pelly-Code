using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Llm;

namespace PellyCoder.Core.Specs.Drafting;

/// <param name="Stage1Prompt">Raw prompt of the think stage (ends with the think-open tag).</param>
/// <param name="Think">Think text exactly as the server returned it (also when truncated).</param>
/// <param name="ThinkTruncated">The think stage stopped at its token limit (done reason "length").</param>
/// <param name="Answer">The answer stage text, untouched.</param>
public sealed record DraftCall(string Stage1Prompt, string Think, bool ThinkTruncated, string Answer, int InputTokens, double Seconds);

/// <summary>The S6 planning probe's arm B: a think stage, then an answer stage that continues the think text verbatim.</summary>
public sealed class DraftModel(ILlmBackend backend, QwenChatTemplate template, TimeProvider time)
{
    private const int NumCtx = 8192;
    private const string KeepAlive = "10m";
    private static readonly SamplingOptions ThinkSampling = new(0.6, 0.95, 20, 1.0, 0.0);
    private static readonly SamplingOptions AnswerSampling = new(0.1, 0.9, 20, 1.05, 0.0);

    private string Stage1(string request, string renderedIndex) =>
        template.Build(DraftPrompt.System, DraftPrompt.User(request, renderedIndex)) + template.ThinkOpen;

    public int CountInput(string request, string renderedIndex) =>
        QwenTokenCounter.Shared.Count(Stage1(request, renderedIndex));

    public async Task<DraftCall> RunAsync(string request, string renderedIndex, CancellationToken ct, Action<TimeSpan>? onWaiting = null)
    {
        var start = time.GetTimestamp();
        var stage1 = Stage1(request, renderedIndex);

        var think = await QwenStreamClient.ReadAsync(backend,
            new LlmRequest(stage1, ThinkSampling, 2048, [template.ThinkClose], NumCtx, KeepAlive), time, ct, onWaiting);

        // S6 Runner.Stage2Prompt: the think text goes on as returned, truncated or not.
        var stage2 = stage1 + think.Text + template.ThinkClose;
        var answer = await QwenStreamClient.ReadAsync(backend,
            new LlmRequest(stage2, AnswerSampling, 1024, [template.EndOfTurn], NumCtx, KeepAlive), time, ct, onWaiting);

        return new DraftCall(stage1, think.Text, think.Usage.DoneReason == "length", answer.Text,
            QwenTokenCounter.Shared.Count(stage1), time.GetElapsedTime(start).TotalSeconds);
    }
}
