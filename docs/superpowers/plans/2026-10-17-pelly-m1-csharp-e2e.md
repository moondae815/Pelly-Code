# Pelly-Coder M1 (C# 최소 E2E) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** SDK 스타일 C# 파일의 메서드 하나를 `pelly cs <파일> --method <이름> "<요청>"`으로 로컬 Ollama 모델이 고치고, 구문 검사·사용자 승인·롤백까지 FSM이 처리하는 최소 E2E를 만든다.

**Architecture:** `PellyCoder.Core`에 FSM(순수 전이 함수 + 단계 클래스), 파일 안전장치, SEARCH/REPLACE 패처, Roslyn 슬라이서, 프롬프트 조립·토큰 예산, Ollama 2단계 생성 클라이언트를 둔다. `PellyCoder.Cli`는 Spectre.Console 명령(`cs`, `doctor`)과 `IUserInteraction` 구현만 맡는다. VERIFY는 M1에서 Roslyn 구문 검사만 하고(빌드 검증은 M2), 대상은 `--method`(필요하면 `--class`)로 직접 지정한다(자동 탐색·`--line`은 M2).

**Tech Stack:** .NET 10, Spectre.Console.Cli, Microsoft.CodeAnalysis.CSharp, Microsoft.ML.Tokenizers 2.0.1, Microsoft.Extensions.Configuration·DependencyInjection, System.IO.Abstractions, DiffPlex, xUnit v3(MTP), Ollama 0.40.0(`/api/generate` raw)

**Spec:** `docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md` (§3 결정 D1~D23, §5~§8, §10.2, §11 M1 행). 요구사항 원본: `docs/PRD.md` v1.6. M0 측정 근거: `docs/spikes/S1~S5`.

## Global Constraints

* 런타임·빌드: `global.json` SDK `10.0.302`, 모든 `src/` 프로젝트 `net10.0`, `Nullable`·`ImplicitUsings` enable, `TreatWarningsAsErrors` true (루트 `Directory.Build.props`, 이미 있음).
* 패키지는 `Directory.Packages.props`(CPM, 간접 의존 고정 켜짐)에서만 버전 관리. 라이선스 MIT/Apache-2.0만. `System.Security.Cryptography.Pkcs` 10.0.13 고정 유지.
* **D20:** `PellyCoder.Core`는 `Console.*`을 쓰지 않는다. 모든 진행 표시·입력은 `IUserInteraction`으로만.
* **설계 §5:** `File.*`·`Directory.*`·`Process.Start`·`HttpClient` 생성은 각 구현 클래스 안에서만. 나머지는 `IFileSystem`(System.IO.Abstractions), `TimeProvider`, 주입된 `HttpClient`를 쓴다.
* **D21:** `llm.thinkMode` 기본 `retry` — 시도 1은 빈 think 프리필, 시도 2부터 THINK 단계. `off`/`always` 지원.
* **D22:** THINK 길이는 `num_predict = budget.think`(512)로만 제한. 클라이언트는 사용자 Ctrl+C 외에는 스트림을 끊지 않는다.
* **D23:** `stop`은 보내되 동작을 가정하지 않는다. THINK 응답은 첫 `</think>` 앞까지만 쓴다.
* 예산 기본값(§8.1): `inputTotal 4096, system 600, request 200, context 3000, feedback 296, think 512, codeOutput 2048`, `numCtx 8192`, `keepAlive "30m"`.
* 샘플링 기본값(§8.1): think `T 0.6 / TopP 0.95 / TopK 20 / repeat 1.0 / presence 0`, code `T 0.1 / TopP 0.9 / TopK 20 / repeat 1.05 / presence 0`.
* chat template(§8.2, S1): `docs/spikes/S1-template.json` 값 그대로(이미 `src/PellyCoder.Core/Llm/Resources/template.json`에 있음).
* 시스템 프롬프트는 영어(D9). 컨텍스트에 줄 번호를 넣지 않는다(§8.2).
* 종료 코드(PRD FR-00): 0 성공 / 1 검증 실패 / 2 사용자 거부·중단 / 3 대상 찾기 실패 / 4 환경 오류 / 5 안전 거부.
* 테스트: 환경이 필요한 테스트는 `tests/PellyCoder.Integration.Tests`에만 두고 `[WindowsFact]`/`[SqlServerFact]`/`[OllamaFact]`를 쓴다. 단위 테스트는 `tests/PellyCoder.Core.Tests`.
* 코드 식별자·커밋 메시지 영어, 문서 한국어. 커밋 끝에 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
* 실행 환경: 실제 모델 테스트는 `PELLY_TEST_OLLAMA=http://localhost:11434`, `PELLY_TEST_MODEL=<로컬 모델 태그>`(현재 `qwen3.6:35b-a3b-coding-nvfp4`)로 Mac에서만 돌린다.

## Review Focus

1. **모델이 생성하는 동안 사용자가 IDE에서 같은 파일을 저장한 경우** — APPLY에서 덮어쓰지 않고 종료 코드 4(FileChangedExternally)로 끝나며 사용자 편집이 그대로 남아야 한다. → Task 12 `ExternalEditDuringGeneration_DoesNotOverwrite`.
2. **모델 출력이 블록 앞뒤에 설명문이나 ``` 펜스를 붙인 경우** — 블록만 파싱되고 나머지는 무시돼야 한다. → Task 4 `Parse_IgnoresProseAndFences`.
3. **Ollama 서버가 꺼져 있거나 모델 태그가 없는 경우** — 파일을 건드리기 전에 종료 코드 4와 "무엇을 확인할지" 메시지로 끝나야 한다. → Task 12 `ServerDown_FailsInPreflightWithoutTouchingFile`.
4. **CP949·CRLF 파일(레거시 한글 주석)을 고친 경우** — 바뀐 줄 외의 바이트가 그대로이고 인코딩·줄바꿈이 유지돼야 한다. → Task 12 `Cp949CrLfFile_PreservesUnchangedBytes`.
5. **이미 패치를 쓴 뒤 교정 시도 중에 Ctrl+C를 누른 경우** — 원본 바이트로 복원되고 종료 코드 2여야 한다. → Task 12 `CancelAfterWrite_RestoresOriginalBytes`.

---

## 파일 구조

```text
src/PellyCoder.Core/
├── Abstractions/
│   ├── ILlmBackend.cs            # ILlmBackend, LlmRequest, LlmChunk, LlmServerStatus, SamplingOptions
│   ├── ITokenCounter.cs
│   └── IUserInteraction.cs
├── Config/
│   ├── PellyOptions.cs           # 옵션 클래스 전체 + ThinkMode
│   └── PellyOptionsLoader.cs     # 병합·검증
├── Models/                       # §6 레코드 (파일 하나에 관련 레코드 묶음)
│   ├── EditModels.cs             # EditKind, EditTarget, TextSpanInfo, EditableRegion, Slice, SlicePart
│   ├── PatchModels.cs            # PatchBlock, MatchStage, PatchOutcome, PatchFailure
│   └── VerifyModels.cs           # Diagnostic, VerifyOutcome, Feedback
├── Fsm/
│   ├── RunPhase.cs               # RunPhase, PhaseOutcome, Transition, ExitCodes
│   ├── Transitions.cs
│   ├── RunRequest.cs             # RunRequest, RunContext
│   ├── IPhase.cs
│   ├── Phases/                   # PreflightPhase … ReviewPhase (단계당 파일 하나)
│   └── FsmOrchestrator.cs
├── Workspace/
│   ├── WorkspaceLocator.cs       # Workspace 레코드 포함
│   └── WorkspaceLock.cs
├── Files/
│   ├── FileSnapshot.cs           # FileSnapshot + TextFileCodec
│   ├── FileGuard.cs
│   └── SharingViolationRetry.cs
├── Patchers/
│   ├── SearchReplaceParser.cs
│   ├── SearchReplacePatcher.cs
│   └── MarkerScanner.cs
├── CSharp/
│   ├── ProjectFileLocator.cs
│   ├── LanguageVersionDetector.cs
│   ├── MemberLocator.cs
│   ├── RoslynSlicer.cs
│   └── SyntaxVerifier.cs
├── Llm/
│   ├── QwenTokenCounter.cs
│   ├── QwenChatTemplate.cs
│   ├── PromptResources.cs
│   ├── PromptBuilder.cs
│   ├── OllamaBackend.cs          # IOllamaDiagnostics 포함
│   ├── QwenStreamClient.cs
│   └── Resources/                # template.json(있음), vocab.json, merges.txt, tokenizer-meta.json, prompts/*.txt
├── Feedback/
│   └── FeedbackBuilder.cs
├── Logging/
│   └── RunLogger.cs              # RunSummary 포함
└── Doctor/
    └── DoctorService.cs
