using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;
using PellyCoder.Core.Patchers;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>One attempt: builds the prompt (with the latest feedback), generates, and parses the SEARCH/REPLACE blocks.</summary>
public sealed class EditPhase(PromptBuilder prompts, QwenStreamClient client, FeedbackBuilder feedback, RunLogger logger,
    IUserInteraction ui) : IPhase
{
    public RunPhase Phase => RunPhase.Edit;

    public async Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        ctx.Attempt++;
        ctx.Generation = null;
        ctx.ModelOutput = null;
        ctx.Blocks = [];

        var used = ctx.Feedback;
        var prompt = ctx.Target!.Kind == EditKind.Sql
            ? prompts.BuildSql(ctx.Target, ctx.Sql!.Kind, ctx.SliceResult.Slice, ctx.Request.Request, used, ctx.Rules)
            : prompts.BuildCSharp(ctx.Target, ctx.SliceResult.Slice, ctx.Request.Request, used, ctx.Rules, ctx.Request.Group is not null);
        ctx.Prompt = prompt;
        if (prompt.Error != PromptBudgetError.None)
        {
            ctx.Messages.Add($"프롬프트가 입력 예산을 넘습니다: {prompt.Detail}");
            return PhaseOutcome.BudgetExceeded;
        }
        ctx.Feedback = null; // consumed by this attempt

        GenerationResult gen;
        try
        {
            gen = await client.GenerateEditAsync(prompt.BasePrompt, ctx.Attempt, ct,
                elapsed => ui.ShowWaiting(RunPhase.Edit, ctx.Attempt,
                    $"서버 응답 대기 중 ({elapsed.TotalSeconds:0}초) — 다른 요청을 처리 중이거나 모델을 불러오는 중일 수 있습니다"));
        }
        catch (LlmUnavailableException ex)
        {
            ctx.Messages.Add($"LLM 서버를 사용할 수 없습니다: {ex.Message} (`pelly doctor`로 확인하세요)");
            return PhaseOutcome.EnvError;
        }
        ctx.Generation = gen;
        ctx.ModelOutput = gen.Output;
        logger.Attempt(ctx, prompt.BasePrompt, gen, used);

        var parsed = SearchReplaceParser.Parse(gen.Output);
        if (parsed.Failure is not null)
        {
            ctx.Feedback = feedback.ForPatchFailure(parsed.Failure);
            ctx.FailureKind = parsed.Failure.Kind;
            ctx.FormatErrors++;
            return PhaseOutcome.FormatError;
        }
        ctx.Blocks = parsed.Blocks;
        return PhaseOutcome.Ok;
    }
}
