# Pelly-Coder M3a (리포트·doctor 완성·DTO 슬라이스·--test) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** M3a는 v1 제품 기능을 완성한다. 대상은 다음 일곱 가지다.
* `pelly report`와 `--sanitize`(비식별 리포트)
* `pelly doctor` 완성(pelly 자체·타겟팅 팩·서버 체크리스트·`--sanitize`·`--offline-selftest`)
* DTO 슬라이스
* `--test`(새로 실패한 테스트만 실패)
* `--verbose`
* `--pause-on-exit`
* 서버 대기 시간 분리

M2b에서 넘어온 검증 빈틈 두 개도 닫는다.

**Architecture:**
* 리포트는 `.pelly/runs/*/summary.json`만 읽는다. 비식별 리포트가 다른 파일을 읽지 않도록, 파싱 실패 수를 `RunSummary`에 숫자로 넣는다.
* 비식별 출력은 허용 목록 방식이다(D11): 타입과 형식이 정해진 값만 통과한다.
* `--test`는 기존 `IVerifierStep` 파이프라인에 `TestStep`을 더한다. 원본 상태의 테스트 결과는 Baseline 빌드 직후 같은 작업 안에서 얻는다(§8.9 4번).
* DTO는 대상 멤버가 쓰는 타입 중 프로젝트 안에 선언된 것만 찾아, 우선순위 2의 `[READ-ONLY: dto]` 파트로 넣는다(§8.5).

**Tech Stack:** .NET 10, Roslyn 5.9, ScriptDom 170.191.0, Spectre.Console.Cli 0.57.2, System.Text.Json, xUnit v3(MTP), TRX(VSTest 결과 XML)

**Spec:** `docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md`
* 결정 사항: D11, D18, D19, D20
* 절: §8.2(QwenStreamClient), §8.5(DTO), §8.9 4번(`--test`), §8.12(리포트·`--sanitize`), §8.13(doctor), §8.14(`--pause-on-exit`), §10.4(`--offline-selftest`), §11 M3 행

PRD v1.8:
* FR-00(`--test`·`--verbose`·`--pause-on-exit`)
* FR-06 5번
* FR-08, FR-09
* §5(공용 서버 대기, 성능 가시성)

M2b 이월 근거: 원장 사본 `/private/tmp/claude-501/-Users-payletter-git-root-ReSet/80aff902-238e-4e70-8e26-123252df0fa0/scratchpad/m2b-ledger.md`의 `Final:` 줄(M-1), M-2.

**범위 밖 (M3b로 넘김):**
* 벤치마크 50건과 테스트 연동 판정
* 프롬프트·샘플링 튜닝
* `win-x64` 단일 파일 publish(S6)와 `publish-check` CI 잡
* SBOM·라이선스 목록·체크섬·버전 기록·`pelly.json` 템플릿·반입 zip
* `deploy/` 설치·사용·IDE External Tools 가이드
* S7(Rider 실기 확인, 사람이 함께 함)
* `deploy.sh` 재실행 실패

## Global Constraints