src/PellyCoder.Cli/
├── Program.cs
├── Commands/CsCommand.cs, DoctorCommand.cs
├── Console/SpectreUserInteraction.cs, DiffRenderer.cs
└── Composition.cs                # DI 등록
tests/PellyCoder.Core.Tests/      # 각 구성 요소 이름 + Tests.cs, Fakes/
tests/PellyCoder.Integration.Tests/
├── Files/FileGuardWindowsTests.cs
└── E2E/CsEndToEndTests.cs
```

---

### Task 1: 공용 모델과 FSM 전이 함수

**Files:**
- Create: `src/PellyCoder.Core/Models/EditModels.cs`, `PatchModels.cs`, `VerifyModels.cs`
- Create: `src/PellyCoder.Core/Fsm/RunPhase.cs`, `src/PellyCoder.Core/Fsm/Transitions.cs`
- Test: `tests/PellyCoder.Core.Tests/Fsm/TransitionsTests.cs`

**Interfaces:**
- Produces:
  * §6의 레코드 그대로: `EditKind`, `EditTarget(EditKind Kind, string FilePath, string SymbolDisplay, TextSpanInfo MemberSpan, string? ProjectPath)`, `TextSpanInfo(int StartLine, int EndLine)`(1-based 포함), `EditableRegion(TextSpanInfo Span, string Reason)`, `SlicePart(string Kind, string Text, int Tokens, int Priority)`, `Slice(IReadOnlyList<SlicePart> Included, IReadOnlyList<SlicePart> Omitted, string LanguageHint)`, `PatchBlock(string Search, string Replace)`, `MatchStage { Exact = 1, TrailingWhitespace = 2, Indentation = 3 }`, `PatchOutcome(bool Success, string? NewText, IReadOnlyList<MatchStage> Stages, PatchFailure? Failure)`, `PatchFailure(string Kind, string Detail, string? NearestOriginal)`, `Diagnostic(string File, int Line, int Column, string Severity, string Code, string Message)`, `VerifyOutcome(bool Passed, IReadOnlyList<Diagnostic> NewErrors, IReadOnlyList<Diagnostic> NewWarnings, IReadOnlyList<string> Notices)`, `Feedback(string Text, int Tokens)`.
  * `PatchFailure.Kind` 값 상수: `static class PatchFailureKinds { Parse, NotFound, Ambiguous, OutsideRegion, Overlap, Unencodable }` (문자열 상수).
  * `enum RunPhase { Preflight, Locate, Slice, Edit, Apply, Verify, Review, Done, Failed }` (설계의 `Rollback`은 상태가 아니라 Failed 진입 시 오케스트레이터 동작으로 처리).
  * `enum PhaseOutcome { Ok, EnvError, NotFound, Ambiguous, BudgetExceeded, FormatError, MatchFailed, SafetyRejected, ShapeRejected, FileChangedExternally, NewErrors, Approved, Rejected, DryRunCompleted, Cancelled, Crashed }`
  * `sealed record Transition(RunPhase Next, int? ExitCode)` — `Next`가 `Done`/`Failed`일 때만 `ExitCode`가 있다.
  * `static class ExitCodes { const int Success = 0, VerifyFailed = 1, UserCancelled = 2, LocateFailed = 3, EnvError = 4, SafetyRejected = 5; }`
  * `static class Transitions { Transition Next(RunPhase current, PhaseOutcome outcome, int attempt, int maxRetries); bool IsTerminal(RunPhase p); }`

- [ ] **Step 1: 실패하는 테스트 작성** — 설계 §7.1 표의 모든 행을 `[Theory]`로:
  ```csharp
  [Theory]
  [InlineData(RunPhase.Preflight, PhaseOutcome.Ok, 0, RunPhase.Locate, null)]
  [InlineData(RunPhase.Preflight, PhaseOutcome.EnvError, 0, RunPhase.Failed, 4)]
  [InlineData(RunPhase.Locate, PhaseOutcome.Ok, 0, RunPhase.Slice, null)]
  [InlineData(RunPhase.Locate, PhaseOutcome.NotFound, 0, RunPhase.Failed, 3)]
  [InlineData(RunPhase.Locate, PhaseOutcome.Ambiguous, 0, RunPhase.Failed, 3)]
  [InlineData(RunPhase.Slice, PhaseOutcome.Ok, 0, RunPhase.Edit, null)]
  [InlineData(RunPhase.Slice, PhaseOutcome.BudgetExceeded, 0, RunPhase.Failed, 4)]
  [InlineData(RunPhase.Edit, PhaseOutcome.Ok, 1, RunPhase.Apply, null)]
  [InlineData(RunPhase.Edit, PhaseOutcome.BudgetExceeded, 1, RunPhase.Failed, 4)]
  [InlineData(RunPhase.Edit, PhaseOutcome.EnvError, 1, RunPhase.Failed, 4)]          // LLM 서버 끊김
  [InlineData(RunPhase.Edit, PhaseOutcome.FormatError, 1, RunPhase.Slice, null)]
  [InlineData(RunPhase.Edit, PhaseOutcome.FormatError, 4, RunPhase.Failed, 1)]
  [InlineData(RunPhase.Apply, PhaseOutcome.Ok, 1, RunPhase.Verify, null)]
  [InlineData(RunPhase.Apply, PhaseOutcome.MatchFailed, 3, RunPhase.Slice, null)]
  [InlineData(RunPhase.Apply, PhaseOutcome.MatchFailed, 4, RunPhase.Failed, 1)]
  [InlineData(RunPhase.Apply, PhaseOutcome.SafetyRejected, 4, RunPhase.Failed, 5)]
  [InlineData(RunPhase.Apply, PhaseOutcome.ShapeRejected, 4, RunPhase.Failed, 1)]
  [InlineData(RunPhase.Apply, PhaseOutcome.FileChangedExternally, 1, RunPhase.Failed, 4)]
  [InlineData(RunPhase.Apply, PhaseOutcome.EnvError, 1, RunPhase.Failed, 4)]
  [InlineData(RunPhase.Apply, PhaseOutcome.DryRunCompleted, 1, RunPhase.Done, 0)]
  [InlineData(RunPhase.Verify, PhaseOutcome.Ok, 1, RunPhase.Review, null)]
  [InlineData(RunPhase.Verify, PhaseOutcome.NewErrors, 2, RunPhase.Slice, null)]
  [InlineData(RunPhase.Verify, PhaseOutcome.NewErrors, 4, RunPhase.Failed, 1)]
  [InlineData(RunPhase.Review, PhaseOutcome.Approved, 1, RunPhase.Done, 0)]
  [InlineData(RunPhase.Review, PhaseOutcome.Rejected, 1, RunPhase.Failed, 2)]
  [InlineData(RunPhase.Edit, PhaseOutcome.Cancelled, 2, RunPhase.Failed, 2)]
  [InlineData(RunPhase.Verify, PhaseOutcome.Crashed, 2, RunPhase.Failed, 4)]
  public void Next_FollowsDesignTable(RunPhase cur, PhaseOutcome o, int attempt, RunPhase next, int? exit)
      => Assert.Equal(new Transition(next, exit), Transitions.Next(cur, o, attempt, maxRetries: 3));

  [Fact] public void Next_UndefinedPair_Throws()   // 예: Locate + Approved
      => Assert.Throws<InvalidOperationException>(() => Transitions.Next(RunPhase.Locate, PhaseOutcome.Approved, 0, 3));
  [Fact] public void Next_MaxRetriesZero_FailsOnFirstRetryableOutcome()
      => Assert.Equal(new Transition(RunPhase.Failed, 1), Transitions.Next(RunPhase.Apply, PhaseOutcome.MatchFailed, 1, 0));
  ```
  재시도 규칙: `attempt < 1 + maxRetries`이면 `Slice`. `attempt`는 EDIT 진입 횟수(Edit 단계가 올린 뒤의 값).

- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*TransitionsTests"` / Expected: 컴파일 실패

- [ ] **Step 3: 레코드·열거·`Transitions` 구현** — `switch` 식 하나로. `Cancelled`→Failed(2), `Crashed`→Failed(4), `EnvError`→Failed(4)는 모든 현재 상태에서 허용.

- [ ] **Step 4: 통과 확인** — 같은 명령 / Expected: 29개 통과 (Theory 27 + Fact 2)

- [ ] **Step 5: Commit** — `git commit -m "feat(core): add shared models and FSM transition table"`

---

### Task 2: 설정·워크스페이스·실행 잠금

**Files:**
- Create: `src/PellyCoder.Core/Config/PellyOptions.cs`, `PellyOptionsLoader.cs`
- Create: `src/PellyCoder.Core/Workspace/WorkspaceLocator.cs`, `WorkspaceLock.cs`
- Modify: `Directory.Packages.props` (`Microsoft.Extensions.Configuration`, `.Json`, `.Binder`, `System.IO.Abstractions`, `System.IO.Abstractions.TestingHelpers`, `Microsoft.Extensions.TimeProvider.Testing`) — 환경 변수·CLI 값은 직접 변환해 `AddInMemoryCollection`(기본 패키지에 포함)으로 넣는다
- Test: `tests/PellyCoder.Core.Tests/Config/PellyOptionsLoaderTests.cs`, `Workspace/WorkspaceLocatorTests.cs`, `Workspace/WorkspaceLockTests.cs`

**Interfaces:**
- Produces:
  * `enum ThinkMode { Off, Retry, Always }`
  * `sealed record SamplingOptions(double Temperature, double TopP, int TopK, double RepeatPenalty, double PresencePenalty)` (이 Task에서 `Abstractions/ILlmBackend.cs` 파일을 만들어 그 안에 둔다 — Task 9가 같은 파일에 인터페이스를 더한다).
  * 옵션 클래스(가변 프로퍼티 + 기본값, 바인더용): `PellyOptions { LlmOptions Llm; BudgetOptions Budget; SamplingSet Sampling; RetryOptions Retry; LogOptions Log; }`, `LlmOptions { string Endpoint; string Model; string KeepAlive; int NumCtx; Dictionary<string,string> Headers; int ConnectTimeoutSeconds = 3; int StreamIdleTimeoutSeconds = 10; string? Template; ThinkMode ThinkMode = ThinkMode.Retry; }`, `BudgetOptions { InputTotal, System, Request, Context, Feedback, Think, CodeOutput }`, `SamplingSet { SamplingOptions Think; SamplingOptions Code; }`, `RetryOptions { int Max = 3; }`, `LogOptions { int RetentionDays = 30; }` — 기본값은 Global Constraints.
  * `sealed record OptionsLoadResult(PellyOptions? Options, IReadOnlyList<string> Errors)`
  * `static class PellyOptionsLoader { OptionsLoadResult Load(IFileSystem fs, string? userConfigPath, string? workspaceConfigPath, IReadOnlyDictionary<string, string?> environment, IReadOnlyDictionary<string, string?> cliOverrides); }` — 키 형식은 `llm:endpoint` (환경 변수는 `PELLY__LLM__ENDPOINT` → `llm:endpoint`, 접두사 `PELLY__`만 읽음).
  * `static class PellyPaths { string UserConfigPath(); }` = `Path.Combine(Environment.GetFolderPath(SpecialFolder.ApplicationData), "pelly", "config.json")`.
  * `sealed record Workspace(string Root, string PellyDir)`, `static class WorkspaceLocator { Workspace Resolve(IFileSystem fs, string targetFilePath); void EnsurePellyDir(IFileSystem fs, Workspace ws); }`
  * `static class WorkspaceLock { IDisposable? TryAcquire(IFileSystem fs, Workspace ws); }` — `.pelly/lock`을 `FileShare.None`으로 연다. 이미 잠겨 있으면 `null`.

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  // PellyOptionsLoaderTests (MockFileSystem)
  [Fact] Defaults_MatchDesign()             // Budget.InputTotal==4096, System==600, Request==200, Context==3000, Feedback==296, Think==512, CodeOutput==2048, Llm.NumCtx==8192, Llm.KeepAlive=="30m", Llm.ThinkMode==Retry, Sampling.Think==new(0.6,0.95,20,1.0,0), Sampling.Code==new(0.1,0.9,20,1.05,0), Retry.Max==3
  [Fact] MergeOrder_UserThenWorkspaceThenEnvThenCli()   // 같은 키 llm:model을 네 곳에 다르게 두면 CLI 값; CLI 없으면 env; …
  [Fact] EnvironmentPrefix_OnlyPellyDoubleUnderscore()  // PELLY__LLM__MODEL 반영, LLM__MODEL·PELLYX__ 무시
  [Fact] UnknownKey_IsError()               // {"llm":{"modle":"x"}} → Errors에 "llm:modle"
  [Fact] InputParts_ExceedInputTotal_IsError()   // system+request+context+feedback > inputTotal → 오류 문자열에 "budget"
  [Fact] InputPlusOutput_ExceedNumCtx_IsError()  // inputTotal+think+codeOutput > numCtx
  [Fact] ThinkMode_ParsesCaseInsensitive()  // "always" → Always, "bogus" → 오류
  [Fact] MissingFiles_AreIgnored()
  [Fact] FutureSections_AreKnown()          // build/test/sql 섹션 키(설계 §8.1)는 M1에서 쓰지 않아도 오류가 아니다
  // WorkspaceLocatorTests
  [Fact] Root_IsNearestAncestorWithGitSvnOrSln()   // /r/.git, /r/a/b/f.cs → /r ; /r/x.sln 동일 ; 가장 가까운 것 우선
  [Fact] Root_FallsBackToFileDirectory()
  [Fact] EnsurePellyDir_CreatesRunsBackupAndGitignoreStar()  // .pelly/.gitignore 내용 == "*\n"
  // WorkspaceLockTests (실제 임시 폴더)
  [Fact] SecondAcquire_ReturnsNull_UntilFirstDisposed()
  ```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Config*" --filter-namespace "*Workspace*"` / Expected: 컴파일 실패

