using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Verification;

namespace PellyCoder.Core.Fsm;

/// <summary>The single place that wires the phases and their dependencies (used by the CLI and the E2E tests).</summary>
public static class FsmOrchestratorFactory
{
    public static FsmOrchestrator Create(IFileSystem fs, ILlmBackend backend, IUserInteraction ui, PellyOptions options, TimeProvider time,
        IProcessRunner processes)
    {
        var tokens = QwenTokenCounter.Shared;
        var template = options.Llm.Template is { } path ? QwenChatTemplate.LoadFromFile(fs, path) : QwenChatTemplate.LoadDefault();
        var fileGuard = new FileGuard(fs, time);
        var logger = new RunLogger(fs, time);
        var feedback = new FeedbackBuilder(tokens, options.Budget.Feedback);
        var prompts = new PromptBuilder(template, PromptResources.LoadDefault(), tokens, options.Budget);
        var client = new QwenStreamClient(backend, template, options.Llm, options.Budget, options.Sampling, time);
        var builds = new BuildVerifier(processes, options.Build);

        IPhase[] phases =
        [
            new PreflightPhase(fs, fileGuard, backend, options.Llm),
            new LocatePhase(fs, ui, new BuildToolResolver(fs, processes, options.Build), builds),
            new SlicePhase(fileGuard, tokens, options.Budget),
            new EditPhase(prompts, client, feedback, logger),
            new ApplyPhase(fileGuard, feedback, ui, options.Build),
            new VerifyPhase([new SyntaxStep(fs, feedback), new BuildStep(builds, feedback, options.Build)]),
            new ReviewPhase(ui),
        ];
        return new FsmOrchestrator(phases, fileGuard, logger, fs, options, ui, time);
    }
}