* **이어받는 제약:** M1·M2a·M2b 계획의 Global Constraints를 그대로 따른다.
  * 빌드 환경: SDK `10.0.302`, `net10.0`, Nullable·ImplicitUsings·TreatWarningsAsErrors
  * 패키지: CPM(간접 고정), MIT/Apache-2.0 라이선스만
  * Core 규칙: `Console.*` 금지(D20), 외부 프로세스는 `IProcessRunner`, DB는 `ISqlExecutor`
  * 종료 코드는 0~5만 쓴다.
  * 환경 테스트 속성: `[WindowsFact]`/`[SqlServerFact]`/`[OllamaFact]`/`[OllamaSqlFact]`
  * 언어: 식별자·커밋은 영어, 문서는 한국어
  * 커밋 끝에 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`
* **네트워크:** 새 코드는 설정된 Ollama·SQL 서버 외에 접속하지 않는다. `--offline-selftest`는 어떤 네트워크 객체도 만들지 않는다.
* **비식별 출력(D11, §8.12):** 허용 목록 밖의 문자열은 출력하지 않는다. 출력할 수 있는 값은 다음뿐이다.
  * `ErrorCodes`: 정규식 `^(CS|MSB|NU|Msg|SQL)\d+$` 또는 정확히 `TEST`(R8)
  * 버전 문자열: `^[0-9A-Za-z.+\-]{1,40}$`. 그 밖은 `"invalid"`.
  * `PromptVersion`: `^(cs|sql)-v\d+$`
  * `FailureKind`: `FailureKinds.IsKnown` 통과 값. 그 밖은 `"Other"`.
  * `ProjectStyle`: `Sdk|Legacy|None|Sql`. 그 밖은 `"Other"`.
  * `ThinkMode`: `Off|Retry|Always`
  * `ModelName`·`RunId`·경로는 출력하지 않는다.
* **새 설정 기본값:** `test.command = null`, `test.projects = []`, `test.timeoutSeconds = 300`(R11).
* **Windows:** POSIX 전용 동작을 넣을 때는 Windows 동작도 정한다. 실기 Windows에서 못 잰 경로(레거시 `test.command`, 타겟팅 팩 경로, 콘솔 키 대기)는 코드 주석과 문서에 「Windows 미확인」으로 남긴다.
* **CI:** 각 작업을 마치기 전에 브랜치를 push해 CI 4잡 녹색을 확인한다.

## Review Focus

1. **비식별 리포트에 소스 문자열이 섞여 들어옴**
   * 원인: `summary.json`의 문자열 필드(`FailureKind`·`ErrorCodes`·버전·`ModelName`)에 경로·식별자·SQL 조각이 들어 있는 경우.
   * 기대 동작: JSON·Markdown 어디에도 나오지 않는다.
   * 테스트: Task 4 `Sanitize_InjectedStrings_NeverAppear`, Task 11 `DoctorSanitize_InjectedDetail_NeverAppears`
2. **External Tools 창에서 인자 오류·예외로 끝나는 경우**
   * 기대 동작: 그래도 결과를 보여 주고 키 입력을 기다린다.
   * 입력이 리디렉션된 실행(CI·벤치마크)에서는 절대 기다리지 않는다.
   * 테스트: Task 6 `ShouldPause_ParseErrorStillPauses`, `ShouldPause_RedirectedInput_NeverPauses`
3. **`--test`에서 수정 전부터 실패하던 테스트, 수정 뒤 테스트 프로젝트 빌드 실패, 테스트 시간 초과**
   * 기대 동작은 각각 다음과 같다.
     * 원래 실패하던 테스트는 세지 않는다.
     * 빌드 실패는 피드백으로 재시도한다(크래시 아님).
     * 시간 초과는 종료 4와 원본 복원.
   * 테스트: Task 9 `PreexistingFailure_IsNotCounted`, `TestProjectBuildBreaks_IsFeedback`, `TestTimeout_Exit4_Restored`
4. **DTO 탐색이 큰 프로젝트·CP949 파일·깨진 파일을 만남**
   * 기대 동작: 멈추거나 실패하지 않고 상한(파일 5,000개, 파일당 1MB) 안에서 끝난다. 못 찾으면 dto 파트 없이 진행한다.
   * 테스트: Task 7 `Cp949AndBrokenFiles_DoNotFail`, `FileCap_StopsScanning`
5. **공용 서버가 바빠 첫 토큰이 늦음**
   * 기대 동작: 3초 뒤부터 매초 "서버 응답 대기 중" 상태를 보이고, 대기 시간은 `QueueSeconds`로 TTFT와 분리한다.
   * 스트림 무응답 시간 제한의 의미는 바꾸지 않는다.
   * 테스트: Task 2 `SlowFirstToken_ShowsWaitingStatus_AndRecordsQueue`

## 결정 (계획 단계 판단, 틀렸을 때 비용 포함)

* **R1. 설계 §11의 M3를 M3a(제품 기능)와 M3b(벤치마크 50건·튜닝·반입 패키지)로 나눈다.**
  * 이유: 튜닝은 DTO·`--test`가 들어간 최종 제품으로 해야 하고, 패키지는 튜닝 뒤에 만든다.
  * 틀렸을 때 비용: 계획이 하나 더 늘어난다.
* **R2. `RunSummary`에 `int FormatErrors`(EDIT 결과가 FormatError였던 횟수)를 더한다.**
  * 이유: 리포트·비식별 리포트가 `summary.json`만 읽고도 파싱 성공률을 낼 수 있다(§8.12 "다른 파일은 읽지 않음").
  * 틀렸을 때 비용: 요약 스키마가 한 필드 늘어난다.
* **R3. `QueueSeconds = max(0, 클라이언트 TTFT − load_duration − prompt_eval_duration)`로 계산하고, `TtftSeconds`에는 대기를 뺀 값을 남긴다.**
  * 이유: PRD §5 "대기 시간은 TTFT와 분리해 기록".
  * 틀렸을 때 비용: 첫 토큰 생성 시간 등 작은 오차가 대기로 잡힐 수 있다.
  * 참고: M2b 기준선은 단독 사용이라 대기 ≈ 0이다.
* **R4. `--verbose`는 실행이 끝난 뒤 단계별 시간·토큰·시도 요약과 실행 폴더 경로를 보여 준다.**
  * 프롬프트 원문 자체는 출력하지 않고 `attempt-N/prompt.txt` 경로를 안내한다.
  * 이유: 프롬프트는 4천 토큰이라 콘솔을 덮는다.
  * 틀렸을 때 비용: 프롬프트를 보려면 파일을 열어야 한다.
* **R5. DTO에 `enum`(멤버 목록)과 public 필드도 넣는다.**
  * 이유: `OrderStatus.Shipped` 같은 값과 레거시 DTO의 필드가 모델에 가장 필요한 정보다.
  * 설계 §8.5는 class/record/struct/interface의 프로퍼티만 적고 있다. Task 15에서 갱신한다.
  * 틀렸을 때 비용: dto 파트가 조금 커진다(우선순위 2라 예산 초과 시 먼저 빠진다).
* **R6. `.sln`이 없는 작업 공간에서는 작업 공간의 `*.csproj`(bin/obj 제외)를 같은 규칙(D18)으로 훑는다.**
  * 틀렸을 때 비용: 솔루션 밖 테스트 프로젝트가 잡힐 수 있다.
* **R7. 레거시 `test.command`에 `{trx}` 자리 표시자가 있으면 테스트별로 비교한다.**
  * 없으면 종료 코드만 비교한다: 원본이 통과했는데 지금 실패하면 실패, 원본도 실패했으면 "비교할 수 없음" 알림.
  * 틀렸을 때 비용: `{trx}`를 안 쓰는 레거시 프로젝트는 판정이 거칠다.
* **R8. 테스트 실패의 진단 코드는 `TEST`로 하고, 비식별 허용 목록에 넣는다.**
* **R9. doctor 비식별 출력은 행 이름(알려진 목록)·상태와, 행마다 정한 키의 `Facts`만 남긴다.** `Detail`·`Action` 문장은 출력하지 않는다.
* **R10. `--pause-on-exit`는 `Program`에서 원시 인자로 판정한다.**
  * 이유: 인자 해석 오류·예외로 끝나도 창이 바로 닫히지 않게 한다.
* **R11. 새 설정 `test.timeoutSeconds = 300`을 둔다.**
  * 이유: 빌드 시간 제한(120초)과 별개다. 테스트 실행이 더 길 수 있다.

---

## 파일 구조 (신규·변경)

```text
src/PellyCoder.Core/
├── Reporting/
│   ├── FailureKinds.cs            # 신규: 알려진 FailureKind 카탈로그
│   ├── KpiMath.cs                 # 신규: 최근접 순위 백분위, 파싱 성공률
│   ├── RunHistory.cs              # 신규: summary.json 읽기
│   ├── RunReport.cs               # 신규: 집계
│   └── ReportSanitizer.cs         # 신규: 비식별 JSON/Markdown
├── Logging/RunLogger.cs           # 변경: FormatErrors, QueueSeconds
├── Abstractions/ILlmBackend.cs, IUserInteraction.cs   # 변경: LoadDuration, ShowRunSummary
├── Llm/OllamaBackend.cs, QwenStreamClient.cs          # 변경: load_duration, 대기 콜백
├── CSharp/DtoCollector.cs, RoslynSlicer.cs            # 신규·변경
├── Files/TextDecoding.cs          # 신규: FileSnapshot의 디코딩 규칙 추출
├── Tests/                         # 신규 (네임스페이스 PellyCoder.Core.Testing — 타입 이름과 겹치지 않게)
│   ├── TestProjectLocator.cs, TestRunner.cs, TrxParser.cs
├── Verification/TestStep.cs       # 신규
├── Config/PellyOptions.cs, PellyOptionsLoader.cs      # 변경: TestOptions
├── Fsm/…                          # 변경: RunRequest(Verbose, RunTests), Locate·Apply·Edit·Orchestrator
├── Feedback/FeedbackBuilder.cs    # 변경: ForTests
├── Doctor/DoctorService.cs, DoctorSanitizer.cs, OfflineSelfTest.cs   # 변경·신규
└── Sql/SqlServerExecutor.cs, SqlReferenceCollector.cs # 변경: M2b 이월
src/PellyCoder.Cli/
├── Commands/RunSettings.cs (신규), CsCommand.cs, SqlCommand.cs, DoctorCommand.cs, ReportCommand.cs (신규)
├── ExitPause.cs (신규), Program.cs, CliApp.cs, Terminal/SpectreUserInteraction.cs
deploy/server-checklist.md          # 신규 (Cli에 EmbeddedResource로 연결)
eval/PellyCoder.Eval/KpiReport.cs   # 변경: Core KpiMath 사용
tests/…                             # 각 구성 요소 테스트, fixtures/tests/*.trx
docs/m3a-e2e.md, docs/m3a-benchmark.md                 # 신규
```

---

### Task 1: 리포팅 기반 — FailureKind 카탈로그·KPI 계산·FormatErrors

**Files:**
- Create: `src/PellyCoder.Core/Reporting/FailureKinds.cs`, `KpiMath.cs`
- Modify: `src/PellyCoder.Core/Logging/RunLogger.cs`, `src/PellyCoder.Core/Fsm/RunRequest.cs`(RunContext), `src/PellyCoder.Core/Fsm/Phases/EditPhase.cs`, `eval/PellyCoder.Eval/KpiReport.cs`
- Test: `tests/PellyCoder.Core.Tests/Reporting/FailureKindsTests.cs`, `KpiMathTests.cs`, `tests/PellyCoder.Core.Tests/Logging/RunLoggerTests.cs`

**Interfaces:**
- Produces:
  * `static class FailureKinds`
    * 지금 코드가 쓰는 모든 값을 `const string`으로 둔다.
      * `PatchFailureKinds`의 6개
      * `BaselineErrors`, `BuildToolUnavailable`, `BuildUnverifiable`, `Crashed`, `FileChangedExternally`, `ShapeRejected`
      * `SqlForbiddenInOriginal`, `SqlParseErrors`, `SqlPermission`, `SqlSafetyRejected`, `SqlTargetNotAllowed`
      * `TargetAmbiguous`, `TargetNotFound`
      * `SyntaxErrors`, `BuildErrors`, `SqlObjectsErrors`, `SqlCompileErrors`, `TestErrors`(Task 9에서 생김)
      * `EnvError`, `BudgetExceeded`, `Rejected`, `Cancelled`
    * `IReadOnlySet<string> All`, `bool IsKnown(string? kind)`
  * `static class KpiMath`
    * `double? Percentile(IEnumerable<double> values, double p)` — 최근접 순위법, 빈 입력이면 null
    * `(int Ok, int Total) ParseSuccess(IEnumerable<(int Attempts, int FormatErrors)> runs)`
  * `RunSummary`에 `int FormatErrors`를 `ErrorCodes` 다음, `FailureKind` 앞에 넣는다. `RunContext.FormatErrors`는 EditPhase가 FormatError를 돌려줄 때 1 증가한다.
  * Eval `KpiReport`의 백분위·파싱 성공률 계산은 `KpiMath`를 호출하도록 바꾼다. 결과는 바뀌지 않아야 한다(기존 Eval 테스트 그대로 통과).
- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] EveryFailureKindLiteralInSource_IsKnown()   // src/ 아래 *.cs에서 FailureKind = "..." 리터럴과 IVerifierStep.Name + "Errors"를 모두 모아 IsKnown 단언 (소스 스캔)
  [Fact] IsKnown_RejectsFreeText()                   // "C:\\x\\a.cs", "", null → false
  [Theory] Percentile_NearestRank([1..10], 0.5 → 5, 0.9 → 9) ([3], 0.9 → 3)   [Fact] Percentile_Empty_IsNull()
  [Fact] ParseSuccess_SumsAttemptsMinusFormatErrors()  // (3,1),(1,0) → (3,4)
  [Fact] Summary_CountsFormatErrors()                // FormatError 두 번 뒤 성공한 실행 → FormatErrors 2
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Reporting*"` 그리고 `dotnet test tests/PellyCoder.Eval.Tests`
- [ ] **Step 5: Commit** — `git commit -m "feat(reporting): add failure-kind catalog, KPI math and format-error count"`