- [ ] **Step 3: 구현** — `ConfigurationBuilder`에 `AddJsonStream`(IFileSystem으로 연 스트림), `AddInMemoryCollection`(env·CLI 변환본) 순서로 넣고 `Bind`. 알 수 없는 키는 `configuration.AsEnumerable()`의 키를 허용 목록(옵션 클래스 리플렉션 + build/test/sql 고정 목록)과 대조.

- [ ] **Step 4: 통과 확인** — 같은 명령 / Expected: 13개 통과

- [ ] **Step 5: Commit** — `git commit -m "feat(core): add options loader, workspace locator, and run lock"`

---

### Task 3: FileGuard (인코딩 보존·원자적 쓰기·백업·외부 변경 감지)

**Files:**
- Create: `src/PellyCoder.Core/Files/FileSnapshot.cs`, `FileGuard.cs`, `SharingViolationRetry.cs`
- Test: `tests/PellyCoder.Core.Tests/Files/TextFileCodecTests.cs`, `FileGuardTests.cs`, `SharingViolationRetryTests.cs`
- Test: `tests/PellyCoder.Integration.Tests/Files/FileGuardWindowsTests.cs`

**Interfaces:**
- Consumes: `Workspace` (Task 2)
- Produces:
  * `sealed record FileSnapshot(string Path, Encoding Encoding, bool HasBom, string NewLine, bool EndsWithNewLine, string Sha256, string Text)` — `Text`는 `\n` 정규화본, `Sha256`은 디스크 바이트의 소문자 hex.
  * `static class TextFileCodec { FileSnapshot Read(IFileSystem fs, string path); byte[] Encode(FileSnapshot like, string textLf); }` — `Encode`는 표현 불가 문자에서 `UnencodableTextException(int Line, char Character)`.
  * `enum FileWriteStatus { Written, ExternallyChanged, Locked, Unencodable }`, `sealed record FileWriteResult(FileWriteStatus Status, FileSnapshot? Snapshot, string? Detail)`
  * `sealed class FileGuard(IFileSystem fs, TimeProvider time)`:
    `FileSnapshot Read(string path)`;
    `string Backup(Workspace ws, string runId, FileSnapshot original)` → 백업 파일 경로(`.pelly/backup/<runId>/<루트 기준 상대 경로>`);
    `Task<FileWriteResult> WriteAsync(FileSnapshot current, string newTextLf, CancellationToken ct)` — 먼저 디스크 SHA-256이 `current.Sha256`과 같은지 확인;
    `Task<FileWriteResult> RestoreAsync(string backupPath, FileSnapshot lastKnown, CancellationToken ct)` — 디스크가 `lastKnown`과 다르면 `ExternallyChanged`로 복원하지 않음.
  * `static class SharingViolationRetry { Task<bool> RunAsync(Action action, int maxAttempts, TimeSpan delay, TimeProvider time, CancellationToken ct); }` — `IOException`의 `HResult & 0xFFFF`가 `32`(공유 위반) 또는 `33`(잠금 위반)일 때만 재시도, 그 외 예외는 그대로 던짐. FileGuard는 `maxAttempts 5`, `delay 200ms`.

- [ ] **Step 1: 실패하는 테스트 작성** (실제 임시 폴더 사용, `new FileSystem()`)
  ```csharp
  [Theory] [InlineData("utf8")] [InlineData("utf8bom")] [InlineData("cp949")] [InlineData("utf16le")]
  RoundTrip_UnchangedText_IsByteIdentical(string kind)   // 한글 주석 포함 CRLF 파일 → Read → Encode(snapshot, snapshot.Text) == 원본 바이트
  [Fact] Read_DetectsEncodingsInDesignOrder()     // BOM UTF-8 → HasBom; 무BOM 유효 UTF-8 → UTF-8; 무효 UTF-8 → CP949(CodePage 949)
  [Theory] [InlineData("a\r\nb\r\nc\n", "\r\n")] [InlineData("a\nb\nc\r\n", "\n")]
  Read_NewLine_IsMajority(string content, string expected)
  [Fact] EndsWithNewLine_IsPreserved()            // "a\nb"(끝 줄바꿈 없음) 왕복 유지
  [Fact] Encode_Cp949_UnencodableCharacter_Throws()   // 수정 텍스트에 "😀" → UnencodableTextException(Line == 2)
  [Fact] Write_ChangedOnlyOneLine_KeepsOtherBytes()   // CP949 CRLF 파일 3번째 줄만 바꿈 → 1,2,4줄 바이트 동일, CRLF 유지
  [Fact] Write_WhenDiskChangedSinceSnapshot_ReturnsExternallyChanged_AndDoesNotWrite()
  [Fact] Backup_ThenRestore_RestoresOriginalBytes()
  [Fact] Restore_WhenDiskChangedAfterOurWrite_DoesNotOverwrite()   // → ExternallyChanged
  [Fact] Write_LeavesNoTempFile()                  // 폴더에 ".*.pelly-tmp" 없음
  // SharingViolationRetryTests (FakeTimeProvider: Microsoft.Extensions.TimeProvider.Testing)
  [Fact] RetriesSharingViolation_ThenSucceeds()    // HResult 0x80070020 두 번 → 세 번째 성공 → true, 호출 3회
  [Fact] GivesUpAfterMaxAttempts()                 // 5회 모두 실패 → false
  [Fact] OtherIOException_IsRethrown()
  // FileGuardWindowsTests [WindowsFact]
  [WindowsFact] Write_WhileFileOpenedWithShareNone_ReturnsLocked()  // FileShare.None으로 연 상태에서 WriteAsync → Locked, 원본 그대로
  ```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Files*"` / Expected: 컴파일 실패

- [ ] **Step 3: 구현** — 설계 §8.8 그대로. 쓰기: 같은 폴더 `.<이름>.pelly-tmp`에 쓰고 `OperatingSystem.IsWindows()`면 `fs.File.Replace(tmp, path, null)`, 아니면 `fs.File.Move(tmp, path, overwrite: true)`. 실패 시 임시 파일 삭제. `CodePagesEncodingProvider`는 `TextFileCodec`의 정적 생성자에서 등록.

- [ ] **Step 4: 통과 확인** — 같은 명령 / Expected: 전부 통과. `dotnet test tests/PellyCoder.Integration.Tests -- --filter-class "*FileGuardWindows*"` → Mac에서 Skip

- [ ] **Step 5: Commit** — `git commit -m "feat(core): add FileGuard with encoding-preserving atomic writes and backups"`

---

### Task 4: SEARCH/REPLACE 파서·패처

**Files:**
- Create: `src/PellyCoder.Core/Patchers/SearchReplaceParser.cs`, `SearchReplacePatcher.cs`, `MarkerScanner.cs`
- Test: `tests/PellyCoder.Core.Tests/Patchers/SearchReplaceParserTests.cs`, `SearchReplacePatcherTests.cs`

**Interfaces:**
- Consumes: `PatchBlock`, `PatchOutcome`, `PatchFailure`, `PatchFailureKinds`, `MatchStage`, `EditableRegion`, `TextSpanInfo` (Task 1)
- Produces:
  * `sealed record ParseResult(IReadOnlyList<PatchBlock> Blocks, PatchFailure? Failure)`; `static class SearchReplaceParser { ParseResult Parse(string modelOutput); }`
  * `static class SearchReplacePatcher { PatchOutcome Apply(string originalLf, IReadOnlyList<PatchBlock> blocks, IReadOnlyList<EditableRegion> regions); }` — `NewText`는 `\n` 기준.
  * `static class MarkerScanner { bool HasConflictMarkers(string textLf); }` — 줄이 `<<<<<<<`, `=======`, `>>>>>>>`로 시작하면 true.

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  // Parser
  [Fact] Parse_SingleBlock()
  [Fact] Parse_MultipleBlocks_InOrder()
  [Fact] Parse_IgnoresProseAndFences()       // 앞에 "Here is the fix:", 블록을 ```csharp … ``` 로 감싸고 뒤에 설명 → 블록 1개, Search/Replace에 ``` 줄 없음 (Review Focus 2)
  [Fact] Parse_MarkerWithTrailingSpaces_IsAccepted()
  [Fact] Parse_MissingReplaceMarker_IsParseFailure()   // Failure.Kind == Parse, Detail에 "truncated"
  [Fact] Parse_EmptySearch_IsParseFailure()
  [Fact] Parse_NoBlocks_IsParseFailure()
  [Fact] Parse_EmptyReplace_IsAllowed()       // 줄 삭제
  // Patcher (regions: target 10..30줄, usings 1..3줄)
  [Fact] Exact_Applies_StageExact()
  [Fact] TrailingWhitespaceDiff_Applies_StageTrailingWhitespace()
  [Fact] IndentationOff_Applies_AndReindentsReplace()   // SEARCH 2칸 들여쓰기, 원본 12칸 → REPLACE 각 줄에 10칸 추가
  [Fact] IndentationWithTabs_UsesTabs()
  [Fact] TwoMatchesInRegion_IsAmbiguous()
  [Fact] MatchOnlyOutsideRegion_IsOutsideRegion()   // [READ-ONLY] 시그니처 "{ ... }" 처럼 영역 밖에만 있는 텍스트
  [Fact] NoMatch_IsNotFound_WithNearestOriginalPlusMinus3()   // NearestOriginal이 7줄(가장 가까운 줄 ±3)
  [Fact] MultipleBlocks_AppliedSequentially_RegionShifts()    // 첫 블록이 usings에 2줄 추가 → 두 번째 블록이 이동한 target 영역에서 매칭
  [Fact] OverlappingBlocks_IsOverlap()        // 두 번째 블록의 SEARCH가 첫 블록이 바꾼 줄을 가리킴 → Overlap
  [Fact] EmptyLinesMatchOnlyEmptyLines_InIndentationStage()
  [Fact] MarkerScanner_DetectsConflictMarkers()
  ```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Patchers*"` / Expected: 컴파일 실패

