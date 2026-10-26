# V2-2c 테스트 우선 검증 — 벤치마크와 남은 가드 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 다음을 만든다.
- 테스트 우선 검증의 효과를 재는 벤치마크
  - `pelly-eval run --test-first`
  - 테스트가 연결된 apply 사례 8건
  - 요약 지표와 `compare --test-first`
- V2-2b에서 미룬 가드 두 가지: 작업을 건너는 테스트 변경 검사, 그룹 테스트 변경 검사
- 한국어 스택 형식 읽기
- 로컬 모델로 한 예비 측정

**Architecture:**
- **제품 쪽(`PellyCoder.Core`):** 테스트 메서드 선언 해시를 공유 함수(`TestDeclarationHash`)로 둔다. 쓰는 곳은 셋이다.
  - FSM: 기대 해시가 있으면 기존 파일 해시 검사에 더해 비교한다.
  - SpecRunner: 수용 기준의 첫 연결 작업 직전에 해시를 잡는다.
  - GroupTester: 기준 테스트와 판정 사이에 비교한다.
- **평가 쪽(`eval/PellyCoder.Eval`):** 기존 `run`·`apply` 흐름에 선택 모드만 더한다.
  - 옵션이 없으면 작업 목록·결과 형식·수치가 그대로다.
  - 판정 프로젝트는 테스트 우선 모드에서만 작업 공간 안으로 복사한다.

**Tech Stack:** .NET 10, Roslyn, System.IO.Abstractions, xunit v3, 로컬 Ollama(qwen3.6:35b, 예비 측정용).

**Spec:** `docs/superpowers/specs/2026-10-26-v2-2c-test-first-benchmark-design.md`. 상위 설계는 `docs/superpowers/specs/2026-10-26-v2-2-test-first-design.md`다. 재생 실험(§8)은 설계에서 뺐으므로 이 계획에 없다.

## Global Constraints

- **보존:**
  - `pelly-eval run`(옵션 없음)과 `pelly-eval apply`(기본 = 기존 12건)는 작업 목록·`results.json`·`report.md`·`summary.json`·`apply-summary.md`의 기존 필드와 수치가 그대로다. 새 필드는 값이 있을 때만 쓴다.
  - `eval/corpus`, `eval/benchmark/tasks/*.yaml`, `eval/benchmark/apply/*.md`, `judges/`는 고치지 않는다.
- **제품 보존:**
  - 기대 해시가 없는 실행(V2-2a `pelly cs --verify-test`, `--resume`으로 첫 연결 작업이 끝난 apply)은 동작이 그대로다.
  - 테스트 링크가 없는 그룹은 그대로다.
- **게이트:**
  - 워크트리 `/Users/payletter/git-root/Pelly-Code-v2-2c`에서 `dotnet build -warnaserror` 종료 0, `dotnet test` 실패 0이다. 건너뜀은 SQL 서버·로컬 모델·Windows 전용만 허용한다.
  - 메인 체크아웃 `/Users/payletter/git-root/Pelly-Code`에서는 빌드·테스트하지 않는다.
- **새 실패 유형:** `GroupTestFileChanged`(종료 4). `FailureKinds.All`과 `GroupFailureKinds`(`IsGroupKind`)에 등록한다.
- **문구:**
  - 사용자 문구는 한국어, 코드 주석은 영어다.
  - 그룹 문구는 `그룹 {id}: 지정한 테스트가 실행 중에 바뀌었습니다: {파일}`이다.
  - 교차 작업 변경은 V2-2a 문구(`지정한 테스트 파일이 실행 중에 바뀌었습니다: {상대 경로}`)와 실패 유형 `TestFileChanged`를 그대로 쓴다.
- **판정 프로젝트 복사 위치(테스트 우선 모드):** `work/<id>/<root 이름>/JudgeTests/<프로젝트 폴더 이름>/`.
- **apply 사례 폴더:**
  - 위치는 `eval/benchmark/apply-test-first/<id>/{spec.md, files/, expected.json}`, 승인 파일은 `eval/benchmark/apply-test-first/APPROVED.json`이다.
  - 사례 id는 `t1`~`t8`이고, 결과 표와 `apply.json`에서 기존 사례와 겹치지 않는다.