---

### Task 2: 서버 대기 시간 분리·"서버 응답 대기 중" 표시

**Files:**
- Modify: `src/PellyCoder.Core/Abstractions/ILlmBackend.cs`, `src/PellyCoder.Core/Llm/OllamaBackend.cs`, `QwenStreamClient.cs`, `src/PellyCoder.Core/Fsm/Phases/EditPhase.cs`, `src/PellyCoder.Core/Fsm/FsmOrchestratorFactory.cs`, `src/PellyCoder.Core/Logging/RunLogger.cs`
- Test: `tests/PellyCoder.Core.Tests/Llm/QwenStreamClientTests.cs`, `OllamaBackendTests.cs`, `tests/PellyCoder.Core.Tests/Logging/RunLoggerTests.cs`

**Interfaces:**
- Consumes: `RunSummary`(Task 1)
- Produces:
  * `LlmChunk`의 맨 끝에 `TimeSpan? LoadDuration = null`을 더한다. OllamaBackend가 마지막 줄의 `load_duration`(ns)을 읽어 채운다.
  * `LlmUsage`에 `TimeSpan Queue`를 더한다(R3 계산).
  * `QwenStreamClient.GenerateEditAsync(string basePrompt, int attempt, CancellationToken ct, Action<TimeSpan>? onWaiting = null)`
    * 요청마다 첫 청크가 오기 전 3초가 지나면, 그 뒤 매초 `onWaiting(경과)`를 부른다.
    * 타이머는 주입된 `TimeProvider`를 쓴다.
  * `RunSummary`에 `double QueueSeconds`를 `TtftSeconds` 다음에 넣는다(설계 §8.12 위치). `TtftSeconds = (Ttft − Queue)`초.
  * `EditPhase` 생성자에 `IUserInteraction ui`를 더한다.
    * 대기 콜백에서 `ui.ShowStatus(RunPhase.Edit, ctx.Attempt, $"서버 응답 대기 중 ({경과:0}초) — 다른 요청을 처리 중이거나 모델을 불러오는 중일 수 있습니다")`를 부른다.
- [ ] **Step 1: 실패하는 테스트 작성** (`FakeTimeProvider` + 늦게 첫 청크를 주는 가짜 백엔드)
  ```csharp
  [Fact] SlowFirstToken_ShowsWaitingStatus_AndRecordsQueue()  // 첫 청크 6초 뒤, load 1s·prompt_eval 0.5s → onWaiting 호출 3·4·5초, Queue 4.5s, 요약 TtftSeconds 1.5, QueueSeconds 4.5
  [Fact] FastFirstToken_NoWaitingCallback()                   // 1초 → 호출 0
  [Fact] Queue_NeverNegative()                                // load+prompt_eval > TTFT → 0
  [Fact] OllamaBackend_ParsesLoadDuration()                   // 녹화 NDJSON 마지막 줄 load_duration
  [Fact] StreamIdleTimeout_Unchanged()                        // 기존 무응답 시간 제한 테스트 그대로 통과
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Llm*" --filter-namespace "*Logging*" --filter-namespace "*Fsm*"`
- [ ] **Step 5: Commit** — `git commit -m "feat(llm): separate server queue time from TTFT and show a waiting status"`

---

### Task 3: `pelly report`

**Files:**
- Create: `src/PellyCoder.Core/Reporting/RunHistory.cs`, `RunReport.cs`, `src/PellyCoder.Cli/Commands/ReportCommand.cs`
- Modify: `src/PellyCoder.Cli/CliApp.cs`, `Composition.cs`
- Test: `tests/PellyCoder.Core.Tests/Reporting/RunHistoryTests.cs`, `RunReportTests.cs`, `tests/PellyCoder.Cli.Tests/ReportCommandTests.cs`

**Interfaces:**
- Consumes: `KpiMath`, `FailureKinds`, `RunSummary`(Task 1·2)
- Produces:
  * `sealed record RunRecord(RunSummary Summary, DateTimeOffset StartedUtc)` — `StartedUtc`는 RunId(`yyyyMMdd-HHmmss-xxxx`)에서 해석한다.
  * `static class RunHistory { RunHistoryResult Load(IFileSystem fs, Workspace ws, DateOnly? since); }`
    * `sealed record RunHistoryResult(IReadOnlyList<RunRecord> Runs, int Unreadable)`
    * 읽거나 해석할 수 없는 `summary.json`은 세기만 하고 건너뛴다.
  * `static class RunReport { RunReportData Build(IReadOnlyList<RunRecord> runs); }`
    * 필드: 실행 수(종류별), 성공(종료 0) 수·비율, 종료 코드 분포
    * TTFT·대기·작업 시간(단계 합)의 중위·P90(`TtftSeconds > 0`만)
    * 파싱 성공률(R2), think 최대·잘림 수
    * 실패 유형 분포, 단계별 평균 시간, 시도 수 분포
  * `pelly report [--since yyyy-MM-dd] [--sanitize] [--out <폴더>]`
    * 작업 공간은 현재 폴더 기준 `WorkspaceLocator.Resolve`로 정한다.
    * 표 순서는 PRD §2.2를 따른다. 검증 불변 조건 행은 "독립 검증 없음 — 벤치마크 실행기로 측정"으로 표시한다.
    * 기록이 없으면 "실행 기록이 없습니다: {runs 폴더}"를 보이고 종료 0.
    * `--sanitize` 옵션은 Task 4에서 추가한다(이 작업에서는 두지 않는다).
- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] Load_ReadsSummaries_CountsUnreadable()   [Fact] Load_SinceFiltersByRunIdDate()
  [Fact] Build_RatesAndPercentiles()               // 성공 3/4, TTFT 0인 실행 제외, 파싱 성공률 (Attempts−FormatErrors)/Attempts
  [Fact] Build_FailureKindDistribution_UnknownAsOther()
  [Fact] Report_NoRuns_Exit0_WithPath()  [Fact] Report_PrintsPrdRowsInOrder()
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Reporting*"` 그리고 `dotnet test tests/PellyCoder.Cli.Tests`
- [ ] **Step 5: Commit** — `git commit -m "feat(cli): add pelly report"`