- [ ] **Step 3: 구현** — 설계 §8.7 그대로. `Overlap` 판정: 블록을 적용할 때마다 "바뀐 줄 범위"를 기록하고, 다음 블록의 일치 범위가 그 범위와 겹치면 실패. NearestOriginal 거리: `Trim()` 문자열의 Levenshtein 거리, 같으면 앞쪽 줄.

- [ ] **Step 4: 통과 확인** — 같은 명령 / Expected: 19개 통과

- [ ] **Step 5: Commit** — `git commit -m "feat(core): add SEARCH/REPLACE parser and region-limited patcher"`

---

### Task 5: Qwen 토큰 카운터

**Files:**
- Create: `src/PellyCoder.Core/Abstractions/ITokenCounter.cs`, `src/PellyCoder.Core/Llm/QwenTokenCounter.cs`
- Create: `src/PellyCoder.Core/Llm/Resources/vocab.json`, `merges.txt` (S4에서 받은 파일 복사: `spikes/S4-tokenizer/`의 파일, SHA-256은 `docs/spikes/S4-tokenizer.md`와 일치해야 함), `tokenizer-meta.json`
- Create: `THIRD-PARTY-NOTICES.md` (Qwen 토크나이저 Apache-2.0 고지)
- Modify: `src/PellyCoder.Core/PellyCoder.Core.csproj` (`<EmbeddedResource Include="Llm/Resources/**" />`), `Directory.Packages.props` (`Microsoft.ML.Tokenizers` 2.0.1)
- Test: `tests/PellyCoder.Core.Tests/Llm/QwenTokenCounterTests.cs`, `tests/fixtures/tokenizer/samples.json`

**Interfaces:**
- Produces: `interface ITokenCounter { int Count(string text); }`; `sealed class QwenTokenCounter : ITokenCounter { static QwenTokenCounter Shared { get; } }` (지연 초기화, 스레드 안전).
* `tokenizer-meta.json` 형식: `{ "source": "Qwen/Qwen3.6-35B-A3B tokenizer.json", "sha256": "5f9e4d…cb42", "preTokenizerRegex": "<tokenizer.json의 Split 정규식>", "specialTokens": { "<|endoftext|>": 248044, … 26개 } }` — 값은 S4에서 받은 `tokenizer.json`에서 뽑는다(스크립트는 커밋하지 않음, 생성 명령을 커밋 메시지에 남김).

- [ ] **Step 1: 테스트 픽스처와 실패하는 테스트 작성** — `spikes/S4-tokenizer/samples.json`과 `results.csv`에서 범주별 2개씩(cs·sql·ko·template, 총 8개)을 `{ "text": …, "serverCount": … }`로 `tests/fixtures/tokenizer/samples.json`에 옮긴다(서버 값이 오라클).
  ```csharp
  [Fact] Count_MatchesServerPromptEvalCount_ForRecordedSamples()   // 8개 모두 Count(text) == serverCount
  [Fact] SpecialTokens_CountAsOne()          // Count("<|im_start|>") == 1, Count("</think>") == 1
  [Fact] Count_Empty_IsZero()
  [Fact] Shared_IsSameInstance()
  ```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*QwenTokenCounterTests"` / Expected: 컴파일 실패

- [ ] **Step 3: 구현** — 설계 §8.2 TokenCounter 코드 블록대로(`BpeOptions` + `ByteLevel` + `RegexPreTokenizer` + NFC 정규화). 임베디드 리소스에서 읽는다. `BpeOptions`의 열거형 어휘 생성자를 쓰고, 맞는 오버로드가 없으면 실행 파일 옆이 아닌 `Path.GetTempPath()` 아래 고유 폴더로 풀어 파일 경로 생성자를 쓴다.

- [ ] **Step 4: 통과 확인** — 같은 명령 / Expected: 4개 통과

- [ ] **Step 5: Commit** — `git commit -m "feat(llm): add embedded Qwen tokenizer counter"`

---

### Task 6: 대상 멤버 찾기 (M1 범위)

**Files:**
- Create: `src/PellyCoder.Core/CSharp/ProjectFileLocator.cs`, `LanguageVersionDetector.cs`, `MemberLocator.cs`
- Modify: `Directory.Packages.props` (`Microsoft.CodeAnalysis.CSharp` 최신 안정판)
- Test: `tests/PellyCoder.Core.Tests/CSharp/LanguageVersionDetectorTests.cs`, `MemberLocatorTests.cs`, `ProjectFileLocatorTests.cs`

**Interfaces:**
- Consumes: `TextSpanInfo` (Task 1)
- Produces:
  * `static class ProjectFileLocator { string? FindNearestCsproj(IFileSystem fs, string filePath); }`
  * `sealed record LanguageTarget(LanguageVersion Version, string Hint)`; `static class LanguageVersionDetector { LanguageTarget Detect(IFileSystem fs, string? csprojPath); }` — Hint 예: `"C# 12, net8.0"`, `"C# 7.3, .NET Framework 4.8"`(SDK `net48`), `"C# 7.3, .NET Framework 4.5"`(레거시 `v4.5`). csproj 없음 → `LanguageVersion.Default`와 `"C# (latest)"`.
  * `sealed record MemberCandidate(string Display, string ContainingType, TextSpanInfo Span)` — `Display` 예: `"OrderService.CancelAsync(int, string)"`. `Span`은 앞쪽 XML 문서 주석·특성 포함 시작 줄부터 멤버 끝 줄까지.
  * `static class MemberLocator { IReadOnlyList<MemberCandidate> FindByName(string sourceLf, LanguageVersion version, string? className, string memberName); }` — 메서드·생성자(이름 = 클래스 이름)·프로퍼티 대상, 대소문자 구분. 중첩 타입 포함.

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  // LanguageVersionDetector: 설계 §8.5 표 전체를 Theory로
  [Theory]
  [InlineData("<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net8.0</TargetFramework></PropertyGroup></Project>", LanguageVersion.CSharp12, "C# 12, net8.0")]
  [InlineData("…<TargetFramework>net48</TargetFramework>…", LanguageVersion.CSharp7_3, "C# 7.3, .NET Framework 4.8")]
  [InlineData("…<TargetFrameworks>net8.0;netstandard2.0</TargetFrameworks>…", LanguageVersion.CSharp7_3, "C# 7.3, netstandard2.0")]  // 가장 낮은 것
  [InlineData("…<LangVersion>10</LangVersion><TargetFramework>net48</TargetFramework>…", LanguageVersion.CSharp10, "C# 10, .NET Framework 4.8")]
  // + netcoreapp2.1→7.3, netcoreapp3.1→8, netstandard2.1→8, net5.0~net10.0→9~14
  Detect_SdkStyle(string csproj, LanguageVersion expected, string hint)
  [Fact] Detect_LegacyCsproj_V45_Is73()      // 실제 코퍼스 eval/corpus/Legacy/Shop.Data/Shop.Data.csproj
  [Fact] Detect_LangVersionLatest_UsesTfmDefault()
  // MemberLocator — 실제 코퍼스 eval/corpus/Modern/Shop.Api/Services/OrderService.cs
  [Fact] FindByName_CancelAsync_ReturnsOneWithSpanIncludingDocComment()   // Span.StartLine = "/// <summary>" 줄
  [Fact] FindByName_Overloads_ReturnsAll()   // 인라인 소스: void M(int) / void M(string) → 2개
  [Fact] FindByName_ClassFilter_Disambiguates()
  [Fact] FindByName_Constructor_And_Property()
  [Fact] FindByName_CaseSensitive_NoMatch()  // "cancelasync" → 0개
  // ProjectFileLocator
  [Fact] FindNearestCsproj_WalksUp()         // eval/corpus/Modern/Shop.Api/Services/OrderService.cs → …/Shop.Api/Shop.Api.csproj
  ```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*CSharp*"` / Expected: 컴파일 실패

- [ ] **Step 3: 구현** — 설계 §8.5 감지 규칙. 레거시 판별은 루트 요소에 `Sdk` 속성·`<Sdk>` 자식이 없음, 버전은 `TargetFrameworkVersion`(`v4.5` → ".NET Framework 4.5"). 멤버 Span 시작: 선언의 첫 토큰 leading trivia 중 첫 문서 주석 줄(없으면 선언 첫 줄, 특성은 선언 Span에 포함).

- [ ] **Step 4: 통과 확인** — 같은 명령 / Expected: 전부 통과

- [ ] **Step 5: Commit** — `git commit -m "feat(csharp): add project, language version, and member locators"`

---

### Task 7: Roslyn 슬라이서 (기본)

**Files:**
- Create: `src/PellyCoder.Core/CSharp/RoslynSlicer.cs`
- Test: `tests/PellyCoder.Core.Tests/CSharp/RoslynSlicerTests.cs`

**Interfaces:**
- Consumes: `MemberCandidate`, `LanguageTarget` (Task 6), `ITokenCounter` (Task 5), `Slice`, `SlicePart`, `EditableRegion`, `TextSpanInfo` (Task 1)
- Produces:
  * `sealed record SliceResult(Slice Slice, IReadOnlyList<EditableRegion> Regions, bool TargetTooLarge)`
  * `static class RoslynSlicer { SliceResult Build(string sourceLf, MemberCandidate target, LanguageTarget language, ITokenCounter tokens, int contextBudget); }`
  * `SlicePart.Kind` 값: `"target"`, `"usings"`, `"fields"`, `"signatures"` (M1에는 `dto` 없음 — M3). 우선순위: signatures 1, usings 3, fields 4, target 제거 불가(int.MaxValue).
  * Regions: `EditableRegion(span, "target")`, `EditableRegion(span, "usings")` — usings 파트가 포함됐을 때만. using이 없는 파일은 첫 `namespace`(또는 파일 범위 namespace) 선언 줄을 usings 영역으로.