- **승인 전 표시:** 요약 제목에 `예비(사람 승인 전)`를 붙인다.
- **커밋:** 커밋 꼬리는 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`다. 경로 단언에 `/`를 박지 않는다.

## Review Focus

1. **`--test-first`로 작업을 돌린 뒤 같은 출력 폴더의 독립 검증이 작업 공간 안 `JudgeTests/` 복사본(pelly가 만진 것)을 판정 프로젝트로 고르는 경우:** 늘 `work/<id>/judges/` 쪽을 써야 한다(Task 4 `TestFirst_IndependentVerifierIgnoresWorkspaceJudgeCopy`).
2. **`Shop.Data.JudgeTests`처럼 코퍼스 소스를 `Compile Include`로 컴파일하는 판정 프로젝트:** 작업 공간 안 복사본이 수정된 대상 파일을 컴파일해야 GREEN이 의미 있다(Task 4 `TestFirst_DataJudgeCompilesEditedSource`).
3. **수용 기준 테스트 파일의 다른 멤버(도우미)를 다른 작업이 고친 경우:** 교차 작업 가드가 오탐하면 안 된다(Task 2 `CrossTask_HelperInTestFileChanged_NoFalseAlarm`).
4. **`--set test-first`의 `files/`가 기존 파일을 덮어쓰는 사례(예: 기존 테스트 파일에 메서드 추가):** 「바뀐 `.cs`」 비교에서 원본으로 잡히지 않아야 한다(Task 6 `Apply_FilesOverlayIsBaselineForChangedCs`).
5. **`compare --test-first`에 서로 다른 모델 digest·Ollama 버전의 요약을 줄 때:** 기존 `compare`처럼 판정 불가(종료 4)다(Task 5 `CompareTestFirst_DifferentDigest_NotJudged`).

---

## File Structure

- `src/PellyCoder.Core/Testing/TestDeclarationHash.cs` (새): 테스트 메서드 선언 텍스트 해시
- `src/PellyCoder.Core/Fsm/RunRequest.cs`, `Verification/TestStep.cs`, `Logging/RunLogger.cs` (수정): 기대 해시, `FirstAttemptGreen`
- `src/PellyCoder.Core/Specs/SpecRunner.cs`, `Specs/GroupTester.cs`, `Specs/FsmTaskExecutor.cs`, `Specs/ITaskExecutor.cs`, `Specs/ApplySummary.cs`, `Reporting/FailureKinds.cs` (수정): 교차 작업·그룹 가드
- `src/PellyCoder.Core/Feedback/FeedbackBuilder.cs` (수정): 한국어 스택
- `eval/PellyCoder.Eval/` (수정): `EvalArguments.cs`, `BenchmarkRunner.cs`, `WorkCopy.cs`, `IndependentVerifier.cs`, `Models.cs`, `KpiReport.cs`, `RepeatSummary.cs`, `Acceptance.cs`(`CompareCommand`), `ApplyBenchmark.cs`, `ApplyBenchmarkReport.cs`, `Program.cs`
- `eval/PellyCoder.Eval/TestFirstMetrics.cs` (새): §4.3 지표 계산
- `eval/benchmark/apply-test-first/` (새): 사례 8건, `README.md`, `APPROVED.json`(사람이 채움; 계획에서는 만들지 않음)
- 문서: `eval/benchmark/README.md`, `README.md`(알려진 한계), `docs/PRD.md` §12.4, `docs/v2-2c-test-first-benchmark.md`(새, 예비 측정)

---

### Task 1: 테스트 선언 해시, FSM 기대 해시, 첫 시도 GREEN 기록

**Files:**
- Create: `src/PellyCoder.Core/Testing/TestDeclarationHash.cs`
- Modify: `src/PellyCoder.Core/Fsm/RunRequest.cs`, `Fsm/Phases/LocatePhase.cs`(필요하면), `Verification/TestStep.cs`, `Logging/RunLogger.cs`
- Test: `tests/PellyCoder.Core.Tests/Testing/TestDeclarationHashTests.cs`, `tests/PellyCoder.Core.Tests/Fsm/TestFirstFlowTests.cs`

**Interfaces:**
- Produces:
  - `static class TestDeclarationHash`
    - `string? Of(IFileSystem fs, string workspaceRoot, string testName)`
      - `TestMethodIndex.Find`로 찾은 선언(들)의 메서드 선언 텍스트를 이어 SHA-256 hex를 만든다. 정렬은 `FullName` 순이다.
      - 찾지 못하면 null이다.
      - V2-2a `TestFirstState.HashOf`의 대상 파일 분기와 같은 텍스트 규칙을 쓴다. 가능하면 그 분기가 이 함수를 쓰도록 바꾼다(DRY, 동작 같음).
  - `RunRequest`에 마지막 선택 인자 `IReadOnlyDictionary<string, string>? ExpectedTestHashes = null`. 키는 테스트 이름(요청의 `VerifyTests` 철자), 값은 `TestDeclarationHash.Of` 결과다.
  - `TestFirstSummary`에 마지막 선택 인자 `GreenState FirstAttemptGreen = GreenState.NotChecked`

동작:
- **TestStep 기대 해시:** GREEN·회귀가 모두 통과해 기존 파일 해시 비교까지 지난 뒤에 확인한다. `ExpectedTestHashes`의 이름마다 `TestDeclarationHash.Of`를 다시 재서 다르면 V2-2a와 같은 `Environment("지정한 테스트 파일이 실행 중에 바뀌었습니다: {선언 파일 상대 경로}", "TestFileChanged")`다. 다시 잰 값이 null(선언이 사라짐)이어도 같다.
- **`FirstAttemptGreen`:** `ctx.Attempt == 1`인 VERIFY의 테스트 단계에서 GREEN 판정이 나면 그 값이다. 첫 시도가 테스트 단계에 이르지 못했으면 `NotChecked`다. Green 역할 이름이 없으면 `NotChecked`다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `Of_SameDeclaration_SameHash`, `Of_BodyChanged_DifferentHash`, `Of_OtherMemberInFileChanged_SameHash`, `Of_NotFound_Null`
  - `VerifyTest_ExpectedHashMismatch_Exit4`: 기대 해시를 다른 값으로 준 실행 → GREEN 통과 뒤 `TestFileChanged`, 롤백
  - `VerifyTest_ExpectedHashMatch_Passes`
  - `Summary_FirstAttemptGreen_FailedThenPassed`: 첫 시도 GREEN 실패, 둘째 통과 → `firstAttemptGreen == "Failed"`, `green == "Passed"`
  - `Summary_FirstAttemptBuildFails_NotChecked`
  - V2-2a·V2-2b 기존 흐름 테스트는 고치지 않고 통과
- [ ] **Step 2:** `--filter "FullyQualifiedName~TestDeclarationHashTests|FullyQualifiedName~TestFirstFlowTests"` → FAIL
- [ ] **Step 3:** 구현한다.
- [ ] **Step 4:** PASS, `dotnet test tests/PellyCoder.Core.Tests` 전체 PASS
- [ ] **Step 5: 커밋** `feat(testing): test declaration hash, expected-hash check, first-attempt GREEN`

### Task 2: 작업을 건너는 가드와 그룹 가드

**Files:**
- Modify: `src/PellyCoder.Core/Specs/SpecRunner.cs`, `Specs/ITaskExecutor.cs`, `Specs/FsmTaskExecutor.cs`, `Specs/GroupTester.cs`, `Specs/ApplySummary.cs`, `Reporting/FailureKinds.cs`, `README.md`(알려진 한계)
- Test: `tests/PellyCoder.Core.Tests/Specs/SpecRunnerTestFirstTests.cs`, `SpecRunnerGroupTestFirstTests.cs`, `GroupTesterTests.cs`

**Interfaces:**
- Consumes: Task 1 `TestDeclarationHash.Of`, `RunRequest.ExpectedTestHashes`
- Produces:
  - `TaskTests`에 선택 속성 `IReadOnlyDictionary<string, string>? ExpectedHashes { get; init; }`. `FsmTaskExecutor`가 `RunRequest.ExpectedTestHashes`로 옮긴다.
  - `GroupTestPlan`에 선택 속성 `IReadOnlyDictionary<string, string>? ExpectedHashes { get; init; }`
  - `GroupFailureKinds.TestFileChanged = "GroupTestFileChanged"`

동작:
- **SpecRunner(그룹 밖):**
  - 각 AC의 첫 연결 작업이 이 apply에서 돌기 직전에 `TestDeclarationHash.Of(fs, ws.Root, ac.Test)`를 잰다.
  - AC의 마지막 연결 작업이 첫 연결 작업과 다르면, 마지막 연결 작업의 `TaskTests.ExpectedHashes`에 그 값을 넣는다(그 AC의 테스트만).
  - 첫 연결 작업이 이미 `[x]`(이어서 하기)면 넣지 않는다.
- **그룹:**
  - `GroupTester.BaselineAsync`가 계획 테스트마다 `TestDeclarationHash.Of`를 기록한다.
  - `VerifyAsync`가 GREEN·회귀를 통과한 직후 다시 잰다. 이때 `plan.ExpectedHashes`가 있으면(그룹 밖 첫 연결 작업에서 잰 값) 그 값과 비교하고, 없으면 기준 값과 비교한다.
  - 다르면 판정에 표시하고, SpecRunner가 `UndoGroupAsync(…, GroupFailureKinds.TestFileChanged, 4)`와 함께 `그룹 {id}: 지정한 테스트가 실행 중에 바뀌었습니다: {파일}`을 알린다.
- **README 알려진 한계:** V2-2b의 「해시는 실행마다, 그룹은 없음」 줄을 고친다.
  - 메서드 선언 단위로 작업을 건너 보고, 그룹도 본다.
  - 같은 파일의 다른 멤버 변경은 보지 않는다.
  - `--resume`에서 첫 연결 작업이 끝났으면 그 실행 안에서만 본다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `CrossTask_TestEditedBetweenTasks_LastTaskGetsExpectedHash`: 가짜 실행기에서 T1 실행 콜백이 테스트 메서드 본문을 바꾼다. T3 요청의 `ExpectedTestHashes`가 T1 직전 해시다(실제 FSM 비교는 Task 1이 고정).
  - `CrossTask_HelperInTestFileChanged_NoFalseAlarm`: 같은 파일의 도우미만 바꿈 → 기대 해시가 다시 잰 값과 같다.
  - `CrossTask_Resume_NoExpectedHash`, `CrossTask_SingleLinkedTask_NoExpectedHash`
  - `Group_TestEditedDuringGroup_UndoneExit4`(`GroupTestFileChanged`, 파일 원래대로)
  - `Group_LastLinkedInGroup_UsesExpectedHashFromOutside`
  - `Group_NoTestChange_Unchanged`
- [ ] **Step 2:** FAIL 확인
- [ ] **Step 3:** 구현한다.
- [ ] **Step 4:** PASS, `tests/PellyCoder.Core.Tests` 전체 PASS
- [ ] **Step 5: 커밋** `feat(apply): cross-task and group test-change guards`

### Task 3: 한국어 스택 형식

**Files:**
- Modify: `src/PellyCoder.Core/Feedback/FeedbackBuilder.cs`(`StackFrame` 정규식 ~line 283), `README.md`(알려진 한계의 「영어 스택만」)
- Test: `tests/PellyCoder.Core.Tests/Feedback/FeedbackBuilderTests.cs`

**Interfaces:** 없음(내부 정규식).

- 영어 `^\s*at (?<m>.+?) in (?<f>.+?):line (?<l>\d+)\s*$`에 더해 `^\s*위치:\s*(?<m>.+?)\s+파일\s+(?<f>.+?):줄\s*(?<l>\d+)\s*$`를 받는다.
- 출력 형식(`  at Type.Method (target:line)`)은 같다.
- 코드 주석에 「Windows 미확인(픽스처 기준)」을 적는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `ForGreen_KoreanNetFrameworkFrames`: `   위치: Shop.Services.OrderService.Cancel(Int32 id) 파일 C:\work\Shop\Services\OrderService.cs:줄 42` → `  at OrderService.Cancel (Services/OrderService.cs:42)`(대상 상대 경로 `Services/OrderService.cs`)
  - `ForGreen_KoreanAsyncFrame`(`<CancelAsync>d__3.MoveNext()` 형태)
  - 영어 기존 테스트는 그대로 통과
- [ ] **Step 2:** FAIL → **Step 3:** 구현 → **Step 4:** PASS
- [ ] **Step 5: 커밋** `feat(feedback): read Korean .NET Framework stack frames`

### Task 4: `pelly-eval run --test-first`

**Files:**
- Modify: `eval/PellyCoder.Eval/EvalArguments.cs`, `BenchmarkRunner.cs`, `WorkCopy.cs`, `IndependentVerifier.cs`, `Models.cs`, `Program.cs`, `RepeatSummary.cs`(`RunConditions`)
- Test: `tests/PellyCoder.Eval.Tests/BenchmarkRunnerTests.cs`, `EvalArgumentsTests.cs`, `WorkCopyTests.cs`, `IndependentVerifierJudgeTests.cs`

**Interfaces:**
- Consumes: Task 1 `TestFirstSummary.FirstAttemptGreen`
- Produces:
  - `EvalArguments.TestFirst`(bool, 플래그 `--test-first`, `run`에서만)
  - `TaskResult`에 마지막 선택 인자 `TestFirstResult? TestFirst = null`
  - `sealed record TestFirstResult(string Red, string Green, string FirstAttemptGreen, int Regressions, int BreaksExcluded)`. 값은 `summary.json`의 문자열 그대로다.
  - `RunConditions`에 마지막 선택 인자 `bool? TestFirst = null`(`--test-first`면 true)
  - `static string WorkCopy.CopyJudgesIntoWorkspace(BenchmarkSuite suite, SuiteRoot root, string workRoot)`: 복사한 프로젝트 폴더 목록을 돌려준다.

동작:
- `--test-first`이면 `judge.tests`가 없는 작업은 `Skipped(…, "테스트 우선 대상 아님")`이다.
- 대상 작업의 실행 순서:
  1. 작업 복사본을 준비한다.
  2. `CopyJudgesIntoWorkspace`로 판정 프로젝트를 `<root>/JudgeTests/`에 복사한다. 이때 `JudgeTargetRoot`가 `work/<id>/<root>`를 가리키도록 고정한다(방법 자유, `Shop.Api.JudgeTests`의 `ProjectReference`와 `Shop.Data.JudgeTests`의 `Compile Include` 둘 다 맞아야 함).
  3. 기존 솔루션 복원 뒤 판정 프로젝트도 복원한다(`IndependentVerifier`가 쓰는 복원 명령과 같은 방식). 실패하면 `EnvironmentSkipped(…, "복원 실패", …)`다.
  4. 요청에 `VerifyTests = judge.tests`, `ExpectBreaks = judge.failingTests`(빈 목록이면 null)를 싣는다.
- 실행 뒤 `RunArtifacts`에서 `summary.json`의 `testFirst`를 읽어 `TaskResult.TestFirst`에 둔다.
- `IndependentVerifier`가 코퍼스 프로젝트를 모을 때 `<root>/JudgeTests/` 아래는 늘 뺀다. 옵션 없는 실행에는 그 폴더가 없으므로 동작이 같다.
- `results.json`은 `TestFirst`가 null이면 그 키를 쓰지 않는다(보존).

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `Args_TestFirst_RunOnly`(`apply --test-first`는 오류 종료 4)
  - `TestFirst_SkipsTasksWithoutJudgeTests`
  - `CopyJudgesIntoWorkspace_FixesJudgeTargetRoot`: 복사본 csproj(또는 props)를 MSBuild 평가 없이 읽었을 때 경로가 작업 복사본 루트를 가리킨다.
  - `TestFirst_RequestCarriesVerifyTestsAndBreaks`(실행기에 넘긴 `RunRequest` 확인. 기존 테스트의 실행기 주입 방식을 따른다)
  - `TestFirst_IndependentVerifierIgnoresWorkspaceJudgeCopy`
  - `TestFirst_DataJudgeCompilesEditedSource`(통합: 실제 dotnet, `cs-modern-003` 같은 Data 작업 하나. 캔드 응답 또는 가짜 백엔드로 정답 수정 → `TestFirst.Green == "Passed"`. 기존 Eval 통합 테스트가 실제 dotnet을 쓰는 방식을 따른다)
  - `Run_WithoutTestFirst_ResultsJsonUnchanged`: 기존 골든 또는 키 목록 비교
- [ ] **Step 2:** `dotnet test tests/PellyCoder.Eval.Tests --filter …` → FAIL
- [ ] **Step 3:** 구현한다.
- [ ] **Step 4:** PASS, `tests/PellyCoder.Eval.Tests` 전체 PASS
- [ ] **Step 5: 커밋** `feat(eval): run --test-first with judge tests inside the workspace`

### Task 5: 테스트 우선 지표와 `compare --test-first`

**Files:**
- Create: `eval/PellyCoder.Eval/TestFirstMetrics.cs`
- Modify: `KpiReport.cs`, `RepeatSummary.cs`, `Acceptance.cs`(`CompareCommand`), `EvalArguments.cs`
- Test: `tests/PellyCoder.Eval.Tests/TestFirstMetricsTests.cs`, `KpiReportTests.cs`, `RepeatSummaryTests.cs`, `AcceptanceTests.cs`

**Interfaces:**
- Consumes: Task 4 `TaskResult.TestFirst`, `RunConditions.TestFirst`
- Produces:
  - `sealed record TestFirstMetricsData(int Ran, int Met, int Judged, int RedWarned, int RedChecked, int BlockedWrongPatches, int FixedByFeedback, int BreaksExcluded)`
  - `static TestFirstMetricsData TestFirstMetrics.Build(IReadOnlyList<TaskResult> results)`
  - `RepeatSummaryData`에 마지막 선택 인자 `TestFirstMetricsData? TestFirst = null`(회차 합)

규칙(설계 §4.3, Claude 결정 그대로):
- `Ran`은 `TestFirst`가 있는 행 수다.
- `RedChecked`는 `Red ∈ {Red, AlreadyPassed}`, `RedWarned`는 `Red == AlreadyPassed`다.
- `Judged`는 `Red == Red` 그리고 실행 종료 코드가 2가 아닌 행이다. `Met`은 `Judged` 중 종료 0이고 `Green == Passed`이고 `Regressions == 0`인 행이다(`pelly report` 규칙과 같음).
- `BlockedWrongPatches`는 `FirstAttemptGreen == Failed`인 행이다. `FixedByFeedback`은 그중 `Status == Passed`(최종 종료 0이고 독립 판정 통과)인 행이다.
- `report.md`·`summary.md`에 `TestFirst`가 있으면 다음 줄을 더한다.
  - 표 줄
    - `| 수용 기준 충족률 | … |`
    - `| RED 확인 경고율 | … |`
    - `| 막은 잘못된 패치 | {BlockedWrongPatches}건 |`
    - `| 피드백으로 고친 비율 | {FixedByFeedback}/{BlockedWrongPatches} |`
  - 안내 문장: `판정 테스트가 GREEN 판정과 독립 판정에 함께 쓰입니다. 차이에는 GREEN 피드백과 회귀 확인 효과가 섞여 있습니다.`
- `compare --test-first <v1 summary.json> <테스트 우선 summary.json>`
  - 판정이 아니라 보고다. 종료 0은 보고함, 4는 판정 불가다.
  - 판정 불가 조건은 기존 `compare`와 같다(모델 digest·Ollama 버전 다름, 형식 버전 오류). 그에 더해 후보의 `RunConditions.TestFirst != true`이거나 기준이 `true`이면 판정 불가다.
  - 출력 항목:
    - 두 요약의 평균 성공 수(기준선은 테스트 우선 대상 작업만 센다)
    - 차이
    - 시간 중위값 비율
    - 검증 불변 조건 위반 수
    - 후보의 `TestFirst` 지표

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `Metrics_Counts`: 행 다섯 개(Red/Passed/첫Failed/Passed 상태, Red/Failed/첫Failed/Failed 상태, AlreadyPassed/Passed, Red/NotChecked 종료 1, `TestFirst` 없음) → `Ran 4, Met 1, Judged 3, RedWarned 1, RedChecked 4, BlockedWrongPatches 2, FixedByFeedback 1`
  - `Report_TestFirstRows_OnlyWhenPresent`
  - `Summary_TestFirstAggregated_AcrossRuns`, `Summary_OldJsonWithoutTestFirst_Loads`
  - `CompareTestFirst_Reports`, `CompareTestFirst_CandidateNotTestFirst_NotJudged`, `CompareTestFirst_DifferentDigest_NotJudged`
  - `CompareTestFirst_BaselineCountsOnlyTestFirstTasks`: 기준선에 Legacy·SQL 행이 있어도 평균은 `judge.tests` 작업 id만
- [ ] **Step 2:** FAIL → **Step 3:** 구현 → **Step 4:** PASS, `tests/PellyCoder.Eval.Tests` 전체 PASS
- [ ] **Step 5: 커밋** `feat(eval): test-first metrics, report rows and compare --test-first`

### Task 6: apply 사례 폴더 형식과 독립 테스트 재실행

**Files:**
- Modify: `eval/PellyCoder.Eval/ApplyBenchmark.cs`, `ApplyBenchmarkReport.cs`, `EvalArguments.cs`, `Program.cs`
- Test: `tests/PellyCoder.Eval.Tests/ApplyBenchmarkTests.cs`

**Interfaces:**
- Produces:
  - `EvalArguments.Set`(`--set <default|test-first|all>`, `apply`에서만, 기본 `default`)
  - `ApplyCase`에 마지막 선택 인자 `string? FilesDir = null`, `IReadOnlyList<ExpectedCriterion>? Expected = null`, `bool Approved = true`
  - `sealed record ExpectedCriterion(string AcId, string Red, string Outcome)`
  - `ApplyCaseResult`에 마지막 선택 인자 `IReadOnlyList<AcResult>? Criteria = null`, `int TestJudgeMismatches = 0`
  - `static IReadOnlyList<ApplyCase> ApplyBenchmark.LoadTestFirstCases(string dir, string? glob, IReadOnlySet<string> approved)`

동작:
- **로더:** `default`는 지금 로더(`apply/*.md`)만, `test-first`는 `apply-test-first/<id>/`만, `all`은 둘 다를 쓴다.
  - 폴더 사례의 id는 폴더 이름이다.
  - `spec.md`는 기존 사례와 같은 머리말 규칙(`id`가 폴더 이름과 같아야 함, `scope`)을 따른다.
  - `expected.json`은 `[{ "acId", "red", "outcome" }]`다.
  - `APPROVED.json`이 없거나 id가 없으면 `Approved = false`다.
- **작업 복사본:** 기존 준비(코퍼스 복사, `.git`, 명세 쓰기) 뒤에 `files/`를 `corpus/` 위에 복사한다(같은 상대 경로, 덮어쓰기).
- **바뀐 `.cs` 판정:** 비교 기준은 「원본 코퍼스, 그 위에 `files/`」다. `files/`의 파일 자체는 그 내용이 기준이다.
- **독립 테스트 재실행:** apply 종료 0이고 `apply.json`의 `criteria`가 있으면, `Outcome == Met`인 AC마다 그 `test:`를 pelly 밖에서 확인한다.
  - 선언 프로젝트를 `TestRunner`로 다시 빌드·실행한다. 선언 위치는 `TestMethodIndex`로 찾는다.
  - 결과에서 `TestNames.Matches`로 맞는 모든 결과가 통과하는지 본다.
  - 통과하지 않으면 `TestJudgeMismatches`를 하나 올린다.
  - 레거시는 Windows에서만 하고, 아니면 건너뛰고 표시한다.
- **`apply-summary.md`:**
  - 승인 안 된 사례가 하나라도 있으면 제목 끝에 ` — 예비(사람 승인 전)`를 붙인다.
  - `pelly report`와 같은 계산(`ApplyReport.Build`의 `Criteria`)으로 다음 줄을 낸다.
    - 수용 기준 충족률·RED 확인 경고율·미검증·`breaks:`로 뺀 수
    - `| 테스트 판정 불일치 | {합}건 |`
  - 사례별 AC 표: 사례, AC, 기대 RED/결과, 실제 RED/결과
- **종료 코드:** `TestJudgeMismatches > 0`이면 1이다(검증 불변 조건 위반과 같은 자리, 기존 우선순위 2 → 1 → 4).

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `Set_Default_OnlyExistingCases`, `Set_TestFirst_OnlyFolders`, `Set_All_Both`
  - `LoadTestFirstCases_IdMustMatchFolder`, `LoadTestFirstCases_ApprovedFlag`
  - `Apply_FilesOverlayCopied`, `Apply_FilesOverlayIsBaselineForChangedCs`
  - `Apply_MetCriterion_IndependentTestFails_Mismatch`(가짜 실행기)
  - `Summary_PreliminaryTitle_WhenUnapproved`, `Summary_CriteriaRowsAndPerCaseTable`
  - `ApplyDefault_SummaryUnchanged`(기존 12건 형식 그대로)
- [ ] **Step 2:** FAIL → **Step 3:** 구현 → **Step 4:** PASS, `tests/PellyCoder.Eval.Tests` 전체 PASS
- [ ] **Step 5: 커밋** `feat(eval): test-first apply case folders, independent test re-run`

### Task 7: 사례 8건과 정의 검사

**Files:**
- Create: `eval/benchmark/apply-test-first/{t1..t8}/spec.md`, `files/…`, `expected.json`, `eval/benchmark/apply-test-first/README.md`
- Modify: `eval/benchmark/README.md`(「apply 벤치마크」 절에 `--set`, 테스트 우선 사례, 승인 규칙)
- Test: `tests/PellyCoder.Eval.Tests/ApplyTestFirstCasesTests.cs`(새), 통합 테스트(실제 dotnet)는 기존 Eval 통합 테스트 위치를 따른다.

**Interfaces:**
- Consumes: Task 6 형식

사례 구성(설계 §5.2 표 그대로; 요청문·테스트 코드는 코퍼스에 맞게 만든다):

| id | 범위 | 다루는 것 |
| :--- | :--- | :--- |
| t1 | Modern | AC 하나를 서비스·컨트롤러 두 작업이 채움(첫 작업 RED, 마지막 작업 GREEN) |
| t2 | Modern | `breaks:`로 옛 동작 테스트(`*_CurrentlyCancels`)를 뺌 |
| t3 | Modern | 그룹 시그니처 변경 + 새 시그니처로 쓴 테스트(컴파일 RED, 엄격 회귀) |
| t4 | Modern | 이미 통과하는 테스트(RED 확인 경고, `--yes`로 계속) |
| t5 | Modern | 한 작업이 AC1의 마지막이자 AC2의 첫 작업 |
| t6 | Modern+Sql | C#+SQL을 함께 참조하는 AC(SQL 작업은 판정 안 함), 그리고 SQL만 참조하는 AC(미검증) |
| t7 | Modern | 수용 기준 테스트가 대상 프로젝트를 직접 참조하지 않는 테스트 프로젝트에 있음(선언 프로젝트 합집합으로만 찾음) |
| t8 | Legacy | 레거시 테스트 프로젝트 + `test.command`(`{trx}`), Windows 전용 |

- 각 사례의 `spec.md`는 `spec check`(오류 0)를 통과해야 한다. 경고는 `expected.json`과 맞으면 된다. 예: t6의 `TestUnverified`.
- t7에 새 테스트 프로젝트가 필요하면 `files/` 안에 만든다. 코퍼스는 고치지 않는다.
- t8의 `test.command`는 사례 폴더에 `pelly.json` 조각으로 두고, 실행기가 작업 복사본의 설정에 합친다. 방법은 Task 6 실행기 수정에 포함해도 되고, 이 작업에서 실행기를 조금 고쳐도 된다.

- [ ] **Step 1: 실패하는 정의 검사를 쓴다(`ApplyTestFirstCasesTests`)**
  - `Cases_AreEight_WithExpected`, `Cases_SpecCheckHasNoErrors`(코퍼스 사본 + `files/`에서 `SpecChecker.Check`)
  - `ModernCases_RedIsRealOnOriginal`(실제 dotnet): t1·t2·t5·t6·t7의 `test:`는 원본+`files/`에서 실패, t4는 통과, t3은 테스트 프로젝트가 컴파일 오류(CS 오류만, 지정 테스트 안)
  - `Cases_BreaksPassOnOriginal`
- [ ] **Step 2:** 사례가 없어 FAIL
- [ ] **Step 3:** 사례 8건을 쓴다.
- [ ] **Step 4:** PASS(t8 관련은 Windows 전용으로 건너뜀), `tests/PellyCoder.Eval.Tests` 전체 PASS
- [ ] **Step 5: 커밋** `feat(eval): eight test-first apply cases and their definition checks`

### Task 8: 예비 측정과 문서

**Files:**
- Create: `docs/v2-2c-test-first-benchmark.md`
- Modify: `docs/PRD.md`(§12.4 머리의 구현 표시에 V2-2c 줄, §12.6 측정 기준에 테스트 우선 지표 링크), 설계 문서 상태 줄, `eval/benchmark/README.md`(`--test-first`, `compare --test-first`)

이 작업은 코드가 아니라 측정이다. 로컬 Ollama(`qwen3.6:35b`, 기존 벤치마크 설정 파일)로 돌린다.
1. `pelly-eval run --tasks cs-modern --repeat 3 --out <A>`(v1)
2. `pelly-eval run --tasks cs-modern --test-first --repeat 3 --out <B>`
3. `pelly-eval compare --test-first <A>/summary.json <B>/summary.json`
4. `pelly-eval apply --set test-first --repeat 1 --out <C>`(SQL 연결이 없으면 t6은 「SQL 연결 없음」, macOS에서 t8은 「Windows 전용」으로 건너뜀)

결과 폴더는 git에 넣지 않는다(`eval/results`는 추적 제외). 문서에 남길 것은 다음과 같다.
- 실행 조건(모델·digest·Ollama 버전·pelly 커밋·날짜)
- 표: 성공률 v1 vs 테스트 우선, 막은 잘못된 패치, 피드백으로 고친 비율, 충족률, RED 경고율, 시간 비율, 위반 수, apply 사례별 기대 vs 실제
- 사람 표본 검토 대상 목록
- 모든 수치는 「예비(외부 개발 PC, 사례 승인 전)」로 표시한다.
- 측정이 30분 넘게 돌면 백그라운드로 돌리고 끝을 기다린다. 서버가 없거나 측정이 실패하면 그 사실과 오류를 문서에 적고 수치 칸은 비운다(지어내지 않는다).

- [ ] **Step 1:** 측정 1~4를 실행한다(종료 코드와 결과 폴더를 보고서에 적는다).
- [ ] **Step 2:** 문서를 쓴다.
- [ ] **Step 3:** `dotnet build -warnaserror`와 `dotnet test` 전체 게이트
- [ ] **Step 4: 커밋** `docs: V2-2c preliminary test-first benchmark`