---

### Task 4: `report --sanitize`

**Files:**
- Create: `src/PellyCoder.Core/Reporting/ReportSanitizer.cs`
- Modify: `src/PellyCoder.Cli/Commands/ReportCommand.cs`
- Test: `tests/PellyCoder.Core.Tests/Reporting/ReportSanitizerTests.cs`, `tests/PellyCoder.Cli.Tests/ReportCommandTests.cs`

**Interfaces:**
- Consumes: `RunHistory`, `RunReport`(Task 3)
- Produces:
  * `static class ReportSanitizer { SanitizedReport Sanitize(IReadOnlyList<RunRecord> runs, RunReportData aggregate); }`
  * `sealed record SanitizedReport(...)`: Global Constraints의 허용 목록 필드만 담는다. `string ToJson()`, `string ToMarkdown()`.
  * 실행별 행에 `RunId` 대신 1부터 매긴 순번을 쓴다.
  * `--sanitize`는 `--out`(기본: 현재 폴더)에 `pelly-report-<yyyyMMdd-HHmmss>.json`과 `.md`를 쓰고, 두 경로를 출력한다.
  * 소스 문자열은 `summary.json`만 읽는다(events·attempt 파일을 열지 않는다).
- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] Sanitize_InjectedStrings_NeverAppear()
  // 요약에 넣는 문자열:
  //   ModelName "secret-model"
  //   FailureKind @"C:\Users\kim\OrderService.cs"
  //   ErrorCodes ["CS0103","CustomerService.Cancel","Msg207 dbo.Orders","TEST"]
  //   PellyVersion "1.0; DROP TABLE"
  //   OllamaVersion "0.40.0"
  //   PromptVersion "cs-v2 OrderService"
  //   RunId "20261018-101010-abcd"
  // → JSON·MD 모두에 "secret-model", "Users", "OrderService", "CustomerService", "dbo.Orders", "DROP", "20261018-101010" 없음
  // → "CS0103", "TEST", "0.40.0", "Other", "invalid" 있음
  [Fact] Sanitize_KeepsNumbers()        // PhaseSeconds, TTFT, QueueSeconds, 토큰 수, 시도 수 그대로
  [Fact] Sanitize_DoesNotOpenOtherFiles()   // 가짜 파일 시스템에서 events.jsonl 읽기 시 예외 → 리포트 성공
  [Fact] ReportCommand_Sanitize_WritesTwoFiles()
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인** (같은 명령)
- [ ] **Step 5: Commit** — `git commit -m "feat(reporting): add whitelist-based sanitized report (D11)"`

---

### Task 5: `--verbose`

**Files:**
- Modify: `src/PellyCoder.Core/Abstractions/IUserInteraction.cs`, `src/PellyCoder.Core/Fsm/RunRequest.cs`, `FsmOrchestrator.cs`, `src/PellyCoder.Cli/Terminal/SpectreUserInteraction.cs`, `Commands/CsCommand.cs`, `SqlCommand.cs`
- Modify(가짜 구현): `tests/PellyCoder.Core.Tests/Fakes/ScriptedUserInteraction.cs`, `tests/PellyCoder.Integration.Tests/E2E/RecordingUserInteraction.cs`, `tests/PellyCoder.Cli.Tests/FakeUi.cs`, `eval/PellyCoder.Eval/EvalUserInteraction.cs`
- Test: `tests/PellyCoder.Core.Tests/Fsm/FsmOrchestratorTests.cs`, `tests/PellyCoder.Cli.Tests/SpectreUserInteractionTests.cs`

**Interfaces:**
- Produces:
  * `RunRequest` 맨 끝에 `bool Verbose = false`를 더한다.
  * `IUserInteraction.ShowRunSummary(RunSummary summary, string? runDirectory)`
    * 오케스트레이터가 `finally`에서 요약을 쓴 뒤, `Verbose`일 때만 부른다. 최선 노력: 예외가 결과를 바꾸지 않는다.
  * Spectre 출력(R4):
    * 단계별 초, 시도 수
    * TTFT·대기, 입력 토큰 local/server/cached, think 토큰·잘림
    * 매칭 단계, 오류 코드
    * `프롬프트·응답 원문: {runDirectory}` 한 줄
  * `--verbose` 옵션을 `cs`·`sql`에 둔다(Task 6에서 공통 설정으로 옮김).
- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] Verbose_ShowsSummaryOnce_WithRunDir()   [Fact] NotVerbose_NoSummary()
  [Fact] ShowRunSummaryThrows_ExitCodeUnchanged()
  [Fact] Spectre_ShowRunSummary_PrintsPhasesTokensAndPath()
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Fsm*"` 그리고 `dotnet test tests/PellyCoder.Cli.Tests`
- [ ] **Step 5: Commit** — `git commit -m "feat(cli): add --verbose run summary"`

---

### Task 6: `--pause-on-exit`·공통 실행 옵션

**Files:**
- Create: `src/PellyCoder.Cli/Commands/RunSettings.cs`, `src/PellyCoder.Cli/ExitPause.cs`
- Modify: `src/PellyCoder.Cli/Commands/CsCommand.cs`, `SqlCommand.cs`, `src/PellyCoder.Cli/Program.cs`
- Test: `tests/PellyCoder.Cli.Tests/ExitPauseTests.cs`, `CsCommandTests.cs`, `SqlCommandTests.cs`

**Interfaces:**
- Produces:
  * `abstract class RunSettings : CommandSettings` — cs·sql 공통 옵션: `--yes`, `--dry-run`, `--max-retries`, `--config`, `--think`, `--model`, `--endpoint`, `--verbose`, `--pause-on-exit`.
    * `CsSettings`·`SqlSettings`가 상속한다.
    * 옵션 이름·설명·검증 문구는 바꾸지 않는다(기존 Cli 테스트 그대로 통과).
  * `static class ExitPause`
    * `bool ShouldPause(IReadOnlyList<string> args, bool inputRedirected)` — `--pause-on-exit`가 인자에 있고(대소문자 구분, `--` 뒤 인자는 무시) 입력이 리디렉션되지 않았을 때만 참.
    * `void Wait(IAnsiConsole console, int exitCode, Func<bool> keyAvailable, Action readKey, CancellationToken shutdown)`
      * `[grey]종료 코드 {code}. 아무 키나 누르면 창을 닫습니다…[/]`를 출력한다.
      * 키 입력이나 종료 요청(Ctrl+C·창 닫기)까지 50ms 간격으로 기다린다.
  * `Program`은 `app.RunAsync` 결과 뒤에 판정·대기하고 원래 종료 코드를 돌려준다(R10). 인자 오류·예외로 끝나도 대기한다.
  * 콘솔 키 대기는 「Windows 미확인」 주석을 단다.
- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] ShouldPause_FlagAndConsole_True()  [Fact] ShouldPause_RedirectedInput_NeverPauses()
  [Fact] ShouldPause_AfterDoubleDash_Ignored()  [Fact] ShouldPause_ParseErrorStillPauses()   // 알 수 없는 옵션과 함께 → 참
  [Fact] Wait_ReturnsOnKey()  [Fact] Wait_ReturnsOnShutdown()  [Fact] Wait_PrintsExitCode()
  [Fact] CsAndSql_AcceptPauseOnExitAndVerbose()
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Cli.Tests`
- [ ] **Step 5: Commit** — `git commit -m "feat(cli): add --pause-on-exit and shared run options"`

---

### Task 7: DTO 슬라이스

**Files:**
- Create: `src/PellyCoder.Core/CSharp/DtoCollector.cs`, `src/PellyCoder.Core/Files/TextDecoding.cs`
- Modify: `src/PellyCoder.Core/Files/FileSnapshot.cs`(디코딩을 `TextDecoding`으로 위임), `src/PellyCoder.Core/CSharp/RoslynSlicer.cs`, `src/PellyCoder.Core/Fsm/Phases/SlicePhase.cs`, `src/PellyCoder.Core/Llm/PromptBuilder.cs`, `FsmOrchestratorFactory.cs`
- Test: `tests/PellyCoder.Core.Tests/CSharp/DtoCollectorTests.cs`, `RoslynSlicerTests.cs`, `tests/PellyCoder.Core.Tests/Llm/PromptBuilderTests.cs`

**Interfaces:**
- Produces:
  * `static class TextDecoding { string Decode(byte[] bytes); }` — FileSnapshot과 같은 BOM → 엄격 UTF-8 → CP949 순서. FileGuard 동작은 바뀌지 않는다(기존 FileGuard 테스트 통과).
  * `static class DtoCollector`
    * 시그니처: `string? Collect(IFileSystem fs, string searchRoot, string sourceLf, MemberCandidate target, LanguageVersion version, DtoLimits? limits = null)`
    * `sealed record DtoLimits(int MaxTypes = 5, int MaxMembers = 30, int MaxFiles = 5000, long MaxFileBytes = 1_048_576)`
    * 후보 이름: 대상 멤버의 매개변수·반환 타입, `IdentifierNameSyntax`·`GenericNameSyntax` 이름 중 대문자로 시작하는 것. 처음 나온 순서이고, 대상을 담은 타입 자신은 뺀다.
    * 탐색: `searchRoot` 아래 `*.cs`(`bin`·`obj`·`.pelly`·`.git` 제외)를 정규식 `\b(class|record|struct|interface|enum)\s+(이름들)\b`로 훑고, 맞는 파일만 파싱한다.
    * 렌더 형식(한 타입 한 줄, R5):
      * `class Order: int OrderId; OrderStatus Status; decimal ShippingFee; …`(프로퍼티와 public 필드)
      * `enum OrderStatus: Pending, Paid, Shipped, Delivered, Cancelled`
      * 상한을 넘으면 `; …`를 붙인다.
    * 찾은 것이 없으면 null.
  * `RoslynSlicer.Build(..., string? dtoText = null)` — 파트 `dto`, 우선순위 2.
  * `SlicePhase`는 `Target.ProjectPath`의 폴더(없으면 대상 파일 폴더)를 `searchRoot`로 쓴다. 탐색 예외는 dto 없이 진행한다(알림 없음).
  * `PromptBuilder.ContextOrder`: `fields` → `dto`(`[READ-ONLY: dto]`) → `signatures` → `usings` → `helpers` → `target`.
- [ ] **Step 1: 실패하는 테스트 작성** (Modern 코퍼스 실제 파일 + MockFileSystem)
  ```csharp
  [Fact] CancelAsync_DtoHasOrderStatusEnumAndOrder()     // "enum OrderStatus: Pending" 와 "class Order:" 포함, OrderService 자신 없음
  [Fact] CalculateRefundAsync_DtoHasOrderItem()           // "IsGiftItem", "IsRefundable" 포함
  [Fact] LegacyPublicFields_AreIncluded()
  [Fact] Limits_MaxTypesAndMembers()                      // 6번째 타입 없음, 31번째 멤버 대신 "…"
  [Fact] Cp949AndBrokenFiles_DoNotFail()                  // CP949 DTO 파일은 읽힘, 문법 깨진 파일은 무시
  [Fact] FileCap_StopsScanning()                          // MaxFiles 3 → 4번째 파일의 타입 없음
  [Fact] BinObjExcluded()
  [Fact] Slice_DtoRemovedAfterSignatures_BeforeUsings()   // 예산 단계별
  [Fact] Prompt_DtoSectionOrder()
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*CSharp*" --filter-namespace "*Llm*" --filter-namespace "*Files*" --filter-namespace "*Fsm*"`
- [ ] **Step 5: Commit** — `git commit -m "feat(slicer): add DTO slice for project-declared types used by the target"`

---

### Task 8: 테스트 프로젝트 탐색·테스트 실행기

**Files:**
- Create: `src/PellyCoder.Core/Tests/TestProjectLocator.cs`, `TestRunner.cs`, `TrxParser.cs` (네임스페이스 `PellyCoder.Core.Testing`)
- Modify: `src/PellyCoder.Core/Config/PellyOptions.cs`, `PellyOptionsLoader.cs`
- Test: `tests/PellyCoder.Core.Tests/Testing/*Tests.cs`, `tests/fixtures/tests/modern-pass.trx`, `modern-fail.trx`, `tests/PellyCoder.Integration.Tests/Testing/TestRunnerSdkTests.cs`

**Interfaces:**
- Consumes: `IProcessRunner`, `BuildTool`, `BuildErrorParser`(M2a)
- Produces:
  * `sealed class TestOptions { string? Command; List<string> Projects; int TimeoutSeconds = 300; }`
    * `PellyOptions.Test` 속성으로 둔다.
    * 설정 키 `test:command`·`test:projects`·`test:timeoutSeconds`를 바인딩한다(FutureKeys에서 뺀다).
    * `test:timeoutSeconds`는 1 이상이어야 한다.
  * `static class TestProjectLocator { IReadOnlyList<string> Find(IFileSystem fs, string workspaceRoot, string targetCsproj, TestOptions options); }`
    * `options.Projects`가 있으면 그것을 쓴다. 작업 공간 기준 상대 경로이고, 없는 파일이면 `FileNotFoundException`.
    * 없으면 작업 공간 루트의 `*.sln`에 적힌 프로젝트(없으면 R6 탐색) 중에서 고른다. 조건: 이름에 `Test`(대소문자 무시)가 있고, `ProjectReference`가 대상 csproj의 전체 경로를 가리킬 것.
    * 경로 비교는 Windows면 대소문자 무시.
  * `sealed record TestCaseResult(string FullName, bool Passed, string? Message)`
  * `sealed record TestRun(IReadOnlyList<TestCaseResult> Results, IReadOnlyList<Diagnostic> BuildErrors, int ExitCode, bool TimedOut, bool PerTestResults, string? Error)`
  * `sealed class TestRunner(IProcessRunner processes, BuildOptions build, TestOptions test)`
    * 시그니처: `Task<TestRun> RunAsync(BuildTool tool, string testProject, string workDir, string trxPath, CancellationToken ct)`
    * SDK 실행 순서:
      1. `dotnet build <test> --no-restore --nologo -v q -clp:NoSummary;ErrorsOnly` — 오류가 있으면 `BuildErrors`를 채우고 테스트는 실행하지 않는다.
      2. `dotnet test <test> --no-build --nologo --logger "trx;LogFileName=<trxPath>"`
    * 레거시(「Windows 미확인」):
      1. MSBuild로 테스트 프로젝트를 빌드한다.
      2. `test.command`를 공백·따옴표 규칙으로 실행 파일과 인자로 나누고 `{project}`, `{dll}`, `{trx}`를 치환한다. `{dll}`은 `<프로젝트 폴더>/bin/Debug/<AssemblyName 또는 파일 이름>.dll`.
      3. `{trx}`가 없으면 `PerTestResults = false`(R7).
    * `test.command`가 없으면 `Error = "레거시 테스트 프로젝트는 test.command 설정이 필요합니다"`.
    * 환경 변수는 빌드와 같다(`DOTNET_CLI_UI_LANGUAGE=en`, `VSLANG=1033`).
    * 시간 제한은 `test.timeoutSeconds`다.
  * `static class TrxParser { IReadOnlyList<TestCaseResult> Parse(string trxXml); }`
    * `UnitTestResult`의 `testName`·`outcome`·`Output/ErrorInfo/Message`(첫 줄)를 읽는다.
    * `NotExecuted`는 제외한다.
- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] Locator_ModernApi_FindsApiTests()   [Fact] Locator_ModernJobs_FindsNone()   [Fact] Locator_LegacyData_FindsShopTests()
  [Fact] Locator_OptionsOverride_RelativeToWorkspace()   [Fact] Locator_NoSln_ScansWorkspace()
  [Fact] Trx_ParsesPassFailAndMessages()     // fixtures/tests/*.trx (Modern 코퍼스 실제 실행 녹화)
  [Fact] Runner_Sdk_CommandLinesAndTrxPath()     // ScriptedProcessRunner 인자 단언
  [Fact] Runner_TestBuildErrors_SkipTestRun()    [Fact] Runner_Timeout_Reported()
  [Fact] Runner_LegacyPlaceholders_Substituted()   [Fact] Runner_LegacyWithoutTrx_PerTestFalse()
  [Fact] Options_TestKeysBound_TimeoutValidated()
  [Fact] Sdk_RealRun_ModernApiTests()          // Integration [Fact]: 코퍼스 복사본 restore 후 실제 dotnet test, 결과 3건 이상 Passed
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Testing*"`, Integration `--filter-namespace "*Testing*"`
- [ ] **Step 5: Commit** — `git commit -m "feat(testing): locate test projects (D18) and run tests with TRX results"`

---

### Task 9: VERIFY의 테스트 단계·`--test`

**Files:**
- Create: `src/PellyCoder.Core/Verification/TestStep.cs`
- Modify: `src/PellyCoder.Core/Fsm/RunRequest.cs`, `Phases/LocatePhase.cs`, `Phases/ApplyPhase.cs`, `FsmOrchestrator.cs`, `FsmOrchestratorFactory.cs`, `src/PellyCoder.Core/Feedback/FeedbackBuilder.cs`, `src/PellyCoder.Cli/Commands/CsCommand.cs`
- Test: `tests/PellyCoder.Core.Tests/Fsm/TestFlowTests.cs`, `tests/PellyCoder.Core.Tests/Feedback/FeedbackBuilderTests.cs`

**Interfaces:**
- Consumes: `TestProjectLocator`, `TestRunner`, `TestRun`(Task 8), `IVerifierStep`, `StepResult`, `BaselineDiff`
- Produces:
  * `RunRequest` 맨 끝(`Verbose` 다음)에 `bool RunTests = false`를 더한다. CLI `pelly cs --test`.
  * `RunContext.TestProjects`(LOCATE가 설정)와 `RunContext.BaselineTests`(`Task<IReadOnlyList<TestRun>>?`)
    * `BaselineTests`는 Baseline 빌드가 끝난 뒤 같은 취소 토큰으로 이어서 실행한다.
    * APPLY는 첫 쓰기 전에 Baseline과 BaselineTests를 모두 기다린다.
    * 오케스트레이터의 종료 처리(취소·최대 10초 대기)는 둘 다 적용한다.
  * LOCATE 동작:
    * 테스트 프로젝트가 없으면 `테스트 프로젝트를 찾지 못해 테스트를 건너뜁니다 (빌드 결과로만 판정)` 알림.
    * Windows가 아닌 레거시는 `레거시 테스트는 Windows에서만 실행합니다 — 테스트를 건너뜁니다`.
    * 드라이런은 테스트를 시작하지 않는다.
  * `TestStep(TestRunner runner, FeedbackBuilder feedback, TestOptions options) : IVerifierStep`(`Name = "Test"`, 라벨 `TestErrors`)
    * BuildStep 뒤에 둔다. 테스트 프로젝트가 없으면 통과다.
    * 새 실패 = 지금 실패했고, Baseline에서 실패가 아니었던(통과 또는 없음) 테스트.
    * 진단: `Diagnostic("", 0, 0, "error", "TEST", FullName)` (R8)
  * 테스트 프로젝트 빌드 오류 처리:
    * Baseline 대비 새 CS 오류면 `ForBuild` 피드백으로 실패한다.
    * CS가 아닌 새 오류면 `Environment`.
  * 시간 초과면 `Environment("테스트가 {n}초 안에 끝나지 않았습니다 (test.timeoutSeconds)")`.
  * `PerTestResults == false`(R7) 판정:
    * 원본 종료 0인데 지금 0이 아니면 실패한다. 피드백은 `TESTS FAILED (exit {n})`.
    * 원본도 0이 아니면 통과로 보고, 알림 `테스트가 원래도 실패해 비교할 수 없습니다`를 한 번 띄운다.
  * `FeedbackBuilder.ForTests(IReadOnlyList<TestCaseResult> failures)`
    * 형식: `TESTS FAILED ({n} new failures)` 다음 줄마다 `<FullName>: <메시지 첫 줄>`
    * 예산 안으로 자른다(뒤 항목부터).
- [ ] **Step 1: 실패하는 테스트 작성** (ScriptedProcessRunner가 인자의 trx 경로에 fixture를 써 주는 가짜)
  ```csharp
  [Fact] NewFailure_RetryWithTestsFeedback_ThenPasses()   // 2번째 프롬프트에 "TESTS FAILED (1 new failure)" — 종료 0
  [Fact] PreexistingFailure_IsNotCounted()
  [Fact] TestProjectBuildBreaks_IsFeedback()               // 테스트 빌드 CS 오류 → BUILD FAILED 피드백, 크래시 아님
  [Fact] TestTimeout_Exit4_Restored()
  [Fact] NoTestProjects_WarnsAndPasses()
  [Fact] RunTestsOff_NoTestProcess()                       // 기존 흐름에서 테스트 프로세스 호출 0
  [Fact] ApplyWaitsForBaselineTests()                      // BaselineTests 완료 전 쓰기 없음
  [Fact] CancelDuringBaselineTests_Exit2_FileUntouched()
  [Fact] LegacyNoTrx_ExitCodeComparison()
  [Fact] ForTests_FitsBudget()
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Fsm*" --filter-namespace "*Feedback*"` 그리고 `dotnet test tests/PellyCoder.Cli.Tests`
- [ ] **Step 5: Commit** — `git commit -m "feat(verify): add --test step that fails only on newly failing tests"`

---

### Task 10: doctor 완성 — pelly 자체·설정·타겟팅 팩·서버 체크리스트

**Files:**
- Create: `deploy/server-checklist.md`
- Modify: `src/PellyCoder.Core/Doctor/DoctorService.cs`, `src/PellyCoder.Cli/Commands/DoctorCommand.cs`, `src/PellyCoder.Cli/PellyCoder.Cli.csproj`(체크리스트 EmbeddedResource 연결), `Composition.cs`
- Test: `tests/PellyCoder.Core.Tests/Doctor/DoctorServiceTests.cs`, `tests/PellyCoder.Cli.Tests/DoctorCommandTests.cs`, `tests/PellyCoder.Integration.Tests/Doctor/TargetingPackWindowsTests.cs`