- [ ] **Step 1: 실패하는 테스트 작성** (코퍼스 `Modern/Shop.Api/Services/OrderService.cs`, `ITokenCounter`는 실제 `QwenTokenCounter.Shared`)
  ```csharp
  [Fact] Target_IsVerbatimSourceOfMember()           // target 파트 Text == 원본의 Span 줄들 그대로(문서 주석 포함)
  [Fact] Signatures_ReplaceBodiesWithEllipsis()       // CalculateRefundAsync가 "public async Task<decimal> CalculateRefundAsync(int orderId) { ... }" 한 줄로, 본문 문자열("환불액") 없음
  [Fact] Fields_IncludeFieldsAndConstructorSignature()   // "_repository", "OrderService(IOrderRepository repository, ILogger<OrderService> logger) { ... }"
  [Fact] Usings_PartAndRegion_MatchFileUsingLines()
  [Fact] NoLineNumbersInAnyPart()
  [Fact] OverBudget_DropsSignaturesThenUsingsThenFields()   // 예산을 줄여 가며 Omitted 순서 = signatures, usings, fields
  [Fact] UsingsDropped_RemovesUsingsRegion()
  [Fact] TargetAloneOverBudget_SetsTargetTooLarge()       // eval/corpus/Legacy/Shop.Batch/SettlementBatch.cs BuildReport(207줄)에 예산 500
  [Fact] FileWithoutUsings_UsesNamespaceLineAsUsingsRegion()
  [Fact] Tokens_InEachPart_EqualCounterResult()
  ```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*RoslynSlicerTests"` / Expected: 컴파일 실패

- [ ] **Step 3: 구현** — 설계 §8.5. 시그니처: 메서드·생성자·접근자 본문(블록·식 본문 모두)을 `{ ... }`로 바꾼 선언을 한 줄로 정규화(`NormalizeWhitespace` 후 줄바꿈 제거). 자동 프로퍼티는 그대로, 본문 있는 접근자는 `get { ... }`. 대상 멤버 자신은 signatures에서 뺀다.

- [ ] **Step 4: 통과 확인** — 같은 명령 / Expected: 10개 통과

- [ ] **Step 5: Commit** — `git commit -m "feat(csharp): add budget-aware Roslyn slicer with editable regions"`

---

### Task 8: chat template·프롬프트 조립·예산

**Files:**
- Create: `src/PellyCoder.Core/Llm/QwenChatTemplate.cs`, `PromptResources.cs`, `PromptBuilder.cs`
- Create: `src/PellyCoder.Core/Llm/Resources/prompts/system.cs.txt`, `fewshot.cs.txt`
- Test: `tests/PellyCoder.Core.Tests/Llm/QwenChatTemplateTests.cs`, `PromptBuilderTests.cs`

**Interfaces:**
- Consumes: `ITokenCounter` (Task 5), `BudgetOptions` (Task 2), `EditTarget`, `Slice`, `Feedback` (Task 1)
- Produces:
  * `sealed record QwenChatTemplate(string SystemStart, string SystemEnd, string UserStart, string UserEnd, string AssistantStart, string ThinkOpen, string ThinkClose, string EndOfTurn)`; `static QwenChatTemplate LoadDefault()`, `static QwenChatTemplate LoadFromFile(IFileSystem fs, string path)`; `string Build(string system, string user)`; `string EmptyThink => ThinkOpen + ThinkClose`.
  * `sealed record PromptResources(string SystemCs, string FewShotCs, string Version)`; `static PromptResources LoadDefault()` — 각 파일 첫 줄 `# version: N`을 떼고 `Version = "cs-v" + N`.
  * `enum PromptBudgetError { None, RequestTooLong, FeedbackTooLong, TotalTooLong }`; `sealed record PromptBuildResult(string BasePrompt, int Tokens, PromptBudgetError Error, string? Detail)`
  * `sealed class PromptBuilder(QwenChatTemplate template, PromptResources resources, ITokenCounter tokens, BudgetOptions budget)`: `PromptBuildResult BuildCSharp(EditTarget target, Slice slice, string request, Feedback? feedback)`
* `system.cs.txt` (정확한 문구):
  ```text
  # version: 1
  You are a code editor that changes exactly one C# member.
  Rules:
  1. Output only SEARCH/REPLACE blocks. No explanations, no markdown fences.
  2. Block format:
  <<<<<<< SEARCH
  (lines copied exactly from an [EDITABLE] section, including indentation)
  =======
  (replacement lines)
  >>>>>>> REPLACE
  3. SEARCH must copy contiguous lines verbatim from [EDITABLE: target] or [EDITABLE: usings]. Never copy from [READ-ONLY] sections.
  4. Keep each SEARCH short but unique: include just enough lines to match exactly one place.
  5. Do not change the member's signature, attributes, or access modifiers. You may add a private helper method right after the target member.
  6. Target language: {LanguageHint}. Do not use newer syntax or APIs.
  7. If a FEEDBACK section is present, fix the problems it describes.
  ```
* `fewshot.cs.txt`: `# version: 1` + "Example" 한 쌍 — 작은 메서드(`public int Add(int a, int b) { return a + b; }`)와 요청 "음수가 들어오면 ArgumentOutOfRangeException" → 블록 1개. 200~260토큰(테스트로 확인).
* 시스템 메시지 = `system.cs.txt` 본문(`{LanguageHint}` 치환) + `\n\n` + few-shot. 사용자 메시지 = 설계 §8.2 구성(### TARGET / ### CONTEXT / ### REQUEST / ### FEEDBACK). CONTEXT 안 순서: `[READ-ONLY: fields]`, `[READ-ONLY: signatures]`, `[EDITABLE: usings]`, `[EDITABLE: target]` (포함된 파트만).

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] Build_MatchesS1ChatMl()   // Build("S","U") == "<|im_start|>system\nS<|im_end|>\n<|im_start|>user\nU<|im_end|>\n<|im_start|>assistant\n"
  [Fact] EmptyThink_IsS3Prefill()  // == "<think>\n\n</think>\n\n"
  [Fact] LoadFromFile_OverridesDefault()
  [Fact] SystemPrompt_IsStableAcrossTargets()   // 다른 대상·요청이라도 LanguageHint가 같으면 BasePrompt의 시스템 부분이 동일(캐시 재사용, S2)
  [Fact] FewShot_TokenCount_Between200And260()
  [Fact] User_SectionsInDesignOrder()            // "### TARGET" < "### CONTEXT" < "### REQUEST"; 피드백 있으면 "### FEEDBACK"이 마지막
  [Fact] NoFeedback_NoFeedbackSection()
  [Fact] RequestOver200Tokens_IsRequestTooLong()
  [Fact] FeedbackOver296Tokens_IsFeedbackTooLong()
  [Fact] TotalOver4096_IsTotalTooLong()          // 조립된 raw 프롬프트 전체를 세서 판단(템플릿 토큰 포함)
  [Fact] OmittedParts_AreNotInPrompt()
  ```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Llm*"` / Expected: 컴파일 실패(신규 클래스)

- [ ] **Step 3: 구현**

- [ ] **Step 4: 통과 확인** — 같은 명령 / Expected: 전부 통과

- [ ] **Step 5: Commit** — `git commit -m "feat(llm): add ChatML template, C# prompt resources, and budgeted prompt builder"`

---

### Task 9: Ollama 백엔드

**Files:**
- Modify: `src/PellyCoder.Core/Abstractions/ILlmBackend.cs` (Task 2에서 만든 파일)
- Create: `src/PellyCoder.Core/Llm/OllamaBackend.cs`
- Create: `tests/fixtures/ollama/generate-stream.ndjson` (실제 서버 녹화), `show.json`
- Test: `tests/PellyCoder.Core.Tests/Llm/OllamaBackendTests.cs`, `tests/PellyCoder.Core.Tests/Fakes/StubHttpHandler.cs`

**Interfaces:**
- Consumes: `LlmOptions` (Task 2), `SamplingOptions` (Task 2)
- Produces (설계 §5, S1에 따른 차이는 아래 표시):
  * `sealed record LlmRequest(string RawPrompt, SamplingOptions Sampling, int NumPredict, IReadOnlyList<string> Stop, int NumCtx, string KeepAlive)`
  * `sealed record LlmChunk(string Text, bool Done, int? PromptEvalCount, int? PromptEvalCachedCount, int? EvalCount, TimeSpan? PromptEvalDuration, string? DoneReason)`
  * `sealed record LlmServerStatus(bool Reachable, string? Version, bool ModelInstalled, bool ModelLoaded, string? Renderer, TimeSpan Latency, string? Error)` — **설계의 `ModelTemplate`을 `Renderer`로 바꿈**(S1: 템플릿은 `{{ .Prompt }}`뿐, `/api/show`의 `RENDERER` 줄이 의미 있음) + 실패 사유 `Error`.
  * `interface ILlmBackend { IAsyncEnumerable<LlmChunk> GenerateAsync(LlmRequest request, CancellationToken ct); Task<LlmServerStatus> GetStatusAsync(CancellationToken ct); }`
  * `interface IOllamaDiagnostics { Task<int?> ChatPromptTokenCountAsync(string system, string user, CancellationToken ct); }` — `/api/chat`, `think: true`, `num_predict: 1`의 `prompt_eval_count`.
  * `sealed class LlmUnavailableException(string message, Exception? inner = null) : Exception`
  * `sealed class OllamaBackend(HttpClient http, LlmOptions options, TimeProvider time) : ILlmBackend, IOllamaDiagnostics` — `http.BaseAddress = options.Endpoint`, `options.Headers`를 매 요청에 붙임.

- [ ] **Step 1: 녹화 픽스처 만들기** — 로컬 서버에서:
  ```bash
  curl -s localhost:11434/api/generate -d '{"model":"qwen3.6:35b-a3b-coding-nvfp4","prompt":"<|im_start|>user\nhi<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n","raw":true,"stream":true,"options":{"num_predict":6}}' > tests/fixtures/ollama/generate-stream.ndjson
  curl -s localhost:11434/api/show -d '{"model":"qwen3.6:35b-a3b-coding-nvfp4"}' > tests/fixtures/ollama/show.json
  ```
  `show.json`이 크면 `modelfile`, `parameters`, `details`, `capabilities`만 남겨 저장.

- [ ] **Step 2: 실패하는 테스트 작성** (`StubHttpHandler`: 경로별 응답·지연·예외를 스크립트로 지정)
  ```csharp
  [Fact] Generate_SendsRawRequestBody()     // 본문 JSON: raw==true, stream==true, keep_alive, options.num_ctx/num_predict/temperature/top_p/top_k/repeat_penalty/presence_penalty/stop
  [Fact] Generate_ParsesRecordedStream()    // 녹화 파일 재생 → 마지막 청크 Done==true, PromptEvalCount·PromptEvalCachedCount·EvalCount·DoneReason 채워짐, Text 이어붙인 값 == 녹화 response 합
  [Fact] Generate_IgnoresUnknownFieldsAndBlankLines()
  [Fact] Generate_ConnectFailure_ThrowsLlmUnavailable()
  [Fact] Generate_NoFirstByteWithinConnectTimeout_RetriesOnceThenThrows()   // FakeTimeProvider, 요청 2회 확인
  [Fact] Generate_IdleMidStream_ThrowsLlmUnavailable()   // 첫 청크 뒤 streamIdleTimeoutSeconds 무응답
  [Fact] Generate_Cancellation_StopsEnumeration()        // OperationCanceledException (LlmUnavailable 아님)
  [Fact] Generate_Http404_ThrowsLlmUnavailable_WithModelHint()   // 메시지에 모델 이름
  [Fact] Status_ReadsVersionTagsPsShow()   // /api/version, /api/tags(모델 존재), /api/ps(로드), /api/show → Renderer == "qwen3.5"
  [Fact] Status_Unreachable_ReturnsReachableFalseWithError()   // 예외를 던지지 않음
  [Fact] Headers_AreSentOnEveryRequest()
  [Fact] ChatPromptTokenCount_PostsChatWithThinkTrue()
  ```

