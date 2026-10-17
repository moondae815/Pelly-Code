using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Logging;

namespace PellyCoder.Core.Fsm;

/// <summary>The single place that wires the M1 phases and their dependencies (used by the CLI and the E2E tests).</summary>
public static class FsmOrchestratorFactory
{
    public static FsmOrchestrator Create(IFileSystem fs, ILlmBackend backend, IUserInteraction ui, PellyOptions options, TimeProvider time)
    {
        var tokens = QwenTokenCounter.Shared;
        var template = options.Llm.Template is { } path ? QwenChatTemplate.LoadFromFile(fs, path) : QwenChatTemplate.LoadDefault();
        var fileGuard = new FileGuard(fs, time);
        var logger = new RunLogger(fs, time);
        var feedback = new FeedbackBuilder(tokens, options.Budget.Feedback);
        var prompts = new PromptBuilder(template, PromptResources.LoadDefault(), tokens, options.Budget);
        var client = new QwenStreamClient(backend, template, options.Llm, options.Budget, options.Sampling, time);

        IPhase[] phases =
        [
            new PreflightPhase(fs, fileGuard, backend, options.Llm),
            new LocatePhase(fs, ui),
            new SlicePhase(fileGuard, tokens, options.Budget),
            new EditPhase(prompts, client, feedback, logger),
            new ApplyPhase(fileGuard, feedback, ui),
            new VerifyPhase(fs, feedback),
            new ReviewPhase(ui),
        ];
        return new FsmOrchestrator(phases, fileGuard, logger, fs, options, ui, time);
    }
}