**Interfaces:**
- Produces(행 이름은 그대로 쓴다):
  * `pelly` 행: `Ok`, `{PellyInfo.Version}, 토크나이저 {SHA-256 앞 12자}`. 해시는 내장 `vocab.json`+`merges.txt` 바이트를 이어 붙여 계산한다.
  * `설정` 행: `Ok`. 사용자 설정 경로(있음/없음), 작업 공간 `pelly.json` 경로(있음/없음)를 보인다.
    * 적용 값도 보인다: `llm.endpoint`, `llm.model`, `llm.thinkMode`, `budget.inputTotal`, `retry.max`, `build.timeoutSeconds`, `test.timeoutSeconds`, `sql.compatLevel`, `sql.connectionString`(가림).
    * 가림은 `SqlConnectionText.Mask`로 한다.
  * `타겟팅 팩` 행: `pelly doctor --project <csproj>`일 때만 둔다.
    * SDK 스타일이면 `Ok "SDK 스타일 — NuGet 참조 어셈블리 사용"`.
    * 레거시: `TargetFrameworkVersion` vX.Y에 대해, Windows면 `%ProgramFiles(x86)%\Reference Assemblies\Microsoft\Framework\.NETFramework\vX.Y` 폴더가 있으면 `Ok`, 없으면 `Fail`과 조치 `"Developer Pack을 설치하거나 Microsoft.NETFramework.ReferenceAssemblies.netXY 패키지를 쓰세요"`. Windows가 아니면 `Warn "Windows에서만 확인합니다"`.
  * `deploy/server-checklist.md`: PRD §7.1 표를 서버 관리자용으로 옮긴다(한국어).
    * 기존 `서버 설정 확인 필요` 행의 Detail을 `아래 체크리스트를 서버 관리자와 확인하세요`로 바꾼다.
    * `DoctorCommand`는 표 뒤에 체크리스트 원문을 `서버 관리자 확인 사항` 제목 아래 출력한다.
  * SQL 행의 예외 가림은 `SqlConnectionText.MaskInText`로 바꾼다(M2b 이월: 따옴표 안 `;`).
- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] Pelly_RowHasVersionAndTokenizerHash()   [Fact] Settings_RowMasksConnection()
  [Fact] TargetingPack_Sdk_Ok()  [Fact] TargetingPack_LegacyOnMac_Warn()  [Fact] NoProject_NoTargetingRow()
  [Fact] Command_PrintsChecklistAfterTable()     [Fact] Sql_QuotedPasswordWithSemicolon_Masked()
  [WindowsFact] TargetingPack_LegacyV48_Ok()     // windows-latest에 4.8 팩 있음
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-namespace "*Doctor*"` 그리고 `dotnet test tests/PellyCoder.Cli.Tests`
  * push 후 `windows-integration`에서 `[WindowsFact]`가 통과해야 한다.
- [ ] **Step 5: Commit** — `git commit -m "feat(doctor): add pelly, settings, targeting-pack rows and server checklist"`

---

### Task 11: `doctor --sanitize`

**Files:**
- Create: `src/PellyCoder.Core/Doctor/DoctorSanitizer.cs`
- Modify: `src/PellyCoder.Core/Doctor/DoctorService.cs`, `src/PellyCoder.Cli/Commands/DoctorCommand.cs`
- Test: `tests/PellyCoder.Core.Tests/Doctor/DoctorSanitizerTests.cs`, `tests/PellyCoder.Cli.Tests/DoctorCommandTests.cs`

**Interfaces:**
- Produces:
  * `DoctorCheck`에 맨 끝 `IReadOnlyDictionary<string, string>? Facts = null`을 더한다. 행마다 채우는 키(R9):
    * `Ollama 서버`: `ollamaVersion`, `latencyMs`
    * `모델`: `loaded`
    * `응답 시간`: `ttftMs`
    * `토크나이저 일치`: `match`
    * `.NET SDK`: `dotnetVersion`
    * `MSBuild (레거시)`: `found`
    * `pelly`: `pellyVersion`, `tokenizerHash`
    * `SQL 연결`: `canViewDefinition`, `serverMajor`
    * `타겟팅 팩`: `frameworkVersion`, `installed`
  * `static class DoctorSanitizer { string ToJson(IReadOnlyList<DoctorCheck>); string ToMarkdown(...); }`
    * 행 이름은 알려진 목록에 있어야 한다. 아니면 `기타`.
    * 남기는 것: `Status`, 위 키 목록의 `Facts` 중 값이 `^[0-9A-Za-z.:_+\-]{0,40}$`인 것.
    * 빼는 것: `Detail`·`Action`
  * `pelly doctor --sanitize [--out <폴더>]`: 화면 출력과 함께 `pelly-doctor-<yyyyMMdd-HHmmss>.json`/`.md`를 쓴다.
- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] DoctorSanitize_InjectedDetail_NeverAppears()   // Detail/Action/Facts 값에 경로·서버 이름·비밀번호 → 출력에 없음
  [Fact] UnknownRowName_IsOther()  [Fact] FactsOutsideWhitelist_Dropped()
  [Fact] Command_Sanitize_WritesTwoFiles()
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인** (Task 10과 같은 명령)
- [ ] **Step 5: Commit** — `git commit -m "feat(doctor): add sanitized doctor output (FR-08, D11)"`

---

### Task 12: `doctor --offline-selftest`

**Files:**
- Create: `src/PellyCoder.Core/Doctor/OfflineSelfTest.cs`
- Modify: `src/PellyCoder.Cli/Commands/DoctorCommand.cs`
- Test: `tests/PellyCoder.Core.Tests/Doctor/OfflineSelfTestTests.cs`, `tests/PellyCoder.Cli.Tests/DoctorCommandTests.cs`

**Interfaces:**
- Produces:
  * `static class OfflineSelfTest { IReadOnlyList<DoctorCheck> Run(); }` — 네트워크·설정 파일·`HttpClient`를 쓰지 않는다. 행은 다섯 개다.
    * `토크나이저`: 내장 토크나이저로 고정 문자열을 세어 0보다 큼.
    * `Roslyn`: C# 7.3·14 코드 조각 파싱 오류 0.
    * `ScriptDom`: `SqlBatchSplitter`로 SET/GO/CREATE PROCEDURE 조각이 배치 3개, 객체 1개.
    * `인코딩`: CP949로 "한글"을 인코딩한 뒤 디코딩한 왕복 결과가 같음.
    * `리소스`: `PromptResources.LoadDefault()`와 `QwenChatTemplate.LoadDefault()` 성공.
  * `DoctorCommand`에 숨은 옵션 `--offline-selftest`(`IsHidden = true`)를 단다. 다섯 행을 표로 보이고, 모두 Ok면 종료 0, 아니면 4. 설정을 읽지 않는다.
- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] SelfTest_AllOk()   [Fact] Command_OfflineSelfTest_Exit0_NoBackendCreated()   // DoctorRunner·HttpClientHolder 미호출 단언
  [Fact] Help_DoesNotListOfflineSelfTest()
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인** (Task 10과 같은 명령)
- [ ] **Step 5: Commit** — `git commit -m "feat(doctor): add hidden --offline-selftest"`

---

### Task 13: M2b 이월 — 컴파일 중 취소·별칭 이름 충돌

**Files:**
- Modify: `src/PellyCoder.Core/Sql/SqlServerExecutor.cs`, `src/PellyCoder.Core/Sql/SqlReferenceCollector.cs`, `docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md`(§8.10 알려진 한계 6번 갱신)
- Test: `tests/PellyCoder.Core.Tests/Sql/SqlReferenceCollectorTests.cs`, `tests/PellyCoder.Integration.Tests/Sql/SqlServerExecutorTests.cs`

**Interfaces:**
- Produces:
  * **M-1:** `SqlServerExecutor`의 공통 실행 경로에서, `ct`가 취소된 상태의 `SqlException`은 `OperationCanceledException(ct)`로 바꿔 던진다. 대상은 컴파일 배치와 메타데이터 조회 모두다. 결과: Ctrl+C면 종료 2.
  * **M-2:** 스키마 없는 `NamedTableReference`를 별칭이라는 이유로 빼는 것은, 같은 문장(가장 가까운 `TSqlStatement`) 안에서 **다른** 테이블 참조가 그 이름을 별칭으로 선언했을 때만이다. `FROM Orders AS Orders`처럼 자기 자신의 별칭은 빼지 않는다.
- [ ] **Step 1: 실패하는 테스트 작성**
  ```csharp
  [Fact] SelfAlias_IsStillObject()                    // SELECT * FROM Orders AS Orders → Objects ∋ Orders
  [Fact] UpdateAliasTarget_StillExcluded()            // 기존 동작 유지
  [Fact] AliasInOtherStatement_DoesNotHide()          // 문장 1의 별칭 o, 문장 2의 FROM o → 객체
  [SqlServerFact] CancelDuringCompile_ThrowsOperationCanceled()   // WAITFOR가 든 배치는 NOEXEC로 실행되지 않으므로, 메타데이터 조회에 대해 취소 토큰을 미리 취소해 확인
  ```
- [ ] **Step 2~4: 실패 확인 → 구현 → 통과 확인**
  * Run: `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*SqlReferenceCollectorTests"` 그리고 PELLY_TEST_SQL 설정 후 Integration `*Sql*`