- [ ] **Step 3: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*OllamaBackendTests"` / Expected: 컴파일 실패

- [ ] **Step 4: 구현** — 설계 §8.2 OllamaBackend. `HttpCompletionOption.ResponseHeadersRead`, 줄 단위 `System.Text.Json` 파싱(`JsonDocument`, 없는 필드는 null). 첫 바이트 전 타임아웃만 1회 재요청(설계 §8.2), 스트림 중간 무응답은 재요청하지 않음.

- [ ] **Step 5: 통과 확인** — 같은 명령 / Expected: 12개 통과

- [ ] **Step 6: Commit** — `git commit -m "feat(llm): add Ollama raw-mode backend with status and diagnostics"`

---

### Task 10: 2단계 생성 클라이언트 (thinkMode·D21~D23)

**Files:**
- Create: `src/PellyCoder.Core/Llm/QwenStreamClient.cs`
- Test: `tests/PellyCoder.Core.Tests/Llm/QwenStreamClientTests.cs`, `tests/PellyCoder.Core.Tests/Fakes/ScriptedLlmBackend.cs`

**Interfaces:**
- Consumes: `ILlmBackend`, `LlmRequest`, `LlmChunk` (Task 9), `QwenChatTemplate` (Task 8), `LlmOptions`, `BudgetOptions`, `SamplingSet` (Task 2)
- Produces:
  * `sealed record LlmUsage(int? PromptEvalCount, int? PromptEvalCachedCount, int? EvalCount, string? DoneReason, TimeSpan Ttft, TimeSpan Total)`
  * `sealed record GenerationResult(string Output, bool ThinkUsed, string? ThinkText, int ThinkTokens, bool ThinkTruncated, LlmUsage? ThinkUsage, LlmUsage CodeUsage)`
  * `sealed class QwenStreamClient(ILlmBackend backend, QwenChatTemplate template, LlmOptions llm, BudgetOptions budget, SamplingSet sampling, TimeProvider time)`: `Task<GenerationResult> GenerateEditAsync(string basePrompt, int attempt, CancellationToken ct)`
  * `ScriptedLlmBackend` (테스트용, 이후 Task에서 재사용): 요청을 기록(`Requests`)하고, 등록 순서대로 응답 텍스트·`DoneReason`·`EvalCount`를 청크 여러 개로 흘려 준다.

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] Retry_Attempt1_SingleCodeRequestWithEmptyThink()
      // 요청 1개, RawPrompt == base + "<think>\n\n</think>\n\n<<<<<<< SEARCH\n", Sampling == Code, NumPredict == 2048, Stop == ["<|im_end|>"], ThinkUsed == false
  [Fact] Retry_Attempt2_ThinkThenCode()
      // 요청 2개: ① base + "<think>\n", Sampling == Think, NumPredict == 512, Stop == ["</think>"] ② base + "<think>\n" + think + "\n</think>\n\n<<<<<<< SEARCH\n"
  [Fact] Always_Attempt1_UsesThink() / [Fact] Off_Attempt3_NoThink()
  [Fact] Think_StopIgnored_CutsAtFirstThinkClose()          // 응답 "plan A\n</think>\n\nanswer…" → ThinkText == "plan A", ThinkTruncated == false (D23)
  [Fact] Think_LengthTruncated_DropsPartialLastLine()       // DoneReason "length", 텍스트 "a\nb\npartial" → "a\nb", ThinkTruncated == true
  [Fact] Think_Tokens_FromServerEvalCount()                 // ThinkTokens == 512 (서버 값)
  [Fact] Output_IsPrefixedWithSearchMarker()                // Output == "<<<<<<< SEARCH\n" + 코드 응답
  [Fact] Usage_RecordsCachedCountsAndTtft()
  [Fact] NeverCancelsBackendStream()                        // 스크립트 백엔드가 받은 ct가 끝까지 취소되지 않음 (D22)
  ```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*QwenStreamClientTests"` / Expected: 컴파일 실패

- [ ] **Step 3: 구현** — 설계 §8.2 QwenStreamClient 의사 코드 그대로. `useThink = thinkMode == Always || (thinkMode == Retry && attempt >= 2)`.

- [ ] **Step 4: 통과 확인** — 같은 명령 / Expected: 10개 통과

- [ ] **Step 5: Commit** — `git commit -m "feat(llm): add two-stage generation with think mode and server-side limits"`

---

### Task 11: 구문 검사와 피드백

**Files:**
- Create: `src/PellyCoder.Core/CSharp/SyntaxVerifier.cs`, `src/PellyCoder.Core/Feedback/FeedbackBuilder.cs`
- Test: `tests/PellyCoder.Core.Tests/CSharp/SyntaxVerifierTests.cs`, `tests/PellyCoder.Core.Tests/Feedback/FeedbackBuilderTests.cs`

**Interfaces:**
- Consumes: `Diagnostic`, `VerifyOutcome`, `Feedback`, `PatchFailure`, `PatchFailureKinds` (Task 1), `ITokenCounter` (Task 5)
- Produces:
  * `static class SyntaxVerifier { VerifyOutcome Verify(string textLf, LanguageVersion version, string displayPath); }` — `SyntaxTree.GetDiagnostics()`의 Error만 `Diagnostic(displayPath, line1, col1, "error", "CSxxxx", message)`로. 진단 메시지 언어는 `CultureInfo.InvariantCulture`로 고정(영어).
  * `sealed class FeedbackBuilder(ITokenCounter tokens, int budget)`:
    `Feedback ForDiagnostics(string header, IReadOnlyList<Diagnostic> errors, string fileTextLf)`;
    `Feedback ForPatchFailure(PatchFailure failure)`
  * 문구(정확히):
    ```text
    ForDiagnostics(header = "SYNTAX CHECK FAILED"):
    SYNTAX CHECK FAILED (2 errors)
    OrderService.cs(41): CS1002 ; expected
      39 |     var order = ...
      40 |     if (...)
    > 41 |         throw new X()
      42 |     ...
    (… 다음 오류 …)

    ForPatchFailure:
    NotFound      → "SEARCH NOT FOUND: the SEARCH block does not match the [EDITABLE] text exactly. Nearest original lines:\n<NearestOriginal>"
    Ambiguous     → "SEARCH AMBIGUOUS: the SEARCH block matches more than one place. Include more surrounding lines."
    OutsideRegion → "SEARCH OUTSIDE EDITABLE REGION: copy SEARCH lines only from [EDITABLE: target] or [EDITABLE: usings]."
    Overlap       → "BLOCKS OVERLAP: each SEARCH must refer to original lines not changed by an earlier block."
    Parse         → "FORMAT ERROR: <Detail>. Output only SEARCH/REPLACE blocks."
    Unencodable   → "ENCODING ERROR: <Detail>. The file encoding cannot store this character; use only characters it supports."
    ```
  * 예산: `budget`(296) 초과 시 뒤쪽 오류부터 뺀다. 오류 하나로도 넘으면 문맥 줄을 줄이고, 그래도 넘으면 메시지를 잘라 `…`를 붙인다. 결과 `Tokens` ≤ budget.

- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] Verify_ValidCode_Passes()
  [Fact] Verify_MissingSemicolon_ReportsCS1002WithLine()
  [Fact] Verify_UsesRequestedLanguageVersion()   // C# 7.3에서 "record R(int X);" → 오류, C# 12에서는 통과
  [Fact] Verify_MessagesAreEnglish()            // CurrentUICulture를 ko-KR로 바꿔도 메시지가 영어
  [Fact] Diagnostics_FormatWithPlusMinus3AndMarker()
  [Fact] Diagnostics_OverBudget_DropsTrailingErrors()   // 오류 10개 → Tokens ≤ 296, 첫 오류는 포함
  [Fact] Diagnostics_SingleHugeError_IsTruncated()
  [Theory] [InlineData("NotFound")] [InlineData("Ambiguous")] [InlineData("OutsideRegion")] [InlineData("Overlap")] [InlineData("Parse")] [InlineData("Unencodable")]
  PatchFailure_UsesExactPhrase(string kind)
  ```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*SyntaxVerifierTests" --filter-class "*FeedbackBuilderTests"` / Expected: 컴파일 실패

- [ ] **Step 3: 구현**

- [ ] **Step 4: 통과 확인** — 같은 명령 / Expected: 전부 통과

- [ ] **Step 5: Commit** — `git commit -m "feat(core): add syntax verifier and budgeted feedback builder"`

---

### Task 12: 단계·오케스트레이터·실행 로그

**Files:**
- Create: `src/PellyCoder.Core/Abstractions/IUserInteraction.cs`
- Create: `src/PellyCoder.Core/Fsm/RunRequest.cs`, `IPhase.cs`, `FsmOrchestrator.cs`, `Phases/PreflightPhase.cs`, `LocatePhase.cs`, `SlicePhase.cs`, `EditPhase.cs`, `ApplyPhase.cs`, `VerifyPhase.cs`, `ReviewPhase.cs`
- Create: `src/PellyCoder.Core/Logging/RunLogger.cs`
- Test: `tests/PellyCoder.Core.Tests/Fsm/FsmOrchestratorTests.cs`, `tests/PellyCoder.Core.Tests/Logging/RunLoggerTests.cs`, `tests/PellyCoder.Core.Tests/Fakes/ScriptedUserInteraction.cs`

