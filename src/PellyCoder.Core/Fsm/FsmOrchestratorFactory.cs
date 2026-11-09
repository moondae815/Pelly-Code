using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Builds;
using PellyCoder.Core.CharTests;
using PellyCoder.Core.Config;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Verification;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Fsm;

/// <summary>The single place that wires the phases and their dependencies (used by the CLI and the E2E tests).</summary>
public static class FsmOrchestratorFactory
{
    public static FsmOrchestrator Create(IFileSystem fs, ILlmBackend backend, IUserInteraction ui, PellyOptions options, TimeProvider time,
        IProcessRunner processes, Func<bool>? isWindows = null)
    {
        var tokens = QwenTokenCounter.Shared;
        var template = options.Llm.Template is { } path ? QwenChatTemplate.LoadFromFile(fs, path) : QwenChatTemplate.LoadDefault();
        var fileGuard = new FileGuard(fs, time);
        var logger = new RunLogger(fs, time);
        var feedback = new FeedbackBuilder(tokens, options.Budget.Feedback);
        var resources = PromptResources.LoadDefault();
        var prompts = new PromptBuilder(template, resources, tokens, options.Budget);
        var client = new QwenStreamClient(backend, template, options.Llm, options.Budget, options.Sampling, time);
        var builds = new BuildVerifier(processes, options.Build);
        var tests = new TestRunner(processes, options.Build, options.Test);

        IPhase[] phases =
        [
            new PreflightPhase(fs, fileGuard, backend, options.Llm, tokens.SpecialTokens),
            new LocatePhase(fs, ui, new BuildToolResolver(fs, processes, options.Build, isWindows), builds,
                new ModelMemberPicker(backend, template, resources, options.Llm, options.Sampling), isWindows, tests),
            new SlicePhase(fs, fileGuard, tokens, options.Budget),
            new EditPhase(prompts, client, feedback, logger, ui),
            new ApplyPhase(fileGuard, feedback, ui, options.Build),
            new VerifyPhase([new SyntaxStep(fs, feedback), new RulesStep(feedback), new BuildStep(builds, feedback, options.Build), new TestStep(tests, feedback, options.Test, fs)]),
            new ReviewPhase(ui),
        ];
        return new FsmOrchestrator(phases, fileGuard, logger, fs, options, ui, time);
    }

    /// <summary>
    /// <c>pelly test</c>: the same PREFLIGHT and LOCATE (with the baseline tests), then the characterization-test steps (design
    /// §3, §4). <paramref name="isWindows"/> decides whether legacy projects can be built and tested.
    /// </summary>
    public static CharTestOrchestrator CreateCharTest(IFileSystem fs, ILlmBackend backend, IUserInteraction ui, PellyOptions options,
        TimeProvider time, IProcessRunner processes, Func<bool>? isWindows = null)
    {
        var tokens = QwenTokenCounter.Shared;
        var template = options.Llm.Template is { } path ? QwenChatTemplate.LoadFromFile(fs, path) : QwenChatTemplate.LoadDefault();
        var fileGuard = new FileGuard(fs, time);
        var logger = new RunLogger(fs, time);
        var resources = PromptResources.LoadDefault();
        var client = new QwenStreamClient(backend, template, options.Llm, options.Budget, options.Sampling, time);
        var builds = new BuildVerifier(processes, options.Build);
        var tests = new TestRunner(processes, options.Build, options.Test);
        var preflight = new PreflightPhase(fs, fileGuard, backend, options.Llm, tokens.SpecialTokens);
        var locate = new LocatePhase(fs, ui, new BuildToolResolver(fs, processes, options.Build, isWindows), builds,
            new ModelMemberPicker(backend, template, resources, options.Llm, options.Sampling), isWindows, tests);
        var prompts = new CharTestPromptBuilder(tokens, options.Budget, template, resources);
        var feedback = new CharTestFeedback(new FeedbackBuilder(tokens, options.Budget.Feedback), tokens, options.Budget.Feedback);
        return new CharTestOrchestrator(fs, preflight, locate, fileGuard, logger, client, prompts, feedback, tests, builds, tokens,
            tokens.SpecialTokens, ui, options, time);
    }

    /// <summary>The SQL pipeline: same EDIT/REVIEW, SQL-specific preflight, locate, slice, apply and verify.</summary>
    public static FsmOrchestrator CreateSql(IFileSystem fs, ILlmBackend backend, IUserInteraction ui, PellyOptions options, TimeProvider time,
        ISqlExecutor sql, SqlReaderOpen? prod = null)
    {
        var tokens = QwenTokenCounter.Shared;
        var template = options.Llm.Template is { } path ? QwenChatTemplate.LoadFromFile(fs, path) : QwenChatTemplate.LoadDefault();
        var fileGuard = new FileGuard(fs, time);
        var logger = new RunLogger(fs, time);
        var feedback = new FeedbackBuilder(tokens, options.Budget.Feedback);
        var resources = PromptResources.LoadDefault();
        var prompts = new PromptBuilder(template, resources, tokens, options.Budget);
        var client = new QwenStreamClient(backend, template, options.Llm, options.Budget, options.Sampling, time);

        IPhase[] phases =
        [
            new SqlPreflightPhase(new PreflightPhase(fs, fileGuard, backend, options.Llm, tokens.SpecialTokens), sql, options.Sql),
            new SqlProdCheckPhase(new SqlLocatePhase(ui, options.Sql), prod ?? new SqlReaderOpen(null, null, null), time),
            new SqlSlicePhase(fileGuard, tokens, options.Budget, sql),
            new EditPhase(prompts, client, feedback, logger, ui),
            new SqlApplyPhase(fileGuard, feedback, ui),
            new VerifyPhase([new RulesStep(feedback), new SqlObjectStep(sql, feedback), new SqlCompileStep(sql, feedback)]),
            new ReviewPhase(ui),
        ];
        return new FsmOrchestrator(phases, fileGuard, logger, fs, options, ui, time);
    }