- [ ] **Step 5: Commit** — `git commit -m "fix(sql): map cancelled SQL calls to cancellation; scope alias exclusion per statement"`

---

### Task 14: 실제 모델 E2E·벤치마크 회귀 확인

**Files:**
- Create: `tests/PellyCoder.Integration.Tests/E2E/M3aEndToEndTests.cs`, `docs/m3a-e2e.md`, `docs/m3a-benchmark.md`

**Interfaces:**
- Consumes: 앞 작업 전부, `CorruptingBackend`, `CannedLlmBackend`, Eval 실행기

- [ ] **Step 1: E2E 작성** (`[OllamaFact]`, Modern 코퍼스 임시 복사·restore)
  ```csharp
  [OllamaFact] Dto_InPrompt_AndEditSucceeds()          // cs-modern-002 요청, prompt.txt에 "[READ-ONLY: dto]"·"IsGiftItem", 종료 0
  [OllamaFact] Test_PassingEdit_RunsTests_Exit0()      // CalculateRefundAsync 요청(기존 테스트와 모순 없음), --test, 테스트 프로세스 실행 기록, 종료 0
  [OllamaFact] Test_ContradictingEdit_FailsWithTestErrors_Restored()   // CancelAsync에 배송 중 예외 요청(CurrentlyCancels 테스트와 모순), --test, 종료 1·FailureKind TestErrors·파일 원본
  [OllamaFact] Report_AndSanitize_OverRealRuns()       // 위 실행들의 작업 공간에서 pelly report·--sanitize, 비식별 파일에 "OrderService"·경로 없음
  ```
- [ ] **Step 2: 실행** — M2a Task 14와 같은 방법으로 3회 실행하고 같은 실패 규칙을 따른다.
  * 프롬프트 리소스 문제면 리소스를 고치고 버전을 올린다.
  * Core 결함이면 BLOCKED로 보고한다.
  * 기대값을 완화하지 않는다.
- [ ] **Step 3: 벤치마크 회귀 (PRD §8.2)** — 설정은 M2b Task 14와 같다(`--config`, `PELLY_SQL_CONNECTION`). 30건을 1회 실행하고 `docs/m3a-benchmark.md`에 적는다.
  * KPI 표
  * M2b 기준선(28/30, 판정 강화 뒤 재판정 29/30)과의 작업별 차이
  * 대기 시간 열
  * 검증 불변 조건 위반이 1건이라도 있으면 BLOCKED로 보고한다.
  * 성공률이 M2b 기준선보다 낮으면 DONE_WITH_CONCERNS로 작업별 원인 분석을 붙인다. 병합 여부는 사람이 정한다(PRD §8.2).
- [ ] **Step 4: 기록** — `docs/m3a-e2e.md`에 적는다.
  * 테스트별 3회 결과
  * `--verbose` 출력 1건, `--pause-on-exit` 수동 확인 1건(터미널에서 키 입력으로 끝남)
  * `pelly report` 표와 비식별 Markdown 1건
- [ ] **Step 5: 전체 검증·Commit**
  * `dotnet build -warnaserror && dotnet test` 실패 0, push 후 CI 4잡 녹색
  * `git commit -m "test(e2e): DTO slice, --test, report and sanitize against local model; benchmark regression"`

---

### Task 15: 문서 갱신

**Files:**
- Modify: `docs/superpowers/specs/2026-10-17-pelly-coder-v1-design.md`, `docs/PRD.md`, `README.md`, `eval/benchmark/README.md`(필요하면)

- [ ] **Step 1: 설계 문서 반영** — 각 변경 옆에 근거(작업 번호·R 번호)를 적는다.
  * §8.2: 대기 콜백·Queue 계산(R3)
  * §8.5: DTO 렌더 형식·enum·필드·상한(R5)
  * §8.9 4번: `TestStep`, R6, R7, `test.timeoutSeconds`(R11), 피드백 형식
  * §8.12: `FormatErrors`(R2), `QueueSeconds`, `FailureKinds` 카탈로그, `pelly report` 표, 비식별 허용 목록(`TEST` 포함, R8)
  * §8.13: 새 행, `--sanitize`(R9), `--offline-selftest`
  * §8.14: `--pause-on-exit`(R10)
  * §11 M3 행: M3a/M3b 분리(R1)
- [ ] **Step 2: PRD 반영**
  * FR-00: 옵션 상태
  * FR-08·FR-09: 구현 내용
  * §5: 대기 시간
  * 변경 이력 v1.9
- [ ] **Step 3: README** — `pelly report`, `--sanitize`, `--test`, `--verbose`, `--pause-on-exit`, `pelly doctor --project`·`--sanitize`의 사용 예
- [ ] **Step 4: 검증** — 문서만 바뀌었는지 `git diff --stat`으로 확인한다.
- [ ] **Step 5: Commit** — `git commit -m "docs: reflect M3a report, doctor, DTO slice and --test"`

---

## M3a 완료 기준

* `pelly report`와 `--sanitize`가 실제 실행 기록으로 동작한다. 주입한 소스 문자열이 비식별 출력에 나오지 않는다(Review Focus 1).
* `pelly doctor`가 FR-08 항목(서버·모델·빌드 도구·타겟팅 팩·SQL·pelly 자체·설정)을 보이고, `--sanitize`와 숨은 `--offline-selftest`가 동작한다.
* DTO 슬라이스와 `--test`가 실제 모델 E2E로 동작한다(새로 실패한 테스트만 실패).
* 벤치마크 30건 회귀 측정 기록이 있다. 검증 불변 조건 위반은 0건이다.
* CI 4잡 녹색, `dotnet test` 실패 0.

## M3b로 넘기는 것 (다음 계획)

* 벤치마크 50건(C# 30 + SQL 20)과 `judge.tests`를 쓰는 테스트 연동 판정, 그 결과로 하는 프롬프트·샘플링 튜닝(회귀 차단 규칙 PRD §8.2)
* S6: `win-x64` self-contained 단일 파일 publish 확인과 CI `publish-check` 잡(`pelly doctor --offline-selftest`)
* 반입 패키지:
  * SBOM(CycloneDX), 서드파티 라이선스 목록, SHA-256 체크섬
  * 버전 기록(pelly·Ollama·모델 GGUF 해시·벤치마크 요약), `pelly.json` 템플릿
  * zip 생성 스크립트
* `deploy/` 문서: 설치·사용 가이드, IDE External Tools 가이드(§8.14), "실행 중 외부 통신 없음" 명세
* S7: Rider External Tools 실기 확인. 사용자와 함께 하며, Visual Studio는 M4에서 확인.
* `eval/corpus/Sql/deploy.sh` 재실행 실패 수정