**Interfaces:**
- Consumes: Task 1~11 전부 (위 Produces 그대로)
- Produces:
  * `interface IUserInteraction` — 설계 §5와 같고 `ShowStatus`만 구조화: `void ShowStatus(RunPhase phase, int attempt, string message)`. 그 외 `bool IsInteractive`, `ConfirmAsync`, `SelectAsync<T>`, `ShowDiff(string path, string before, string after)`, `Warn(string)`.
  * `sealed record RunRequest(string FilePath, string? ClassName, string MethodName, string Request, bool Yes, bool DryRun, int? MaxRetries)`
  * `sealed class RunContext` — `RunId`, `Request`, `Workspace`, `Phase`, `Attempt`, `Original`(FileSnapshot), `Current`(FileSnapshot), `BackupPath`(string?), `Target`(EditTarget?), `Candidate`(MemberCandidate?), `Language`(LanguageTarget?), `SliceResult`, `ModelOutput`, `Blocks`, `Feedback`(Feedback?), `ExitCode`, `Generation`(GenerationResult?), `MatchStages`(List), `ErrorCodes`(List<string>), `FailureKind`(string?), `Messages`(List<string>) — 단계 간 데이터는 여기로만.
  * `interface IPhase { RunPhase Phase { get; } Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct); }`
  * `sealed class FsmOrchestrator(IEnumerable<IPhase> phases, FileGuard fileGuard, RunLogger logger, IFileSystem fs, PellyOptions options, IUserInteraction ui, TimeProvider time)`: `Task<int> RunAsync(RunRequest request, CancellationToken ct)`
  * 단계 동작 (설계 §7·§8, M1 범위):
    * **Preflight:** 워크스페이스 해석·`.pelly` 생성·`WorkspaceLock.TryAcquire`(null → EnvError, 메시지 "다른 pelly 실행이 진행 중입니다"), 파일 읽기(없으면 EnvError), `MarkerScanner` → EnvError, `ILlmBackend.GetStatusAsync` → `Reachable == false` 또는 `ModelInstalled == false` → EnvError(메시지에 엔드포인트·모델 이름·`pelly doctor` 안내).
    * **Locate:** `MemberLocator.FindByName` 0개 → NotFound, 2개 이상 → `IsInteractive`면 `SelectAsync`, 아니면 Ambiguous. `ProjectFileLocator`·`LanguageVersionDetector`로 `Target`·`Language` 설정.
    * **Slice:** `fileGuard.Read`로 현재 파일을 다시 읽어 `Current` 갱신 → 대상 멤버를 이름·Span으로 다시 찾음(교정 시도에서 줄이 움직였을 수 있음: `FindByName` 후 `Display`가 같은 후보) → `RoslynSlicer.Build`(예산 `budget.context`) → `TargetTooLarge`면 BudgetExceeded.
    * **Edit:** `Attempt++` → `PromptBuilder.BuildCSharp` 오류 → BudgetExceeded → `QwenStreamClient.GenerateEditAsync` (`LlmUnavailableException` → EnvError) → `SearchReplaceParser.Parse` 실패 → `Feedback = ForPatchFailure` + FormatError.
    * **Apply:** `SearchReplacePatcher.Apply` 실패 → 피드백 + MatchFailed. `DryRun`이면 `ShowDiff(Original, 새 텍스트)` 후 DryRunCompleted(파일 변경 없음). 첫 쓰기 전에 `Backup`. `WriteAsync` → ExternallyChanged → FileChangedExternally / Locked → EnvError / Unencodable → 피드백 + MatchFailed. 성공 시 `Current` 갱신.
    * **Verify:** `SyntaxVerifier.Verify(Current.Text)` 실패 → `Feedback = ForDiagnostics("SYNTAX CHECK FAILED", …)` + NewErrors.
    * **Review:** `ShowDiff(Original.Text, Current.Text)` → `Yes`면 Approved, 아니면 `IsInteractive`면 `ConfirmAsync("적용할까요?", false)`, 비대화형이면 Rejected.
    * **Failed 진입 시:** `BackupPath`가 있으면 `RestoreAsync(BackupPath, Current, CancellationToken.None)`; `ExternallyChanged`면 복원하지 않고 `Warn`으로 백업 경로 안내.
  * `RunLogger(IFileSystem fs, TimeProvider time)`: `void Start(RunContext ctx)`, `void Phase(RunContext ctx, PhaseOutcome outcome, TimeSpan elapsed)`, `void Attempt(RunContext ctx, string prompt, GenerationResult gen, Feedback? feedback)`, `void Complete(RunContext ctx)`; `static string NewRunId(TimeProvider time)` → `yyyyMMdd-HHmmss-xxxx`(소문자 hex 4자리). 시작 시 `retentionDays`보다 오래된 `runs/*` 삭제.
  * `.pelly/runs/<runId>/events.jsonl`(한 줄 = `{ "ts", "phase", "outcome", "attempt", "elapsedMs" }`), `attempt-<n>/prompt.txt`, `think.txt`(있을 때), `output.txt`, `feedback.txt`(있을 때), `summary.json`.
  * `sealed record RunSummary(...)` — 설계 §8.12의 필드 중 M1에서 채울 수 있는 것: `RunId, PellyVersion, OllamaVersion, ModelName, PromptVersion, Kind, ProjectStyle, ExitCode, Attempts, PhaseSeconds, TtftSeconds, ThinkMode, ThinkTokens, ThinkTruncated, InputTokensLocal, InputTokensServer, InputTokensCached, MatchStages, ErrorCodes, FailureKind`. 나머지(`QueueSeconds`)는 M2.

- [ ] **Step 1: 실패하는 테스트 작성** — 임시 폴더에 작은 SDK 스타일 프로젝트(`Demo.csproj` `net8.0` + `Calc.cs` 메서드 `Divide`)를 만들고, `ScriptedLlmBackend`(Task 10)·`ScriptedUserInteraction`으로 실행:
  ```csharp
  [Fact] HappyPath_AppliesPatch_Exit0_LogsSummary()        // 응답 1개(유효 블록), Yes → 종료 0, 파일 변경, summary.json ExitCode 0·Attempts 1
  [Fact] SearchMismatch_ThenFixed_SucceedsOnAttempt2()     // 응답 1: 없는 SEARCH, 응답 2(think 512 + code) → 종료 0, 두 번째 프롬프트에 "SEARCH NOT FOUND" 피드백 포함
  [Fact] SyntaxError_FixForward_SucceedsOnAttempt2()       // 응답 1: 세미콜론 빠진 코드 → 파일에 쓰임 → 응답 2가 그 상태 기준으로 고침 → 종료 0
  [Fact] AllAttemptsFail_RestoresOriginal_Exit1()          // 4번 모두 구문 오류 → 원본 바이트로 복원
  [Fact] ExternalEditDuringGeneration_DoesNotOverwrite()   // 스크립트 백엔드가 응답 직전에 파일을 수정 → 종료 4, 파일 내용 == 외부 수정본 (Review Focus 1)
  [Fact] ServerDown_FailsInPreflightWithoutTouchingFile()  // Status Reachable=false → 종료 4, 백업 없음, 파일 바이트 동일, 메시지에 "pelly doctor" (Review Focus 3)
  [Fact] ModelMissing_FailsInPreflight()
  [Fact] Cp949CrLfFile_PreservesUnchangedBytes()           // 코퍼스 Legacy/Shop.Web/Services/OrderService.cs 복사본, Cancel의 한 줄 바꾸기 → 다른 줄 바이트 동일, CP949 디코드 가능 (Review Focus 4)
  [Fact] CancelAfterWrite_RestoresOriginalBytes()          // 시도 1이 쓰고 구문 오류 → 시도 2 생성 중 취소 → 종료 2, 원본 바이트 (Review Focus 5)
  [Fact] UserRejects_Restores_Exit2()
  [Fact] NonInteractiveWithoutYes_Rejects_Exit2()
  [Fact] DryRun_ShowsDiff_DoesNotWrite_Exit0()
  [Fact] AmbiguousOverload_NonInteractive_Exit3() / [Fact] UnknownMethod_Exit3()
  [Fact] SecondRunWhileLocked_Exit4()
  [Fact] UnhandledException_Restores_Exit4()               // 스크립트 백엔드가 InvalidOperationException
  [Fact] ConflictMarkersInFile_Exit4()
  // RunLoggerTests
  [Fact] RunId_Format()   [Fact] Retention_DeletesOldRuns()   [Fact] AttemptFiles_Written()
  ```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Fsm*" --filter-namespace "*Logging*"` / Expected: 컴파일 실패

- [ ] **Step 3: 구현** — 설계 §7.2 골격. 루프마다 `ui.ShowStatus(phase, attempt, …)`. 취소는 `OperationCanceledException`을 잡아 `PhaseOutcome.Cancelled`로 전이, 그 외 예외는 `Crashed`(로그에 예외 유형·메시지).

- [ ] **Step 4: 통과 확인** — 같은 명령 / Expected: 전부 통과

- [ ] **Step 5: Commit** — `git commit -m "feat(core): add FSM orchestrator, M1 phases, and run logging"`

---

### Task 13: 환경 점검 (`pelly doctor` 기본)

**Files:**
- Create: `src/PellyCoder.Core/Doctor/DoctorService.cs`
- Test: `tests/PellyCoder.Core.Tests/Doctor/DoctorServiceTests.cs`

**Interfaces:**
- Consumes: `ILlmBackend`, `IOllamaDiagnostics`, `LlmServerStatus` (Task 9), `QwenChatTemplate` (Task 8), `ITokenCounter` (Task 5), `PellyOptions` (Task 2)
- Produces:
  * `enum DoctorStatus { Ok, Warn, Fail }`; `sealed record DoctorCheck(string Name, DoctorStatus Status, string Detail, string? Action)`
  * `sealed class DoctorService(ILlmBackend backend, IOllamaDiagnostics diagnostics, QwenChatTemplate template, ITokenCounter tokens, PellyOptions options, TimeProvider time)`: `Task<IReadOnlyList<DoctorCheck>> RunAsync(CancellationToken ct)`
  * 검사 순서와 판정 (설계 §8.13, M1 범위):
    1. `Ollama 서버` — 연결 불가 → Fail("엔드포인트 확인: llm.endpoint")
    2. `모델` — 미설치 → Fail("ollama pull <모델>" 안내), 미로드 → Warn("첫 요청은 모델 로딩으로 느릴 수 있음")
    3. `렌더러` — `Renderer` 표시(Ok, 없으면 Warn)
    4. `템플릿 일치` — 고정 대화 `("You are a test.", "ping")`를 `ChatPromptTokenCountAsync`와 raw(`template.Build` + `ThinkOpen`, `num_predict 1`의 `PromptEvalCount`)로 비교, 같으면 Ok, 다르면 Warn
    5. `토크나이저 일치` — 고정 문자열 `"public int Add(int a, int b) => a + b; // 합계"`의 로컬 수 vs raw `num_predict 1` 서버 수, 다르면 Fail
    6. `응답 시간` — `num_predict 8` 생성의 TTFT 표시(Ok)
    7. `서버 설정 확인 필요` — 항상 Warn, Detail에 PRD §7.1 표의 항목 이름(`OLLAMA_FLASH_ATTENTION`, `OLLAMA_KV_CACHE_TYPE`, `OLLAMA_NUM_PARALLEL`, `OLLAMA_KEEP_ALIVE`)
  * 서버 연결이 Fail이면 3~6은 건너뛰고 상태 `Warn`, Detail "서버 연결 실패로 건너뜀".
  * 출력 형식·종료 코드는 Task 14의 `DoctorCommand`가 정한다.