    /// <summary>
    /// <c>pelly asp</c>: the shared PREFLIGHT, EDIT and REVIEW with ASP locate, slice, apply and verify. One <see cref="AspTextCodec"/>
    /// serves the file guard (read, write, rollback and restore) and the ASP phases, so every read and write keeps the page's encoding.
    /// <paramref name="isWindows"/> decides whether the <c>cscript</c> compile check runs.
    /// </summary>
    public static FsmOrchestrator CreateAsp(IFileSystem fs, ILlmBackend backend, IUserInteraction ui, PellyOptions options, TimeProvider time,
        IProcessRunner processes, ISqlExecutor? sql, Func<bool>? isWindows = null)
    {
        var tokens = QwenTokenCounter.Shared;
        var template = options.Llm.Template is { } path ? QwenChatTemplate.LoadFromFile(fs, path) : QwenChatTemplate.LoadDefault();
        var codec = new AspTextCodec(options.Asp.Encoding);
        var fileGuard = new FileGuard(fs, time, codec);
        var logger = new RunLogger(fs, time);
        var feedback = new FeedbackBuilder(tokens, options.Budget.Feedback);
        var resources = PromptResources.LoadDefault();
        var prompts = new PromptBuilder(template, resources, tokens, options.Budget);
        var client = new QwenStreamClient(backend, template, options.Llm, options.Budget, options.Sampling, time);

        IPhase[] phases =
        [
            new PreflightPhase(fs, fileGuard, backend, options.Llm, tokens.SpecialTokens),
            new AspLocatePhase(fs, ui, options.Asp, codec),
            new AspSlicePhase(fileGuard, tokens, options.Budget, sql, options.Asp, fs, options, codec),
            new EditPhase(prompts, client, feedback, logger, ui),
            new AspApplyPhase(fileGuard, feedback, ui, options.Asp, codec, options.Web),
            new VerifyPhase([
                new AspNamesStep(feedback), new RulesStep(feedback), new AspClientWarningsStep(fs, options, codec), new AspElementsStep(fs, options, codec),
                new AspCompileStep(processes, feedback, fs, isWindows ?? OperatingSystem.IsWindows), new AspSpStep(sql, feedback),
            ]),
            new ReviewPhase(ui),
        ];
        return new FsmOrchestrator(phases, fileGuard, logger, fs, options, ui, time);
    }

    /// <summary>
    /// <c>pelly js</c> and <c>pelly css</c>: the shared PREFLIGHT, EDIT and REVIEW with JS/CSS locate, slice, apply and the
    /// warning-only verify steps. One <see cref="WebCodecRouter"/> (pages by the ASP codec, JS/CSS by <see cref="WebTextCodec"/>) serves the file guard and the web phases, so every read and write
    /// keeps the file's encoding. <paramref name="kind"/> must be <see cref="EditKind.Js"/> or <see cref="EditKind.Css"/>.
    /// </summary>
    public static FsmOrchestrator CreateWeb(IFileSystem fs, ILlmBackend backend, IUserInteraction ui, PellyOptions options, TimeProvider time,
        EditKind kind)
    {
        if (kind is not (EditKind.Js or EditKind.Css))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "pelly js/css needs EditKind.Js or EditKind.Css.");

        var tokens = QwenTokenCounter.Shared;
        var template = options.Llm.Template is { } path ? QwenChatTemplate.LoadFromFile(fs, path) : QwenChatTemplate.LoadDefault();
        var codec = new WebCodecRouter(new WebTextCodec(options.Asp.Encoding), new AspTextCodec(options.Asp.Encoding), options.Asp.Extensions);
        var fileGuard = new FileGuard(fs, time, codec);
        var logger = new RunLogger(fs, time);
        var feedback = new FeedbackBuilder(tokens, options.Budget.Feedback);
        var resources = PromptResources.LoadDefault();
        var prompts = new PromptBuilder(template, resources, tokens, options.Budget);
        var client = new QwenStreamClient(backend, template, options.Llm, options.Budget, options.Sampling, time);

        IPhase locate = kind switch
        {
            EditKind.Js => new JsLocatePhase(fs, ui, options, codec),
            EditKind.Css => new CssLocatePhase(fs, ui, options, codec),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        IVerifierStep[] steps = kind switch
        {
            EditKind.Js => [new RulesStep(feedback), new JsNamesStep(options.Web.Globals), new JsApiStep(), new JsElementsStep(fs, codec)],
            EditKind.Css => [new RulesStep(feedback), new CssPropertyStep()],
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };

        IPhase[] phases =
        [
            new PreflightPhase(fs, fileGuard, backend, options.Llm, tokens.SpecialTokens),
            locate,
            new WebSlicePhase(fileGuard, tokens, options.Budget),
            new EditPhase(prompts, client, feedback, logger, ui),
            new WebApplyPhase(fileGuard, feedback, ui, codec),
            new VerifyPhase(steps),
            new ReviewPhase(ui),
        ];
        return new FsmOrchestrator(phases, fileGuard, logger, fs, options, ui, time);
    }
}
