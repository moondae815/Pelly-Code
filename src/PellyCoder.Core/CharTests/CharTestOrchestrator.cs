using System.IO.Abstractions;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Fsm.Phases;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Verification;
using Diagnostic = PellyCoder.Core.Models.Diagnostic;

namespace PellyCoder.Core.CharTests;

/// <summary>
/// Runs <c>pelly test</c> (design §3, §4): PREFLIGHT and LOCATE as in <c>cs</c>, then the test project, framework and test class,
/// the baseline tests, and up to <c>MaxRetries + 1</c> attempts of generate → static verdicts 1-3 → write → run verdicts 4-7, then
/// review. The target file is mutated only for verdict 7 and restored in a <c>finally</c> that ignores cancellation; the test file and
/// csproj go back to their original bytes unless kept; the <c>chartest.pending</c> marker is deleted only when every file was kept or
/// restored. Built by <see cref="FsmOrchestratorFactory.CreateCharTest"/>.
/// </summary>
public sealed class CharTestOrchestrator(
    IFileSystem fs, PreflightPhase preflight, LocatePhase locate, FileGuard fileGuard, RunLogger logger, QwenStreamClient client,
    CharTestPromptBuilder prompts, CharTestFeedback feedback, TestRunner tests, BuildVerifier builds, ITokenCounter tokens,
    IReadOnlyCollection<string> specialTokens, IUserInteraction ui, PellyOptions options, TimeProvider time)
{
    // Fields (not captured parameters) so the per-run session can reach them.
    private readonly IFileSystem fs = fs;
    private readonly PreflightPhase preflight = preflight;
    private readonly LocatePhase locate = locate;
    private readonly FileGuard fileGuard = fileGuard;
    private readonly RunLogger logger = logger;
    private readonly QwenStreamClient client = client;
    private readonly CharTestPromptBuilder prompts = prompts;
    private readonly CharTestFeedback feedback = feedback;
    private readonly TestRunner tests = tests;
    private readonly BuildVerifier builds = builds;
    private readonly ITokenCounter tokens = tokens;
    private readonly IReadOnlyCollection<string> specialTokens = specialTokens;
    private readonly IUserInteraction ui = ui;
    private readonly PellyOptions options = options;
    private readonly TimeProvider time = time;

    public const string AnswerPrefix = "### USINGS\n";

    public const string NoTestProjectMessage =
        "대상 프로젝트를 참조하는 테스트 프로젝트가 없습니다. 테스트 프로젝트를 만들고 참조를 추가하거나 `test.projects`를 설정하세요";

    public const string NoPerTestResultsMessage = "테스트별 결과를 읽을 수 없어 판정할 수 없습니다";

    public const string LegacyNotWindowsMessage = "레거시 테스트는 Windows에서만 실행합니다";

    /// <summary>Shown when the build of the restored target after a mutation failed or was skipped.</summary>
    public const string MutationOutputWarning = "변이 빌드 결과물이 bin·obj에 남아 있을 수 있습니다 — 다시 빌드하세요";

    private static readonly TimeSpan BaselineStopWait = TimeSpan.FromSeconds(10);

    /// <summary>LOCATE's "going on without tests" notices: <c>pelly test</c> stops instead, with its own message.</summary>
    private static readonly string[] SkipNoticePrefixes =
    [
        "csproj가 없어 빌드 검증을 건너뜁니다", "레거시 .NET Framework 프로젝트는 Windows에서만 빌드 검증합니다",
        "레거시 테스트는 Windows에서만 실행합니다 — 테스트를 건너뜁니다", "테스트 프로젝트를 찾지 못해 테스트를 건너뜁니다",
    ];

    public Task<int> RunAsync(CharTestRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new Session(this, request).RunAsync(ct);
    }

    /// <summary>The options with <c>test.projects</c> replaced by <paramref name="testProject"/> (full path) when given.</summary>
    private PellyOptions OptionsFor(string? testProject) =>
        testProject is null ? options : options.WithTest(options.Test.WithProjects([testProject]));

    /// <summary>One run: its context, state and message display.</summary>
    private sealed class Session
    {
        private readonly CharTestOrchestrator _o;
        private readonly CharTestRequest _request;
        private readonly RunContext _ctx;
        private readonly CharTestState _state;
        private readonly CharTestPending _pending;
        private int _shownMessages;
        private bool _displayBroken;
        private bool _logStarted;

        // Set while the run goes on; valid after the step that sets them.
        private string _testProject = "";
        private FrameworkInfo _framework = null!;
        private LanguageTarget _testLanguage = null!;
        private TestRun _baseline = null!;
        private string _baseText = "";
        private string? _csprojAdded;
        private bool _targetWritten;
        private bool _targetRestoreFailed;

        public Session(CharTestOrchestrator o, CharTestRequest request)
        {
            _o = o;
            _request = request;
            var testProject = request.TestProject is { } tp ? o.fs.Path.GetFullPath(tp) : null;
            // LOCATE as in `cs --test`: it resolves the target, starts the baseline build and then the baseline tests.
            var runRequest = new RunRequest(request.FilePath, request.ClassName, request.MethodName, request.Line, request.Hint ?? "",
                request.Yes, DryRun: false, request.MaxRetries, Kind: EditKind.CSharp, Verbose: request.Verbose, RunTests: true);
            _ctx = new RunContext(RunLogger.NewRunId(o.time), runRequest, o.OptionsFor(testProject));
            _state = new CharTestState(request.Count);
            _ctx.CharTest = _state;
            _pending = new CharTestPending(o.fs);
        }

        private IFileSystem Fs => _o.fs;

        public async Task<int> RunAsync(CancellationToken ct)
        {
            try
            {
                _ctx.ExitCode = await FlowAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _ctx.Messages.Add("사용자가 실행을 중단했습니다.");
                _ctx.FailureKind = FailureKinds.Cancelled;
                _ctx.ExitCode = ExitCodes.UserCancelled;
            }
            catch (Exception ex)
            {
                _ctx.Messages.Add($"예기치 않은 오류로 중단합니다: {ex.GetType().Name}: {ex.Message}");
                _ctx.FailureKind = FailureKinds.Crashed;
                _ctx.ExitCode = ExitCodes.EnvError;
            }
            finally
            {
                try
                {
                    await FinishFilesAsync();
                    await StopBaselineAsync();
                    await RebuildAfterMutationAsync(ct);
                    _ctx.Phase = _ctx.ExitCode == 0 ? RunPhase.Done : RunPhase.Failed;
                    if (_ctx.ExitCode == 0) _ctx.FailureKind = null;
                    Flush();
                    if (!_logStarted && _ctx.Workspace is not null) BestEffort(() => _o.logger.Start(_ctx));
                    BestEffort(() => _o.logger.Complete(_ctx));
                    if (_request.Verbose)
                    {
                        var runDir = _ctx.Workspace is { } w ? Fs.Path.Combine(w.PellyDir, "runs", _ctx.RunId) : null;
                        BestEffort(() => _o.ui.ShowRunSummary(RunLogger.Summarize(_ctx), runDir));
                    }
                    if (_ctx.Lock is not null && _ctx.Workspace is { } ws)
                        BestEffort(() => _o.logger.Prune(ws, _o.options.Log.RetentionDays, _ctx.RunId));
                    Flush();
                }
                finally
                {
                    _ctx.Lock?.Dispose();
                }
            }
            return _ctx.ExitCode;
        }

        private async Task<int> FlowAsync(CancellationToken ct)
        {
            var file = Fs.Path.GetFileName(_request.FilePath);
            if (await PhaseAsync(RunPhase.Preflight, 0, $"사전 점검: {file}", () => _o.preflight.ExecuteAsync(_ctx, ct)) != PhaseOutcome.Ok)
                return ExitCodes.EnvError;

            if (_ctx.Options.Test.Projects.Count == 1 && _request.TestProject is not null && !Fs.File.Exists(_ctx.Options.Test.Projects[0]))
                return Stop(ExitCodes.LocateFailed, FailureKinds.TestNotFound, $"--test-project 파일이 없습니다: {_ctx.Options.Test.Projects[0]}");

            var locateText = _request switch
            {
                { Line: { } line } => $"대상 찾기: {line}번 줄",
                { MethodName: { } method } => $"대상 찾기: {method}",
                _ => "대상 찾기: 힌트에서",
            };
            var located = await PhaseAsync(RunPhase.Locate, 0, locateText, () => _o.locate.ExecuteAsync(_ctx, ct), muteSkipNotices: true);
            if (located is PhaseOutcome.NotFound or PhaseOutcome.Ambiguous) return ExitCodes.LocateFailed;
            if (located != PhaseOutcome.Ok) return ExitCodes.EnvError;
            // The run is a characterization-test run: the summary kind and prompt version follow the target kind (ruling R2).
            _ctx.Target = _ctx.Target! with { Kind = EditKind.CSharpTest };

            ct.ThrowIfCancellationRequested();
            if (SetUp() is { } setupExit) return setupExit;

            // The model is never asked before the baseline is known to be comparable.
            if (await BaselineAsync(ct) is { } baselineExit) return baselineExit;

            var prompt = BuildPromptParts();
            var max = _ctx.MaxRetries + 1;
            for (var attempt = 1; attempt <= max; attempt++)
            {
                _ctx.Attempt = attempt;
                var (passed, exit) = await AttemptAsync(attempt, max, prompt, ct);
                if (exit is { } code) return code;
                if (passed) return await ReviewAsync(ct);
            }

            _ctx.Messages.Add($"재시도 {_ctx.MaxRetries}회를 모두 썼지만 판정을 통과하지 못했습니다 (마지막 실패: {_state.RejectedBy}). 테스트 파일을 원래대로 되돌립니다");
            if (_lastFeedback is { } last) _ctx.Messages.Add("마지막 피드백:\n" + last.Text);
            return ExitCodes.VerifyFailed;
        }

        private Feedback? _lastFeedback;

        /// <summary>Target, test project, run mode, framework and test class checks; null to go on, or the exit code.</summary>
        private int? SetUp()
        {
            var candidate = _ctx.Candidate!;
            if (_ctx.BuildTool is not { } tool)
            {
                return _ctx.ProjectStyle == nameof(ProjectStyle.Legacy)
                    ? Stop(ExitCodes.EnvError, FailureKinds.TestUnverifiable, LegacyNotWindowsMessage)
                    : Stop(ExitCodes.LocateFailed, FailureKinds.TestNotFound, NoTestProjectMessage);
            }

            var single = _ctx.TestProjects.Count == 1 ? _ctx.TestProjects[0] : null;
            string? visibleTo = null;
            if (single is not null && _ctx.Target!.ProjectPath is { } production)
            {
                var assembly = AssemblyNameOf(single);
                if (CharTestTarget.HasInternalsVisibleTo(Fs, production, assembly)) visibleTo = assembly;
            }
            var (methodName, typeName, targetError) = CharTestTarget.Check(_ctx.Current.Text, candidate, _ctx.Language!.Version, visibleTo);
            if (targetError is not null) return Stop(ExitCodes.LocateFailed, FailureKinds.TargetNotFound, targetError);
            _methodName = methodName;
            _typeName = typeName;

            if (_ctx.TestProjects.Count == 0)
                return Stop(ExitCodes.LocateFailed, FailureKinds.TestNotFound, NoTestProjectMessage);
            if (single is null)
            {
                var list = string.Join(", ", _ctx.TestProjects.Select(p => Fs.Path.GetRelativePath(_ctx.Workspace!.Root, p)));
                return Stop(ExitCodes.LocateFailed, FailureKinds.TestAmbiguous,
                    $"테스트 프로젝트 후보가 여럿입니다. --test-project로 하나를 고르세요: {list}");
            }
            _testProject = single;
            _state.TestProject = single;

            // Without per-test results "cannot judge" would pass for success: refused before the baseline is awaited.
            if (tool.Kind == BuildToolKind.MsBuild)
            {
                if (string.IsNullOrWhiteSpace(_o.options.Test.Command))
                    return Stop(ExitCodes.EnvError, FailureKinds.TestUnverifiable, TestRunner.LegacyCommandMissing);
                if (!_o.options.Test.Command.Contains("{trx}", StringComparison.Ordinal))
                    return Stop(ExitCodes.EnvError, FailureKinds.TestUnverifiable,
                        NoPerTestResultsMessage + " (test.command에 {trx}가 없습니다)");
            }
            else if (TestRunner.Kind(single) == TestRunner.TestProjectKind.OtherTestingPlatform)
            {
                return Stop(ExitCodes.EnvError, FailureKinds.TestUnverifiable,
                    NoPerTestResultsMessage + " (Microsoft Testing Platform 프로젝트는 종료 코드만 알 수 있습니다)");
            }

            var (framework, frameworkError) = TestFrameworks.Detect(Fs, single);
            if (framework is not { } fw)
                return Stop(ExitCodes.EnvError, FailureKinds.TestUnverifiable, $"{frameworkError}: {single}");
            _framework = TestFrameworks.Info(fw);
            _state.Framework = _framework;
            _libraries = TestFrameworks.Libraries(Fs, single);

            try
            {
                _testLanguage = LanguageVersionDetector.Detect(Fs, single);
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                return Stop(ExitCodes.EnvError, FailureKinds.EnvError, $"테스트 프로젝트 파일을 읽을 수 없습니다: {single} ({ex.Message})");
            }

            var location = TestClassLocator.Find(Fs, single, typeName, _testLanguage.Version);
            if (location.Error is not null) return Stop(ExitCodes.LocateFailed, FailureKinds.TestAmbiguous, location.Error);
            _state.Location = location;

            if (!location.IsNew)
            {
                try
                {
                    _state.TestOriginal = _o.fileGuard.Read(location.FilePath);
                }
                catch (InvalidDataException ex)
                {
                    return Stop(ExitCodes.EnvError, FailureKinds.EnvError, ex.Message);
                }
                _baseText = _state.TestOriginal.Text;
                if (SpecialTokenGuard.FindIn(_baseText, _o.specialTokens) is { } token)
                    return Stop(ExitCodes.EnvError, FailureKinds.EnvError,
                        $"테스트 파일에 모델 제어 토큰 문자열 '{token}'이(가) 있어 안전하게 편집할 수 없습니다: {location.FilePath}");
                if (_state.TestOriginal.MixedNewLines)
                    _ctx.Messages.Add("테스트 파일에 CRLF와 LF가 섞여 있습니다 — 테스트를 쓰는 동안 줄바꿈이 "
                        + (_state.TestOriginal.NewLine == "\n" ? "LF" : "CRLF") + "로 통일됩니다 (되돌릴 때는 원래 바이트로 돌아갑니다)");
            }
            else
            {
                // The skeleton takes the target's namespace and those of its usings the test project surely sees (workspace-declared
                // namespaces, core BCL), so the model need not guess them (C1) and never inherits an unreferenced package's using.
                var declared = TestClassWriter.DeclaredNamespaces(Fs, _ctx.Workspace!.Root);
                _baseText = TestClassWriter.NewFile(location.Namespace, location.ClassName, _framework,
                    TestClassWriter.TargetUsings(_ctx.Current.Text, _ctx.Language!.Version, declared));
                FileSnapshot csproj;
                try
                {
                    csproj = _o.fileGuard.Read(single);
                }
                catch (InvalidDataException ex)
                {
                    return Stop(ExitCodes.EnvError, FailureKinds.EnvError, ex.Message);
                }
                var relative = Fs.Path.GetRelativePath(Fs.Path.GetDirectoryName(single)!, location.FilePath).Replace('/', '\\');
                try
                {
                    _csprojAdded = TestClassWriter.AddCompileInclude(csproj.Text, relative);
                }
                catch (InvalidOperationException ex)
                {
                    return Stop(ExitCodes.EnvError, FailureKinds.EnvError, $"테스트 csproj에 새 파일을 넣을 수 없습니다: {ex.Message}");
                }
                if (_csprojAdded is not null) _state.CsprojOriginal = csproj;
                _newFileLike = csproj;
            }

            var testProjectName = Fs.Path.GetFileNameWithoutExtension(single);
            Status(RunPhase.Locate, 0, $"대상: {candidate.Display}");
            Status(RunPhase.Locate, 0, $"테스트 프로젝트: {testProjectName} ({FrameworkLabel(_framework.Framework)})");
            Status(RunPhase.Locate, 0, $"테스트 클래스: {Fs.Path.GetRelativePath(_ctx.Workspace!.Root, location.FilePath)} › {location.ClassName} "
                + (location.IsNew ? "(새 파일)" : "(기존)"));
            return null;
        }

        private string _methodName = "";
        private TestLibraries _libraries;
        private string _typeName = "";
        private FileSnapshot? _newFileLike;

        /// <summary>Awaits the baseline tests LOCATE started and checks they can be compared with; null to go on.</summary>
        private async Task<int?> BaselineAsync(CancellationToken ct)
        {
            Status(RunPhase.Verify, 0, "원본 상태 테스트 실행 중…");
            if (_ctx.BaselineTests is not { } running)
                return Stop(ExitCodes.EnvError, FailureKinds.CharTestBaselineUnusable, "원본 상태 테스트를 시작하지 못했습니다");
            var start = _o.time.GetTimestamp();
            var runs = await running.WaitAsync(ct);
            AddSeconds(RunPhase.Verify, start);
            if (runs.Count == 0)
                return Stop(ExitCodes.EnvError, FailureKinds.CharTestBaselineUnusable, "원본 상태 빌드에 실패해 테스트를 실행하지 못했습니다");
            _baseline = runs[0];
            if (CharTestRunJudge.BaselineUsable(_baseline) is { } why)
            {
                var first = _baseline.BuildErrors.Count > 0 ? $" — {Describe(_baseline.BuildErrors[0])}" : "";
                return Stop(ExitCodes.EnvError, FailureKinds.CharTestBaselineUnusable, $"{why}{first}. 원본 상태를 판정할 수 없어 테스트를 만들지 않습니다");
            }
            if (!_baseline.PerTestResults)
                return Stop(ExitCodes.EnvError, FailureKinds.CharTestBaselineUnusable, NoPerTestResultsMessage);
            return null;
        }

        /// <summary>The prompt parts that do not change between attempts.</summary>
        private PromptParts BuildPromptParts()
        {
            var candidate = _ctx.Candidate!;
            var source = _ctx.Current.Text;
            var lines = source.Split('\n');
            var targetSource = string.Join('\n', lines[(candidate.Span.StartLine - 1)..candidate.Span.EndLine]);
            var version = _ctx.Language!.Version;
            var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(version));
            var ns = tree.GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString() ?? "(global)";

            string construction;
            try
            {
                var slice = RoslynSlicer.Build(source, candidate, _ctx.Language, _o.tokens, int.MaxValue, []);
                construction = slice.Slice.Included.FirstOrDefault(p => p.Kind == "fields")?.Text ?? "(no constructors or fields)";
            }
            catch (InvalidOperationException)
            {
                construction = "(no constructors or fields)";
            }

            var searchRoot = _ctx.Target!.ProjectPath is { } project ? Fs.Path.GetDirectoryName(project) : Fs.Path.GetDirectoryName(_ctx.FilePath);
            string? dependencies = null, dto = null;
            if (!string.IsNullOrEmpty(searchRoot))
            {
                dependencies = DependencySignatureCollector.Collect(Fs, searchRoot, source, candidate, version);
                try
                {
                    dto = DtoCollector.Collect(Fs, searchRoot, source, candidate, version, withNamespace: true);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    dto = null;
                }
            }

            var testClass = TestClassContext.Of(_baseText, _state.Location!.ClassName, _framework, _testLanguage.Version);
            var langVersion = _testLanguage.Hint.StartsWith("C# ", StringComparison.Ordinal) ? _testLanguage.Hint[3..] : _testLanguage.Hint;
            return new PromptParts(targetSource, ns, construction, dependencies, dto, testClass, langVersion, string.IsNullOrEmpty(searchRoot) ? null : searchRoot);
        }

        /// <param name="SearchRoot">Where the dependency and dto types were looked up (the target's project folder), also used to
        /// find the namespace of a type the test file misses.</param>
        private sealed record PromptParts(string TargetSource, string Namespace, string Construction, string? Dependencies, string? Dto,
            TestClassContext TestClass, string LangVersion, string? SearchRoot);

        /// <summary>One attempt. Passed: every verdict passed and the tests are written. Exit: the run ends with that code.</summary>
        private async Task<(bool Passed, int? Exit)> AttemptAsync(int attempt, int max, PromptParts parts, CancellationToken ct)
        {
            var location = _state.Location!;
            // The summary describes the last attempt.
            _state.Generated = 0;
            _state.PassedOnCurrent = 0;
            _state.StableOnRerun = null;
            _state.KilledByMutation = 0;
            Status(RunPhase.Edit, attempt, $"테스트 생성 (시도 {attempt}/{max})");
            var editStart = _o.time.GetTimestamp();
            var used = _ctx.Feedback;
            var input = new CharTestPromptInput(parts.TargetSource, _ctx.Candidate!.Display, _typeName, parts.Namespace, parts.Construction,
                parts.TestClass.Helpers, parts.Dependencies, parts.Dto, parts.TestClass.Example, parts.TestClass.Usings, _framework,
                _request.Count, _request.Hint, used) { LangVersion = parts.LangVersion };
            var prompt = _o.prompts.Build(input);
            _ctx.Prompt = prompt;
            if (prompt.Error != PromptBudgetError.None)
                return (false, BudgetStop(prompt));
            _ctx.Feedback = null;

            GenerationResult gen;
            try
            {
                gen = await _o.client.GenerateEditAsync(prompt.BasePrompt, attempt, ct,
                    elapsed => _o.ui.ShowWaiting(RunPhase.Edit, attempt,
                        $"서버 응답 대기 중 ({elapsed.TotalSeconds:0}초) — 다른 요청을 처리 중이거나 모델을 불러오는 중일 수 있습니다"),
                    AnswerPrefix);
            }
            catch (LlmUnavailableException ex)
            {
                return (false, Stop(ExitCodes.EnvError, FailureKinds.EnvError, $"LLM 서버를 사용할 수 없습니다: {ex.Message} (`pelly doctor`로 확인하세요)"));
            }
            _ctx.Generation = gen;
            _ctx.ModelOutput = gen.Output;
            BestEffort(() => _o.logger.Attempt(_ctx, prompt.BasePrompt, gen, used));
            var editElapsed = AddSeconds(RunPhase.Edit, editStart);

            var (output, parseError) = CharTestOutputParser.Parse(gen.Output, _testLanguage.Version);
            if (output is null)
            {
                _ctx.FormatErrors++;
                Retry(attempt, FailureKinds.CharTestShape, _o.feedback.ForParse(parseError!), [Verdict("형태", false)], [parseError!]);
                LogPhase(RunPhase.Edit, PhaseOutcome.FormatError, editElapsed);
                return (false, null);
            }

            var names = TestClassContext.TestMethodNames(output, _framework, _testLanguage.Version);
            _state.Generated = names.Count;
            var rejections = CharTestStaticJudge.Judge(output, _framework, _request.Count, _methodName, parts.TestClass.MemberNames, _testLanguage.Version,
                _libraries);
            if (rejections.Count > 0)
            {
                var kind = rejections[0].Kind;
                var segments = kind switch
                {
                    FailureKinds.CharTestShape => new[] { Verdict("형태", false) },
                    FailureKinds.CharTestNoAssert => [Verdict("형태", true), Verdict("단언·호출", false)],
                    _ => [Verdict("형태", true), Verdict("단언·호출", true), Verdict("환경 의존 없음", false)],
                };
                Retry(attempt, kind, _o.feedback.ForRejections(rejections), segments,
                    rejections.Select(r => r.TestName is null ? r.Reason : $"{r.TestName}: {r.Reason}").ToList());
                LogPhase(RunPhase.Edit, PhaseOutcome.ShapeRejected, editElapsed);
                return (false, null);
            }
            LogPhase(RunPhase.Edit, PhaseOutcome.Ok, editElapsed);
            _state.NewTestNames = names.Select(n => $"{location.ClassName}.{n}").ToList();
            var newNames = _state.NewTestNames;
            var count = newNames.Count;

            var newText = TestClassWriter.Insert(_baseText, location.ClassName, output.Usings, output.Members);
            if (await WriteTestAsync(newText) is { } writeExit) return (false, writeExit);

            var verifyStart = _o.time.GetTimestamp();
            List<string> done = [Verdict("형태", true)];

            // Verdicts 4-5: build and pass on the current code.
            Status(RunPhase.Verify, attempt, "빌드·테스트 실행 (현재 코드)");
            var current = await RunTestsAsync($"chartest-{attempt}-current", ct);
            var onCurrent = CharTestRunJudge.OnCurrent(_baseline, current, newNames, location.FilePath);
            if (onCurrent.FailureKind == FailureKinds.BuildUnverifiable)
                return (false, EnvironmentStop(onCurrent, current, "테스트 파일 밖에서 빌드 오류가 났습니다"));
            if (onCurrent.FailureKind == FailureKinds.CharTestBuild)
            {
                var errors = current.BuildErrors;
                foreach (var code in errors.Select(e => e.Code).Distinct()) if (!_ctx.ErrorCodes.Contains(code)) _ctx.ErrorCodes.Add(code);
                var relative = Fs.Path.GetRelativePath(_ctx.Workspace!.Root, location.FilePath);
                var version = _ctx.Language!.Version;
                var hints = CharTestBuildHints.For(errors, relative, parts.Dependencies, missing => parts.SearchRoot is { } root
                    ? DependencySignatureCollector.NamespacesOf(Fs, root, missing, version, ct: ct)
                    : new Dictionary<string, IReadOnlyList<string>>());
                return await RetryAfterRunAsync(attempt, verifyStart, FailureKinds.CharTestBuild, _o.feedback.ForBuild(errors, relative, newText, hints),
                    [.. done, Verdict("빌드", false)], errors.Take(3).Select(Describe).ToList());
            }
            done.Add(Verdict("빌드", true));
            if (onCurrent.FailureKind == FailureKinds.CharTestFailsOnCurrent)
            {
                _state.PassedOnCurrent = count - onCurrent.FailingTests.Count;
                return await RetryAfterRunAsync(attempt, verifyStart, FailureKinds.CharTestFailsOnCurrent, _o.feedback.ForFailsOnCurrent(onCurrent.Details),
                    [.. done, $"현재 코드에서 통과 {_state.PassedOnCurrent}/{count} ✗"],
                    onCurrent.Details.Select(d => $"{ShortName(d.FullName)}: {d.Message ?? "실패"}").ToList());
            }
            if (onCurrent.FailureKind == FailureKinds.CharTestRegression)
            {
                _state.PassedOnCurrent = count;
                return await RetryAfterRunAsync(attempt, verifyStart, FailureKinds.CharTestRegression, _o.feedback.ForRegression(onCurrent.Details),
                    [.. done, $"현재 코드에서 통과 {count}/{count} ✓", Verdict("기존 테스트 유지", false)],
                    onCurrent.Details.Select(d => $"{ShortName(d.FullName)}: 기존 테스트가 새로 실패함").ToList());
            }
            _state.PassedOnCurrent = count;
            done.Add($"현재 코드에서 통과 {count}/{count} ✓");

            // Verdict 6: the same results on a second run.
            Status(RunPhase.Verify, attempt, "재실행 (결과 일치 확인)");
            var second = await RunTestsAsync($"chartest-{attempt}-rerun", ct);
            var stable = CharTestRunJudge.Stable(current, second, newNames);
            if (stable.FailureKind == FailureKinds.BuildUnverifiable)
                return (false, EnvironmentStop(stable, second, "재실행이 테스트까지 가지 못했습니다"));
            if (stable.FailureKind == FailureKinds.CharTestFlaky)
            {
                _state.StableOnRerun = false;
                return await RetryAfterRunAsync(attempt, verifyStart, FailureKinds.CharTestFlaky, _o.feedback.ForFlaky(stable.FailingTests),
                    [.. done, Verdict("재실행 일치", false)], stable.FailingTests.Select(n => $"{ShortName(n)}: 비결정적인 테스트 (두 실행의 결과가 다름)").ToList());
            }
            _state.StableOnRerun = true;
            done.Add(Verdict("재실행 일치", true));

            // Verdict 7: with the target throwing, every new test fails.
            var (mutated, mutationExit) = await MutateAndRunAsync(attempt, ct);
            if (mutationExit is { } stop) return (false, stop);
            var underMutation = CharTestRunJudge.UnderMutation(mutated!, newNames);
            if (underMutation.FailureKind == FailureKinds.CharTestMutationUnbuildable)
            {
                // Not the model's fault (analyzers, warnings as errors): "cannot judge" never counts as a pass, and no retry helps.
                var why = mutated!.BuildErrors.Count > 0 ? Describe(mutated.BuildErrors[0])
                    : mutated.Error ?? (mutated.TimedOut ? "시간 초과" : "새 테스트의 결과가 없습니다");
                ShowVerdict(attempt, [.. done, "변이 검출 판정 불가 ✗"], []);
                _state.RejectedBy = Numbered(FailureKinds.CharTestMutationUnbuildable);
                LogPhase(RunPhase.Verify, PhaseOutcome.EnvError, AddSeconds(RunPhase.Verify, verifyStart));
                return (false, Stop(ExitCodes.EnvError, FailureKinds.CharTestMutationUnbuildable,
                    $"대상 메서드를 예외로 바꾼 코드가 빌드·실행되지 않아 변이 검사를 판정할 수 없습니다: {why}"));
            }
            if (underMutation.FailureKind == FailureKinds.CharTestSurvivesMutation)
            {
                _state.KilledByMutation = count - underMutation.FailingTests.Count;
                return await RetryAfterRunAsync(attempt, verifyStart, FailureKinds.CharTestSurvivesMutation, _o.feedback.ForSurvivors(underMutation.FailingTests),
                    [.. done, $"변이 검출 {_state.KilledByMutation}/{count} ✗"],
                    underMutation.FailingTests.Select(n => $"{ShortName(n)}: 대상이 예외를 던지게 바꿔도 통과함").ToList());
            }
            _state.KilledByMutation = count;
            _state.RejectedBy = null;
            _ctx.FailureKind = null;
            ShowVerdict(attempt, [.. done, $"변이 검출 {count}/{count} ✓"], []);
            LogPhase(RunPhase.Verify, PhaseOutcome.Ok, AddSeconds(RunPhase.Verify, verifyStart));
            return (true, null);
        }

        private int BudgetStop(PromptBuildResult prompt)
        {
            if (prompt.Error == PromptBudgetError.RequestTooLong)
                return Stop(ExitCodes.EnvError, FailureKinds.BudgetExceeded,
                    $"힌트가 너무 깁니다 ({prompt.Detail}). {_o.options.Budget.Request}토큰 이내로 줄이세요");
            if (prompt.Error == PromptBudgetError.TotalTooLong && prompt.Detail?.StartsWith("target is too large", StringComparison.Ordinal) == true)
                return Stop(ExitCodes.LocateFailed, FailureKinds.BudgetExceeded,
                    $"대상 메서드가 문맥 예산보다 큽니다 ({prompt.Detail}). 더 작은 메서드를 지정하세요");
            return Stop(ExitCodes.EnvError, FailureKinds.BudgetExceeded, $"프롬프트가 입력 예산을 넘습니다: {prompt.Detail}");
        }

        private int EnvironmentStop(RunVerdict verdict, TestRun run, string fallback)
        {
            var detail = verdict.Detail ?? (run.BuildErrors.Count > 0 ? $"{fallback}: {Describe(run.BuildErrors[0])}" : fallback);
            return Stop(ExitCodes.EnvError, FailureKinds.BuildUnverifiable, detail);
        }

        /// <summary>A failed run verdict: the test file goes back to its original bytes, then the next attempt gets the feedback.</summary>
        private async Task<(bool, int?)> RetryAfterRunAsync(int attempt, long verifyStart, string kind, Feedback next, IReadOnlyList<string> segments,
            IReadOnlyList<string> details)
        {
            Retry(attempt, kind, next, segments, details);
            LogPhase(RunPhase.Verify, PhaseOutcome.NewErrors, AddSeconds(RunPhase.Verify, verifyStart));
            if (!await RevertTestAsync())
                return (false, Stop(ExitCodes.EnvError, FailureKinds.FileChangedExternally, "테스트 파일을 원래대로 되돌리지 못해 더 진행하지 않습니다"));
            return (false, null);
        }

        private void Retry(int attempt, string kind, Feedback next, IReadOnlyList<string> segments, IReadOnlyList<string> details)
        {
            _ctx.Feedback = next;
            _lastFeedback = next;
            _ctx.FailureKind = kind;
            _state.RejectedBy = Numbered(kind);
            ShowVerdict(attempt, segments, details);
        }

        /// <summary>"&lt;verdict number&gt;:&lt;failure kind&gt;" (design §4.6 <c>rejectedBy</c>).</summary>
        private static string Numbered(string kind) => kind switch
        {
            FailureKinds.CharTestShape => "1:",
            FailureKinds.CharTestNoAssert => "2:",
            FailureKinds.CharTestEnvDependent => "3:",
            FailureKinds.CharTestBuild => "4:",
            FailureKinds.CharTestFailsOnCurrent or FailureKinds.CharTestRegression => "5:",
            FailureKinds.CharTestFlaky => "6:",
            _ => "7:",
        } + kind;

        private void ShowVerdict(int attempt, IReadOnlyList<string> segments, IReadOnlyList<string> details)
        {
            var text = "판정: " + string.Join(" · ", segments);
            if (details.Count > 0) text += "\n" + string.Join("\n", details.Select(d => "  " + d));
            Status(RunPhase.Verify, attempt, text);
            _ctx.Record(text);
        }

        private static string Verdict(string label, bool ok) => $"{label} {(ok ? "✓" : "✗")}";

        private async Task<TestRun> RunTestsAsync(string name, CancellationToken ct) =>
            await _o.tests.RunAsync(_ctx.BuildTool!, _testProject, _ctx.Workspace!.Root, TestStep.TrxPath(_ctx, name), ct);

        /// <summary>
        /// Writes the mutated target, runs the tests, and restores the target in a <c>finally</c> that ignores cancellation. Exit is
        /// set when the target could not be mutated or restored.
        /// </summary>
        private async Task<(TestRun? Run, int? Exit)> MutateAndRunAsync(int attempt, CancellationToken ct)
        {
            var mutatedText = CharTestTarget.Mutate(_ctx.Original.Text, _ctx.Candidate!, _ctx.Language!.Version);
            _state.TargetBackupPath ??= _o.fileGuard.Backup(_ctx.Workspace!, _ctx.RunId, _ctx.Original);
            _state.TargetMutating = true;
            // Strict: a killed process must leave a marker that names the mutated target, or the target is not touched at all.
            if (WritePending() is { } markerError)
            {
                _state.TargetMutating = false;
                return (null, Stop(ExitCodes.EnvError, FailureKinds.EnvError,
                    $"미완료 표시를 갱신하지 못해 변이 검사를 하지 않습니다 ({markerError}): {_state.PendingPath}"));
            }
            TestRun? run = null;
            int? exit = null;
            try
            {
                var written = await _o.fileGuard.WriteAsync(_ctx.Current, mutatedText, CancellationToken.None);
                if (written.Status != FileWriteStatus.Written)
                {
                    // Nothing was written: the target is not pelly's to restore (a change made outside stays as it is).
                    _state.TargetMutating = false;
                    UpdatePending();
                    exit = Stop(ExitCodes.EnvError, FailureKinds.FileChangedExternally,
                        $"변이를 쓰지 못했습니다 ({written.Detail}). 대상 파일: {_ctx.FilePath}");
                }
                else
                {
                    _targetWritten = true;
                    _state.TargetMutated = written.Snapshot;
                    _ctx.Current = written.Snapshot!;
                    Status(RunPhase.Verify, attempt, "변이 검사 (대상 메서드를 예외로 바꾸고 빌드·테스트)");
                    run = await RunTestsAsync($"chartest-{attempt}-mutation", ct);
                }
            }
            finally
            {
                if (!await RestoreTargetAsync() && exit is null)
                    exit = Stop(ExitCodes.EnvError, FailureKinds.FileChangedExternally,
                        $"대상 파일을 원래대로 되돌리지 못했습니다. 원본은 {_state.TargetBackupPath}에 있습니다. 확인한 뒤 미완료 표시를 지우세요");
            }
            return (run, exit);
        }

        /// <summary>
        /// Puts the original target bytes back (never over a change made outside pelly) and checks the hash. True when the target on
        /// disk is the original. Ignores cancellation.
        /// </summary>
        private async Task<bool> RestoreTargetAsync()
        {
            if (!_state.TargetMutating) return true;
            if (_targetRestoreFailed) return false; // already reported; a second try would only repeat the message
            if (_state.TargetMutated is { } mutated)
            {
                try
                {
                    var result = await _o.fileGuard.RestoreAsync(_state.TargetBackupPath!, mutated, CancellationToken.None);
                    if (result.Status == FileWriteStatus.Written)
                    {
                        _ctx.Current = result.Snapshot!;
                        _state.TargetMutated = null;
                    }
                    else
                    {
                        _ctx.Messages.Add($"대상 파일을 되돌리지 못했습니다 ({result.Detail}). 원본은 {_state.TargetBackupPath}에 있습니다.");
                    }
                }
                catch (Exception ex)
                {
                    _ctx.Messages.Add($"대상 파일을 되돌리지 못했습니다 ({ex.GetType().Name}: {ex.Message}). 원본은 {_state.TargetBackupPath}에 있습니다.");
                }
            }

            if (!DiskHashIs(_ctx.FilePath, _ctx.Original.Sha256))
            {
                _targetRestoreFailed = true;
                return false;
            }
            _state.TargetMutating = false;
            _state.TargetMutated = null;
            UpdatePending();
            return true;
        }

        private bool DiskHashIs(string path, string sha)
        {
            try
            {
                return Fs.File.Exists(path) && TextFileCodec.HashHex(Fs.File.ReadAllBytes(path)) == sha;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        /// <summary>Writes the marker (first time), backs up and writes the test file and, for a new legacy file, the csproj.</summary>
        private async Task<int?> WriteTestAsync(string newTextLf)
        {
            var location = _state.Location!;
            var ws = _ctx.Workspace!;
            if (_state.PendingPath is null)
            {
                // Before the first byte is written: a killed process leaves the marker that blocks the next run until a person looks.
                if (_state.TestOriginal is { } original) _state.TestBackupPath = _o.fileGuard.Backup(ws, _ctx.RunId, original);
                if (_state.CsprojOriginal is { } csproj) _state.CsprojBackupPath = _o.fileGuard.Backup(ws, _ctx.RunId, csproj);
                _state.PendingPath = _pending.Write(ws, _ctx.RunId, _state.PendingFiles(_ctx.FilePath, _ctx.Original.Sha256));
            }

            if (_state.CsprojOriginal is { } project && _state.CsprojCurrent is null)
            {
                var result = await _o.fileGuard.WriteAsync(project, _csprojAdded!, CancellationToken.None);
                if (result.Status != FileWriteStatus.Written)
                    return Stop(ExitCodes.EnvError, FailureKinds.FileChangedExternally, $"테스트 csproj를 쓰지 못했습니다 ({result.Detail})");
                _state.CsprojCurrent = result.Snapshot;
            }

            if (!location.IsNew)
            {
                var result = await _o.fileGuard.WriteAsync(_state.TestOriginal!, newTextLf, CancellationToken.None);
                if (result.Status != FileWriteStatus.Written)
                    return Stop(ExitCodes.EnvError, FailureKinds.FileChangedExternally, $"테스트 파일을 쓰지 못했습니다 ({result.Detail})");
                _state.TestCurrent = result.Snapshot;
                return null;
            }

            if (Fs.File.Exists(location.FilePath))
                return Stop(ExitCodes.EnvError, FailureKinds.FileChangedExternally, $"새로 만들 테스트 파일이 실행 중에 생겼습니다: {location.FilePath}");
            byte[] bytes;
            try
            {
                bytes = TextFileCodec.Encode(NewFileLike(location.FilePath), newTextLf);
            }
            catch (UnencodableTextException ex)
            {
                return Stop(ExitCodes.EnvError, FailureKinds.Unencodable, ex.Message);
            }
            // Recorded before the bytes land, so a failed write is still cleaned up.
            _state.TestCurrent = TextFileCodec.Decode(location.FilePath, bytes);
            Fs.File.WriteAllBytes(location.FilePath, bytes);
            return null;
        }

        /// <summary>A new test file follows the test csproj's BOM and line breaks (UTF-8).</summary>
        private FileSnapshot NewFileLike(string path)
        {
            var like = _newFileLike;
            return new FileSnapshot(path, new UTF8Encoding(false), like?.HasBom ?? false, like?.NewLine ?? "\n", true, "", "");
        }

        /// <summary>The test file back to its original bytes (a new file deleted). False when it changed outside pelly or could not be restored.</summary>
        private async Task<bool> RevertTestAsync()
        {
            if (_state.TestCurrent is not { } current) return true;
            var location = _state.Location!;
            try
            {
                if (location.IsNew)
                {
                    if (!Fs.File.Exists(location.FilePath))
                    {
                        _state.TestCurrent = null;
                        return true;
                    }
                    if (!DiskHashIs(location.FilePath, current.Sha256))
                    {
                        _ctx.Messages.Add($"새로 만든 테스트 파일이 pelly 밖에서 바뀌어 지우지 않았습니다: {location.FilePath}");
                        return false;
                    }
                    Fs.File.Delete(location.FilePath);
                    _state.TestCurrent = null;
                    return true;
                }

                var result = await _o.fileGuard.RestoreAsync(_state.TestBackupPath!, current, CancellationToken.None);
                if (result.Status == FileWriteStatus.Written)
                {
                    _state.TestCurrent = null;
                    return true;
                }
                _ctx.Messages.Add($"테스트 파일을 되돌리지 못했습니다 ({result.Detail}). 원본은 {_state.TestBackupPath}에 있습니다.");
            }
            catch (Exception ex)
            {
                _ctx.Messages.Add($"테스트 파일을 되돌리지 못했습니다 ({ex.GetType().Name}: {ex.Message}). 원본은 {_state.TestBackupPath ?? location.FilePath}에 있습니다.");
            }
            return false;
        }

        private async Task<bool> RevertCsprojAsync()
        {
            if (_state.CsprojCurrent is not { } current) return true;
            try
            {
                var result = await _o.fileGuard.RestoreAsync(_state.CsprojBackupPath!, current, CancellationToken.None);
                if (result.Status == FileWriteStatus.Written)
                {
                    _state.CsprojCurrent = null;
                    return true;
                }
                _ctx.Messages.Add($"테스트 csproj를 되돌리지 못했습니다 ({result.Detail}). 원본은 {_state.CsprojBackupPath}에 있습니다.");
            }
            catch (Exception ex)
            {
                _ctx.Messages.Add($"테스트 csproj를 되돌리지 못했습니다 ({ex.GetType().Name}: {ex.Message}). 원본은 {_state.CsprojBackupPath}에 있습니다.");
            }
            return false;
        }

        /// <summary>Shows the diffs and asks for approval (the same rules as REVIEW: <c>--yes</c> keeps, non-interactive rejects).</summary>
        private async Task<int> ReviewAsync(CancellationToken ct)
        {
            var location = _state.Location!;
            var count = _state.NewTestNames.Count;
            Status(RunPhase.Review, _ctx.Attempt, "변경 검토");
            var start = _o.time.GetTimestamp();
            _o.ui.ShowDiff(location.FilePath, _state.TestOriginal?.Text ?? "", _state.TestCurrent!.Text);
            if (_state.CsprojCurrent is { } csproj) _o.ui.ShowDiff(csproj.Path, _state.CsprojOriginal!.Text, csproj.Text);

            int exit;
            if (_request.DryRun)
            {
                _ctx.Messages.Add("dry-run: 판정을 모두 마쳤습니다. 테스트 파일을 원래대로 되돌립니다");
                exit = ExitCodes.Success;
            }
            else if (_request.Yes)
            {
                _state.Kept = true;
                exit = ExitCodes.Success;
            }
            else if (!_o.ui.IsInteractive)
            {
                _ctx.Messages.Add("비대화형 실행에서는 --yes 없이 테스트를 남기지 않습니다. 원래대로 되돌립니다.");
                _ctx.FailureKind = FailureKinds.Rejected;
                exit = ExitCodes.UserCancelled;
            }
            else if (await _o.ui.ApproveAsync($"이 테스트 {count}개를 남길까요?", ct))
            {
                _state.Kept = true;
                exit = ExitCodes.Success;
            }
            else
            {
                _ctx.FailureKind = FailureKinds.Rejected;
                exit = ExitCodes.UserCancelled;
            }
            LogPhase(RunPhase.Review, exit == 0 ? PhaseOutcome.Approved : PhaseOutcome.Rejected, AddSeconds(RunPhase.Review, start));
            return exit;
        }

        /// <summary>
        /// End of run: target restored (if a mutation is still in place), test file and csproj reverted unless kept, the target hash
        /// compared with the start, and the marker deleted only when all of that succeeded. Never throws; ignores cancellation.
        /// </summary>
        private async Task FinishFilesAsync()
        {
            try
            {
                var safe = await RestoreTargetAsync();
                if (!_state.Kept)
                {
                    safe &= await RevertTestAsync();
                    safe &= await RevertCsprojAsync();
                }

                if (_ctx.Original is not null && !DiskHashIs(_ctx.FilePath, _ctx.Original.Sha256))
                {
                    _ctx.Messages.Add($"대상 파일이 실행을 시작할 때와 다릅니다: {_ctx.FilePath}"
                        + (_state.TargetBackupPath is { } backup ? $" — 원본은 {backup}에 있습니다" : ""));
                    _ctx.FailureKind = FailureKinds.Crashed;
                    _ctx.ExitCode = ExitCodes.EnvError;
                    if (_targetWritten) safe = false;
                }
                if (!safe)
                {
                    _ctx.ExitCode = ExitCodes.EnvError;
                    if (_state.PendingPath is { } kept)
                        _ctx.Messages.Add($"되돌리지 못한 파일이 있어 미완료 표시를 남깁니다: {kept} — 파일을 확인한 뒤 지우세요");
                    return;
                }
                if (_state.PendingPath is { } marker)
                {
                    _pending.Delete(marker);
                    _state.PendingPath = null;
                }
            }
            catch (Exception ex)
            {
                _ctx.Messages.Add($"파일을 정리하는 중 오류가 났습니다 ({ex.GetType().Name}: {ex.Message}). 미완료 표시를 확인하세요");
                _ctx.ExitCode = ExitCodes.EnvError;
            }
        }

        /// <summary>Best-effort marker update (after a restore): a failure only adds a message.</summary>
        private void UpdatePending()
        {
            if (WritePending() is { } error)
                _ctx.Messages.Add($"미완료 표시를 갱신하지 못했습니다 ({error}): {_state.PendingPath}");
        }

        /// <summary>Rewrites the marker with the current file list; the error text, or null when written (or no marker yet).</summary>
        private string? WritePending()
        {
            if (_state.PendingPath is null) return null;
            try
            {
                _state.PendingPath = _pending.Write(_ctx.Workspace!, _ctx.RunId, _state.PendingFiles(_ctx.FilePath, _ctx.Original.Sha256));
                return null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return ex.Message;
            }
        }

        /// <summary>
        /// After a mutation, one best-effort build of the test project with the restored target, so the mutated output does not stay
        /// in bin/obj. Skipped after a cancel or when the target is not back to its original bytes; a failed or skipped build only
        /// adds <see cref="MutationOutputWarning"/> (the exit code does not change). Never throws.
        /// </summary>
        private async Task RebuildAfterMutationAsync(CancellationToken ct)
        {
            if (!_targetWritten) return;
            var rebuilt = false;
            try
            {
                var restored = !_state.TargetMutating && DiskHashIs(_ctx.FilePath, _ctx.Original.Sha256);
                if (restored && !ct.IsCancellationRequested && _ctx.BuildTool is { } tool && _testProject.Length > 0 && _ctx.Workspace is { } ws)
                {
                    Status(RunPhase.Verify, _ctx.Attempt, "원래 코드로 다시 빌드 (변이 빌드 결과물 정리)");
                    var run = await _o.builds.BuildAsync(tool, _testProject, ws.Root, CancellationToken.None);
                    rebuilt = !run.TimedOut && run.Errors.Count == 0;
                }
            }
            catch (Exception)
            {
                rebuilt = false;
            }
            if (!rebuilt) _ctx.Messages.Add(MutationOutputWarning);
        }

        /// <summary>Cancels the baseline build and tests and waits for them (as <see cref="FsmOrchestrator"/> does).</summary>
        private async Task StopBaselineAsync()
        {
            if (_ctx.BaselineCancellation is not { } cts) return;
            try
            {
                try
                {
                    cts.Cancel();
                }
                catch (AggregateException)
                {
                    // A cancellation callback threw; the processes are still being stopped.
                }
                var running = new List<Task>();
                if (_ctx.Baseline is { } baseline) running.Add(baseline);
                if (_ctx.BaselineTests is { } baselineTests) running.Add(baselineTests);
                if (running.Count > 0)
                {
                    try
                    {
                        await Task.WhenAll(running).WaitAsync(BaselineStopWait, _o.time, CancellationToken.None);
                    }
                    catch (TimeoutException)
                    {
                        _ctx.Messages.Add($"원본 상태 빌드·테스트가 {BaselineStopWait.TotalSeconds:0}초 안에 멈추지 않았습니다. 남은 빌드·테스트 프로세스가 있는지 확인하세요");
                    }
                    catch (Exception)
                    {
                        // Cancelled or failed: the result is no longer used.
                    }
                }
            }
            finally
            {
                cts.Dispose();
                _ctx.BaselineCancellation = null;
            }
        }

        private async Task<PhaseOutcome> PhaseAsync(RunPhase phase, int attempt, string text, Func<Task<PhaseOutcome>> run, bool muteSkipNotices = false)
        {
            Status(phase, attempt, text);
            _ctx.Phase = phase;
            var before = _ctx.Messages.Count;
            var start = _o.time.GetTimestamp();
            var outcome = await run();
            var elapsed = AddSeconds(phase, start);
            if (muteSkipNotices)
            {
                for (var i = before; i < _ctx.Messages.Count; i++)
                    if (SkipNoticePrefixes.Any(p => _ctx.Messages[i].StartsWith(p, StringComparison.Ordinal)))
                        _ctx.RecordOnlyMessages.Add(i);
            }
            LogPhase(phase, outcome, elapsed);
            Flush();
            return outcome;
        }

        private void LogPhase(RunPhase phase, PhaseOutcome outcome, TimeSpan elapsed)
        {
            _ctx.Phase = phase;
            if (!_logStarted && _ctx.Workspace is not null)
            {
                BestEffort(() => _o.logger.Start(_ctx));
                _logStarted = true;
            }
            BestEffort(() => _o.logger.Phase(_ctx, outcome, elapsed));
            Flush();
        }

        private TimeSpan AddSeconds(RunPhase phase, long start)
        {
            var elapsed = _o.time.GetElapsedTime(start);
            _ctx.PhaseSeconds[phase] = _ctx.PhaseSeconds.GetValueOrDefault(phase) + elapsed.TotalSeconds;
            return elapsed;
        }

        private int Stop(int code, string? kind, string message)
        {
            if (kind is not null) _ctx.FailureKind = kind;
            _ctx.Messages.Add(message);
            Flush();
            return code;
        }

        private void Status(RunPhase phase, int attempt, string message) => BestEffort(() => _o.ui.ShowStatus(phase, attempt, message));

        private void BestEffort(Action action)
        {
            try
            {
                action();
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ctx.Messages.Add($"로그·화면 출력에 실패했습니다: {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>Shows each message once (record-only ones are skipped); a failing display stops further display.</summary>
        private void Flush()
        {
            while (!_displayBroken && _shownMessages < _ctx.Messages.Count)
            {
                var index = _shownMessages++;
                if (_ctx.RecordOnlyMessages.Contains(index)) continue;
                try
                {
                    _o.ui.Warn(_ctx.Messages[index]);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _displayBroken = true;
                    _ctx.Messages.Add($"메시지를 화면에 표시하지 못했습니다: {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        private string AssemblyNameOf(string csproj)
        {
            try
            {
                var name = XDocument.Parse(Fs.File.ReadAllText(csproj)).Descendants()
                    .FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value.Trim();
                if (!string.IsNullOrEmpty(name)) return name;
            }
            catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
            {
                // fall back to the file name
            }
            return Fs.Path.GetFileNameWithoutExtension(csproj);
        }

        private static string ShortName(string fullName)
        {
            var name = TestNames.WithoutArguments(fullName);
            return name[(name.LastIndexOf('.') + 1)..];
        }

        private static string Describe(Diagnostic d) =>
            d.File.Length == 0 ? $"{d.Code}: {d.Message}" : $"{Path.GetFileName(d.File)}({d.Line},{d.Column}): {d.Code}: {d.Message}";

        private static string FrameworkLabel(TestFramework f) => f switch
        {
            TestFramework.XunitV2 => "xUnit v2",
            TestFramework.XunitV3 => "xUnit v3",
            TestFramework.MsTest => "MSTest",
            _ => "NUnit",
        };
    }
}