- [ ] **Step 1: 실패하는 테스트 작성** (가짜 백엔드·진단)
  ```csharp
  [Fact] AllGood_AllOkExceptServerSettingsWarn()
  [Fact] ServerDown_FirstFail_RestSkipped()
  [Fact] ModelNotLoaded_IsWarn()
  [Fact] TemplateMismatch_IsWarn()
  [Fact] TokenizerMismatch_IsFail()
  [Fact] UsesFixedProbeStrings()     // 가짜 백엔드가 받은 RawPrompt에 정해진 문자열이 있음
  ```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*DoctorServiceTests"` / Expected: 컴파일 실패

- [ ] **Step 3: 구현**

- [ ] **Step 4: 통과 확인** — 같은 명령 / Expected: 6개 통과

- [ ] **Step 5: Commit** — `git commit -m "feat(core): add doctor checks for server, model, template, and tokenizer"`

---

### Task 14: CLI (`pelly cs`, `pelly doctor`)

**Files:**
- Create: `src/PellyCoder.Cli/Composition.cs`, `Commands/CsCommand.cs`, `Commands/DoctorCommand.cs`, `Console/SpectreUserInteraction.cs`, `Console/DiffRenderer.cs`
- Modify: `src/PellyCoder.Cli/Program.cs`, `Directory.Packages.props` (`Microsoft.Extensions.DependencyInjection`, `DiffPlex`, `Spectre.Console.Testing`)
- Create: `tests/PellyCoder.Cli.Tests/` (새 테스트 프로젝트, `PellyCoder.sln`에 추가, csproj는 `tests/PellyCoder.Core.Tests`와 같은 구성 — `xunit.v3.mtp-v2`, `--ignore-exit-code 8`) — `CsCommandTests.cs`, `DiffRendererTests.cs`

**Interfaces:**
- Consumes: `FsmOrchestrator`, `RunRequest` (Task 12), `DoctorService` (Task 13), `PellyOptionsLoader`, `PellyPaths`, `WorkspaceLocator` (Task 2), 나머지 서비스 생성자들
- Produces:
  * `interface IRunner { Task<int> RunAsync(RunRequest request, CancellationToken ct); }` — `FsmOrchestrator` 어댑터(명령 테스트에서 가짜로 바꾸기 위함, Cli 프로젝트 안에 둔다).
  * `pelly cs <FILE> <REQUEST> [--class <NAME>] --method <NAME> [--yes] [--dry-run] [--max-retries <N>] [--config <PATH>] [--think <off|retry|always>] [--model <TAG>] [--endpoint <URL>]` — `--think`·`--model`·`--endpoint`는 각각 `llm:thinkMode`·`llm:model`·`llm:endpoint` CLI 덮어쓰기.
    * `--method` 없음 → 종료 4, 메시지 "M1에서는 --method로 대상을 지정해야 합니다 (자동 탐색은 다음 버전)".
    * 요청문이 비었거나 공백 → 종료 4.
    * 설정 오류 → 종료 4, 각 오류 키 출력.
    * 그 외에는 `IRunner.RunAsync` 결과를 그대로 종료 코드로.
  * `pelly doctor [--config <PATH>] [--model <TAG>] [--endpoint <URL>]` — `DoctorService` 결과를 표(이름·상태·내용·조치)로 출력, Fail이 하나라도 있으면 종료 4, 아니면 0.
  * `SpectreUserInteraction : IUserInteraction` — `IsInteractive = !Console.IsInputRedirected && AnsiConsole.Profile.Capabilities.Interactive`. 외부 문자열은 `Markup.Escape`.
  * `static class DiffRenderer { IReadOnlyList<(char Kind, string Text)> UnifiedLines(string before, string after, int context = 3); }` — `Kind`는 `' '`, `'+'`, `'-'`, `'@'`(구간 머리). `SpectreUserInteraction.ShowDiff`가 색을 입힌다.
  * `Program`: 시작 시 `Console.OutputEncoding = Console.InputEncoding = UTF8`, `Encoding.RegisterProvider(CodePagesEncodingProvider.Instance)`, `Console.CancelKeyPress`로 `CancellationTokenSource.Cancel()`(첫 번째는 `e.Cancel = true`로 프로세스 유지).
  * `Composition.Build(PellyOptions options, IUserInteraction ui) → ServiceProvider` — `HttpClient`는 여기서 한 번 생성(`Timeout = Infinite`, 타임아웃은 OllamaBackend가 관리).

- [ ] **Step 1: 실패하는 테스트 작성** (`Spectre.Console.Testing.CommandAppTester`, 가짜 `IRunner`)
  ```csharp
  [Fact] Cs_WithoutMethod_Exit4_WithM1Message()
  [Fact] Cs_EmptyRequest_Exit4()
  [Fact] Cs_PassesOptionsToRunner()     // --yes --dry-run --max-retries 2 --class C --method M → RunRequest 값 일치
  [Fact] Cs_ThinkOption_OverridesConfig()   // --think always → 오케스트레이터에 전달된 옵션 ThinkMode == Always
  [Fact] Cs_InvalidConfig_Exit4_ListsKeys()
  [Fact] Cs_ReturnsRunnerExitCode()     // 가짜가 3 반환 → 종료 3
  [Fact] Diff_UnifiedLines_ContextAndMarkers()
  [Fact] Version_StillWorks()           // --version → "0.1.0"
  ```

- [ ] **Step 2: 실패 확인** — Run: `dotnet test tests/PellyCoder.Cli.Tests` / Expected: 컴파일 실패

- [ ] **Step 3: 구현**

- [ ] **Step 4: 통과 확인** — Run: `dotnet build -warnaserror && dotnet test tests/PellyCoder.Cli.Tests && dotnet run --project src/PellyCoder.Cli -- doctor --model qwen3.6:35b-a3b-coding-nvfp4` / Expected: 테스트 통과, doctor 표가 출력되고 서버·모델·토크나이저 Ok, 종료 0

- [ ] **Step 5: Commit** — `git commit -m "feat(cli): add cs and doctor commands with Spectre interaction"`

---

### Task 15: 실제 모델 E2E와 M1 마무리

**Files:**
- Create: `tests/PellyCoder.Integration.Tests/E2E/CsEndToEndTests.cs`
- Create: `docs/m1-e2e.md` (실행 기록)
- Modify: `README.md` (사용법 절: `pelly cs`, `pelly doctor`, 설정 파일 위치, 종료 코드)
- Modify: `tests/PellyCoder.Integration.Tests/PellyCoder.Integration.Tests.csproj` (Cli 프로젝트 참조 — Composition 재사용)

**Interfaces:**
- Consumes: `Composition.Build` (Task 14), `RunRequest` (Task 12), 벤치마크 판정 규칙(`eval/benchmark/README.md`: 대상 변경 필수 + `mustAdd`)

- [ ] **Step 1: 테스트 작성** (`[OllamaFact]`, 모델·엔드포인트는 `TestEnv`)
  ```csharp
  [OllamaFact] ModernCancelAsync_AddsShippedGuard()
      // eval/corpus/Modern 전체를 임시 폴더로 복사 → RunRequest(file: Shop.Api/Services/OrderService.cs, class: null, method: "CancelAsync",
      //   request: cs-modern-001의 request, Yes: true, DryRun: false) → 종료 0,
      //   수정된 CancelAsync 안의 "OrderStatus.Shipped" 개수 > 원본 개수 (mustAdd), SyntaxVerifier 통과, CancelAsync 외 멤버 텍스트 불변
  [OllamaFact] LegacyCp949Cancel_KeepsEncodingAndSyntax()
      // eval/corpus/Legacy/Shop.Web 복사 → method "Cancel"(OrderService.cs), cs-legacy-001 request → 종료 0,
      //   파일이 여전히 strict UTF-8로 디코드 실패·CP949로 디코드 성공, CRLF 유지, C# 7.3 구문 검사 통과
  [OllamaFact] DryRun_DoesNotModifyCorpusCopy()
  ```
  각 테스트는 `.pelly/runs/*/summary.json`을 테스트 출력(`ITestOutputHelper`)에 남긴다.

- [ ] **Step 2: 실행** — Run:
  ```bash
  PELLY_TEST_OLLAMA=http://localhost:11434 PELLY_TEST_MODEL=qwen3.6:35b-a3b-coding-nvfp4 \
    dotnet test tests/PellyCoder.Integration.Tests -- --filter-namespace "*E2E*" --fail-skips on
  ```
  Expected: 3개 통과. 실패하면 `summary.json`·`attempt-*` 파일로 원인을 찾는다 — 프롬프트·예산 문제면 해당 Task의 리소스를 고치고 그 Task의 테스트를 다시 돌린 뒤 여기로 돌아온다(모델 출력의 우연한 실패를 테스트 기대값 완화로 덮지 않는다). 같은 테스트를 3회 반복해 통과 횟수를 `docs/m1-e2e.md`에 기록한다.

- [ ] **Step 3: CLI로 직접 실행해 기록** — 코퍼스 복사본에서:
  ```bash
  dotnet run --project src/PellyCoder.Cli -- cs <복사본>/Shop.Api/Services/OrderService.cs \
    "배송 중(Shipped)인 주문을 취소하려고 하면 InvalidOperationException을 던지도록 수정해줘" \
    --method CancelAsync --yes --model qwen3.6:35b-a3b-coding-nvfp4
  ```
  `docs/m1-e2e.md`에 종료 코드, 시도 횟수, 단계별 시간, TTFT, think 사용 여부, diff를 남긴다(설계 §2.2 단일 작업 완료 시간과 비교).

- [ ] **Step 4: 전체 검증** — Run: `dotnet build -warnaserror && dotnet test` / Expected: 실패 0. push 후 `gh run watch --exit-status` → 4개 잡 성공.

- [ ] **Step 5: Commit** — `git commit -m "test(e2e): verify M1 cs path against local model and document run"`

---

## M1 완료 기준 (설계 §11)
* Modern 코퍼스에서 `pelly cs`가 실제 모델로 1건 이상 성공(Task 15).
* 설계 §10.2 중 M1 구성 요소(Transitions, Parser/Patcher, FileGuard, RoslynSlicer, QwenStreamClient, Orchestrator)의 단위 테스트 통과.
* CI 4개 잡 성공.

## M1에서 하지 않는 것 (M2 이후)
빌드 검증(`BuildToolResolver`·`BuildVerifier`·Baseline 병렬화), `MemberShapeGuard`, `--line`·요청문 기반 탐색·모델 후보 선택, 요청문 대화형 입력, SQL 경로 전체, `report`·`--sanitize`, `--test`, `--pause-on-exit`, DTO 슬라이스, 형상 관리(git) 경고.
