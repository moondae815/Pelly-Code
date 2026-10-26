# V2-2b 테스트 우선 검증 — `pelly apply` 연결 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 명세 수용 기준의 `test:`·`breaks:`를 `pelly apply`의 RED·GREEN·회귀 판정에 잇는다. 대상은 그룹 밖 작업과 작업 그룹(새 시그니처로 쓴 테스트의 컴파일 RED 포함) 모두다. 결과는 `spec check`, `apply.json`, `pelly report`에 남긴다.

**Architecture:**
- 명세에서 AC마다 「연결 작업」을 구하는 순수 함수 `TestFirstLinks`를 둔다. `spec check`와 `SpecRunner`가 함께 쓴다.
- **그룹 밖 작업:** V2-2a FSM을 그대로 쓴다. `RunRequest`에 이름별 역할(RED 확인·GREEN 확인)을 더해 「첫 연결 작업은 RED, 마지막 연결 작업은 GREEN」을 표현한다. FSM은 RED 상태를 `RunResult`로 돌려준다.
- **그룹:** FSM은 그룹 작업에서 빌드도 테스트도 하지 않는다. 그래서 `SpecRunner`의 그룹 흐름에 `IGroupTester`를 더한다. 기준 빌드 뒤에 기준 테스트와 RED(컴파일 RED 포함), 그룹 빌드 통과 뒤에 GREEN·회귀를 본다.
  - GREEN 실패는 그 AC의 그룹 안 마지막 연결 작업에 피드백을 주고 다시 실행한다.
  - 회귀가 있으면 그룹을 롤백한다.
- 회귀 판정(v1 `TestStep`의 「새로 실패한 테스트·호스트 사망」 규칙)은 공유 함수로 빼서 그룹 쪽도 같은 규칙을 쓴다.

**Tech Stack:** .NET 10, Roslyn(구문만), System.IO.Abstractions, xunit v3.

**Spec:** `docs/superpowers/specs/2026-10-26-v2-2-test-first-design.md`. 이 계획의 범위(§12 V2-2b):
- §5.2b(명세 `breaks:`), §5.3 컴파일 RED(그룹 전용), §7.1~§7.3, §8 `apply.json`·리포트(AC 단위·미검증·`breaks:`로 뺀 수), §11 단위 테스트 중 apply분
- 제외: 벤치마크(V2-2c), 한국어 스택 형식(V2-2a 원장의 미룬 것)

근거: V2-2a 구현(main `1831193`), V2-2a 원장 `~/.claude/projects/-Users-payletter-git-root-Pelly-Code/handoff/v2-2a-ledger.md`

## Global Constraints

- **보존:**
  - 수용 기준에 `test:`가 없거나 연결 작업이 없는 명세의 `pelly apply`·`spec check`는 동작·프롬프트·종료 코드·기존 테스트가 그대로다. 단, 아래 「spec check 변경」의 경고 문구는 바뀐다.
  - `pelly cs --verify-test`(V2-2a) 동작도 그대로다.
- **게이트:** 워크트리 `/Users/payletter/git-root/Pelly-Code-v2-2b`에서 `dotnet build -warnaserror` 종료 0, `dotnet test` 실패 0. 건너뜀은 SQL 서버·로컬 모델·Windows 전용만 허용한다. 메인 체크아웃 `/Users/payletter/git-root/Pelly-Code`에서는 빌드·테스트하지 않는다.
- **연결 작업(§7.1):**
  - AC의 연결 작업은 `(AC…)`로 그 AC를 참조하는 `cs` 작업이다. 명세 순서를 따르고 완료(`[x]`) 작업도 넣는다.
  - SQL 작업은 연결 작업이 아니다.
  - RED는 첫 연결 작업, GREEN은 마지막 연결 작업에서 본다. 연결 작업은 모두 테스트를 돌리고 그 AC의 `breaks:`를 회귀에서 뺀다.
- **이름:** `test:`·`breaks:` 값은 V2-2a `TestNames.IsValid` 규칙을 따르고, 찾기는 `TestMethodIndex.Find`(테스트 특성이 붙은 메서드)로 한다.
- **판정 불가 = 종료 4:** 그룹 테스트를 돌릴 수 없을 때다. 편집 전이면 바로 종료 4, 편집 뒤면 그룹 롤백 뒤 종료 4다(§7.3).
- **재시도:** 그룹 GREEN 재실행은 빌드 오류 재실행과 같은 그룹 재시도 예산(`o.MaxRetries ?? GroupRetryMax`) 하나를 함께 쓴다.
- **새 실패 유형(`FailureKinds.All`·`GroupFailureKinds`에 등록):**
  - `GroupTestGreenFailed`: 그룹 재실행을 다 써도 GREEN이 아님, 종료 1
  - `GroupTestRegression`: 그룹 회귀, 종료 1
  - `GroupTestUnverifiable`: 그룹 테스트 판정 불가, 종료 4
  - 그룹 밖 작업은 V2-2a 유형을 그대로 쓴다.
- **문구·커밋:**
  - 사용자 문구는 한국어, 모델 문구·코드 주석은 영어다.
  - 커밋 꼬리는 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`다.
  - 경로 단언에 `/`를 박지 않는다.
  - 비식별 리포트에는 테스트 이름과 AC ID가 없고 개수만 있다.

## Review Focus

1. **한 작업이 AC1의 마지막 연결 작업이면서 AC2의 첫 연결 작업일 때:** AC1 테스트는 GREEN만, AC2 테스트는 RED만 본다. AC1 테스트의 「이미 통과」 경고는 나오면 안 된다(Task 3 `Roles_GreenOnlyName_NoAlreadyPassedWarning`, Task 4 `Apply_TaskLastForAc1FirstForAc2`).
2. **`--resume`에서 첫 연결 작업은 `[x]`이고 마지막은 남았을 때:** RED는 「이어서 하기」로 기록하고 GREEN은 그대로 본다(Task 4 `Apply_Resume_RedSkippedGreenChecked`).
3. **`--continue`에서 「이미 통과」 확인을 거부했을 때:** 다음 작업으로 가지 않고 apply가 종료 2로 멈춘다(Task 4 `Apply_RedDeclined_StopsEvenWithContinue`).
4. **AC의 첫 연결 작업은 그룹 밖, 마지막은 그룹 안일 때:** RED는 그룹 밖 작업의 FSM 실행에서, GREEN은 그룹 테스트에서 본다(Task 6 `Group_GreenForAcStartedOutsideGroup`).
5. **AC를 `cs`·`sql` 작업이 함께 참조할 때:** `sql` 작업은 테스트를 돌리지 않고, `cs` 작업만 연결 작업이다. `sql`만 참조하면 「미검증」이다(Task 2 `Links_SqlTaskIsNotLinked`, Task 7 `Report_UnverifiedAc_CountedSeparately`).

---

## File Structure

- `src/PellyCoder.Core/Specs/`
  - `ChangeSpec.cs` (수정): `AcceptanceCriterion.Breaks`
  - `ChangeSpecParser.cs` (수정): `test:`·`breaks:` 하위 줄
  - `TestFirstLinks.cs` (새): AC → 연결 작업, 작업 → 이름별 역할
  - `SpecChecker.cs` (수정): §7.1 검사
  - `FsmTaskExecutor.cs`, `ITaskExecutor.cs` (수정): 역할을 요청에 싣기
  - `SpecRunner.cs` (수정): 그룹 밖 연결, 거부·이어서 하기·끝 승인, AC 결과, 그룹 테스트
  - `GroupTester.cs` (새): `IGroupTester`와 구현
  - `ApplySummary.cs` (수정): `ApplySummary.Criteria`, 새 그룹 실패 유형
- `src/PellyCoder.Core/Testing/`
  - `TestRegressions.cs` (새): `TestStep`에서 뺀 회귀 판정
  - `CompileRed.cs` (새): 컴파일 RED 판정
  - `TestFirstJudge.cs` (수정): `RedState.RedCompile`, `RedState.Skipped`
- `src/PellyCoder.Core/Fsm/RunRequest.cs`, `Fsm/Phases/ApplyPhase.cs`, `Verification/TestStep.cs`, `Fsm/FsmOrchestrator.cs`, `Logging/RunLogger.cs` (수정): 역할, `RunResult.TestFirst`, `TestFirstSummary.InApply`
- `src/PellyCoder.Core/Reporting/` (수정): `ApplyReport.cs`, `RunReport.cs`, `ReportSanitizer.cs`, `FailureKinds.cs`
- `src/PellyCoder.Cli/Commands/ReportCommand.cs`, `ApplyCommand.cs`(그룹 테스트기 연결), `Composition.cs` 또는 `SpecRunnerFactory.cs` (수정)
- 문서: `README.md`의 `pelly apply`·`pelly spec` 절, `docs/PRD.md` §12.4, 설계 상태 줄

---

### Task 1: 명세의 `breaks:`와 `test:` 하위 줄

**Files:**
- Modify: `src/PellyCoder.Core/Specs/ChangeSpec.cs`, `src/PellyCoder.Core/Specs/ChangeSpecParser.cs`
- Test: `tests/PellyCoder.Core.Tests/Specs/ChangeSpecParserTests.cs`

**Interfaces:**
- Produces: `AcceptanceCriterion(string Id, string Text, string? Test, IReadOnlyList<string>? Breaks = null)`. 접근자 `IReadOnlyList<string> BreakNames => Breaks ?? []`.

AC 줄 바로 다음에 오는 들여쓴 줄들(`^\s+(test|breaks):\s*(.+?)\s*$`)을 순서에 상관없이 그 AC의 하위 줄로 읽는다. 들여쓴 줄이 아닌 줄을 만나면 멈춘다. 오류(작업 줄 번호 = 그 하위 줄):
- `test:` 두 번째 → `DuplicateTest`, `수용 기준 {id}에 test:가 둘 이상입니다.`
- 이름이 `TestNames.IsValid`가 아님 → `InvalidTestName`, `수용 기준 {id}의 테스트 이름 형식이 아닙니다: {값}`
- `breaks:`에 `test:`와 같은 값 → `BreaksSameAsTest`, `수용 기준 {id}: 같은 테스트를 test:와 breaks:에 함께 둘 수 없습니다: {값}`
- 같은 AC의 중복 `breaks:`는 한 번만 넣는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `Parse_TestAndBreaks_AnyOrder`: `- AC1: …` 다음 `  breaks: A.Old`, `  test: A.New`, `  breaks: A.Older` → `Test == "A.New"`, `BreakNames == ["A.Old","A.Older"]`
  - `Parse_TestOnly_Unchanged`: 기존 모양 그대로(`Breaks` null 또는 빈 목록, 이슈 0)
  - `Parse_DuplicateTest_Error`, `Parse_InvalidTestName_Error`(`A.M(1)`), `Parse_BreaksSameAsTest_Error`
  - `Parse_UnindentedLineEndsCriterion`: AC 다음 들여쓰지 않은 `breaks: X`는 AC의 하위 줄이 아니다.
- [ ] **Step 2:** `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~ChangeSpecParserTests"` → FAIL
- [ ] **Step 3:** 구현한다.
- [ ] **Step 4:** PASS, 그리고 `--filter "FullyQualifiedName~Specs"` 전체 PASS
- [ ] **Step 5: 커밋** `feat(specs): breaks: lines under acceptance criteria`

### Task 2: 연결 작업 계산과 `spec check`

**Files:**
- Create: `src/PellyCoder.Core/Specs/TestFirstLinks.cs`
- Modify: `src/PellyCoder.Core/Specs/SpecChecker.cs`
- Test: `tests/PellyCoder.Core.Tests/Specs/TestFirstLinksTests.cs`, `tests/PellyCoder.Core.Tests/Specs/SpecCheckerTests.cs`

**Interfaces:**
- Consumes: Task 1 `AcceptanceCriterion.BreakNames`. V2-2a `TestMethodIndex.Find(IFileSystem, string root, string name) -> IReadOnlyList<TestMethodDecl>`, `TestMethodDecl(FullName, File, Csproj)`.
- Produces:
  - `sealed record AcLink(string AcId, string Test, IReadOnlyList<string> Breaks, IReadOnlyList<string> LinkedTaskIds)`. `LinkedTaskIds`는 명세 순서이고 완료 작업도 넣는다.
  - `[Flags] enum TestCheck { None = 0, Red = 1, Green = 2 }`
  - `sealed record TaskTests(IReadOnlyDictionary<string, TestCheck> Checks, IReadOnlyList<string> Breaks)`. `Checks`의 키는 테스트 이름이다. 그 작업이 연결된 모든 AC의 테스트가 들어간다(첫·마지막이 아니면 `None`).
  - `static class TestFirstLinks`
    - `IReadOnlyList<AcLink> Build(ChangeSpec spec)`: `Test`가 있는 AC만 돌려준다.
    - `TaskTests? For(IReadOnlyList<AcLink> links, string taskId)`: 연결된 AC가 없으면 null이다.
    - 한 테스트 이름이 두 AC에 있으면 역할을 OR로 합친다.

`spec check` 변경(§7.1, §5.2b). 기존 `TestNotFound` 경고와 `Scans.TestExists`를 바꾼다.
- 연결 작업 가운데 미완료가 있는 AC를 「판정 대상」이라 한다.
  - 판정 대상의 `test:`를 `TestMethodIndex`로 찾는다. 0개면 **오류** `TestNotFound`, 서로 다른 `FullName`이 2개 이상이면 **오류** `TestAmbiguous`다.
  - 판정 대상이 아닌 AC의 0개·2개 이상은 지금처럼 **경고**다(코드도 같다).
  - 문구: `수용 기준 {id}의 테스트 {name}을(를) 작업 공간에서 찾을 수 없습니다 (테스트 특성이 붙은 메서드만 찾습니다).`, `수용 기준 {id}의 테스트 {name}이(가) 여러 테스트와 맞습니다: {후보}`
- `breaks:`는 같은 규칙으로 찾는다. 판정 대상이면 오류, 아니면 경고다.
- `test:`가 있는데 연결 `cs` 작업이 하나도 없으면 **경고** `TestUnverified`, `수용 기준 {id}은(는) 테스트로 검증되지 않습니다 (연결된 cs 작업이 없음).`다.
- 미완료 연결 작업의 대상(`파일#멤버`)이 그 AC 테스트의 선언(같은 파일 + 같은 메서드 이름)이면 **오류** `TestIsTarget`, `작업 {task}: 수용 기준 {id}의 테스트 메서드를 고치는 작업입니다. 테스트는 사람이 씁니다.`다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `Links_FirstAndLastLinkedCsTasks`: T1(cs, AC1), T2(sql, AC1), T3(cs, AC1) → `LinkedTaskIds == ["T1","T3"]`. `For(T1)`은 `Red`, `For(T3)`는 `Green`, `For(T2)`는 null이다.
  - `Links_SingleLinkedTask_RedAndGreen`
  - `Links_SqlTaskIsNotLinked`: AC가 sql 작업만 참조 → `LinkedTaskIds` 빈 목록
  - `Links_TaskLastForAc1FirstForAc2`: T1(AC1), T2(AC1, AC2), T3(AC2) → `For(T2).Checks` = {AC1 테스트: Green, AC2 테스트: Red}
  - `Links_DoneTasksIncluded`, `Links_BreaksCarried`
  - `Check_LinkedTestMissing_IsError`, `Check_UnlinkedTestMissing_IsWarning`, `Check_LinkedTestAmbiguous_IsError`
  - `Check_ProductMethodSameName_NotATest`: 특성 없는 제품 메서드만 이름이 같음 → 판정 대상이면 오류
  - `Check_TestWithoutLinkedCsTask_TestUnverified`, `Check_TaskTargetsItsTest_TestIsTarget`
  - `Check_LinkedBreaksMissing_IsError`
  - `Check_AllLinkedTasksDone_OnlyWarnings`(`--resume` 대비)
- [ ] **Step 2:** `--filter "FullyQualifiedName~TestFirstLinksTests|FullyQualifiedName~SpecCheckerTests"` → FAIL
- [ ] **Step 3:** 구현한다. `Scans.TestExists`와 `methods` 캐시는 쓰는 곳이 없어지면 지운다.
- [ ] **Step 4:** PASS, `SpecCommandTests`(CLI) PASS
- [ ] **Step 5: 커밋** `feat(specs): acceptance-criterion links and spec check test rules`

### Task 3: FSM — 이름별 역할과 결과 돌려주기

**Files:**
- Modify: `src/PellyCoder.Core/Fsm/RunRequest.cs`, `Fsm/Phases/ApplyPhase.cs`, `Verification/TestStep.cs`, `Fsm/FsmOrchestrator.cs`, `Logging/RunLogger.cs`, `Reporting/RunReport.cs`, `Testing/TestFirstJudge.cs`
- Test: `tests/PellyCoder.Core.Tests/Fsm/TestFirstFlowTests.cs`, `tests/PellyCoder.Core.Tests/Reporting/RunReportTests.cs`

**Interfaces:**
- Consumes: Task 2 `TestCheck`
- Produces:
  - `RunRequest`에 마지막 선택 인자 `IReadOnlyDictionary<string, TestCheck>? TestChecks = null`. null이면 모든 이름이 `Red | Green`이다(V2-2a 단독 실행).
  - `RedState`에 `RedCompile`, `Skipped`를 맨 뒤에 더한다. V2-2a 값의 순서는 바꾸지 않는다.
  - `sealed record TestFirstOutcome(IReadOnlyDictionary<string, RedState> Red, int BreaksExcluded)`. `Red`에는 RED 확인 역할인 이름만, 확인한 상태로 들어간다.
  - `RunResult`에 마지막 선택 인자 `TestFirstOutcome? TestFirst = null`. 테스트 우선 실행에서 채운다.
  - `TestFirstSummary`에 마지막 선택 인자 `bool InApply = false`. `Request.Host is not null`이면 true다.

동작:
- APPLY의 「이미 통과」 경고·확인은 `Red` 역할인 이름만 대상이다.
- VERIFY의 GREEN은 `Green` 역할인 이름만 본다. 역할이 `None`이거나 `Red`만인 이름도 회귀 판정에서 빼지 않는다(v1 규칙대로 기준에서 실패하던 것은 원래 세지 않는다).
- `TestFirstSummary.Red`는 `Red` 역할 이름만으로 모은다. `Red` 역할이 없으면 `NotChecked`다. `Green` 역할이 없으면 `Green`도 `NotChecked`로 둔다.
- `RunReport`의 `TestFirstCounts`는 `InApply == true` 실행을 세지 않는다(AC는 Task 7이 apply 기록에서 센다).
- LOCATE의 사전 조건(색인·해시·프로젝트 합집합)은 역할과 상관없이 모든 이름에 적용한다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `Roles_RedOnly_NoGreenCheck`: 역할 `Red`, VERIFY에서 지정 테스트가 여전히 실패 → 종료 0. 회귀로도 세지 않는다(기준에서 실패). `summary.testFirst.green == "NotChecked"`
  - `Roles_GreenOnlyName_NoAlreadyPassedWarning`: 역할 `Green`, 기준에서 통과 → 경고 없음, GREEN 통과로 종료 0
  - `Roles_GreenOnly_Fails_Retries`: GREEN 실패면 V2-2a와 같이 재시도·`TestGreenFailed`
  - `Roles_None_RegressionOnly`
  - `RunResult_CarriesRedStates`: `RunResult.TestFirst.Red`에 Red 역할 이름과 상태
  - `Summary_InApply_WhenHosted`: `Host` 있는 요청 → `inApply: true`
  - `RunReport_SkipsInApplyRuns`
  - 기존 V2-2a 테스트는 그대로 통과해야 한다(`TestChecks` null).
- [ ] **Step 2:** `--filter "FullyQualifiedName~TestFirstFlowTests|FullyQualifiedName~RunReportTests"` → FAIL
- [ ] **Step 3:** 구현한다.
- [ ] **Step 4:** PASS, `dotnet test tests/PellyCoder.Core.Tests` 전체 PASS
- [ ] **Step 5: 커밋** `feat(fsm): per-test RED/GREEN roles and test-first outcome in RunResult`

### Task 4: `pelly apply` — 그룹 밖 작업 연결과 AC 결과

**Files:**
- Modify: `src/PellyCoder.Core/Specs/ITaskExecutor.cs`, `Specs/FsmTaskExecutor.cs`, `Specs/SpecRunner.cs`, `Specs/ApplySummary.cs`
- Test: `tests/PellyCoder.Core.Tests/Specs/SpecRunnerTestFirstTests.cs` (새, 기존 `SpecRunner` 테스트의 가짜 `ITaskExecutor` 방식을 따른다), `tests/PellyCoder.Core.Tests/Specs/FsmTaskExecutorTests.cs`(있으면 거기, 없으면 새로)

**Interfaces:**
- Consumes: Task 2 `TestFirstLinks`·`TaskTests`, Task 3 `RunRequest.TestChecks`·`RunResult.TestFirst`·`RedState`
- Produces:
  - `TaskRunOptions`에 마지막 선택 인자 `TaskTests? Tests = null`. `FsmTaskExecutor`는 이것을 `VerifyTests`(키 목록), `TestChecks`, `ExpectBreaks`로 옮긴다. SQL 작업에는 싣지 않는다.
  - `enum AcOutcome { Met, NotMet, NotRun, Unverified }`
  - `sealed record AcResult(string AcId, RedState Red, AcOutcome Outcome, string? GreenRunId, int BreaksExcluded)`
  - `ApplySummary`에 마지막 선택 인자 `IReadOnlyList<AcResult>? Criteria = null`. `test:`가 있는 AC가 하나라도 있으면 채운다.

SpecRunner 규칙:
- 시작할 때 `TestFirstLinks.Build`로 링크를 만들고, 작업마다 `For(links, task.Id)`를 `TaskRunOptions.Tests`로 넘긴다.
- **`--resume`:** 첫 연결 작업이 이미 `[x]`인 AC는 그 테스트의 `Red` 역할을 지운다. 그 AC의 `AcResult.Red`는 `Skipped`다.
- **「이미 통과」 거부:** `r.FailureKind == "TestRedDeclined"`이면 `--continue`여도 다음 작업으로 가지 않는다. 실패 표시를 남기고 apply를 종료 2로 끝낸다.
- **`--review-at-end`:**
  - 작업 실행은 지금처럼 `Yes`다. FSM은 묻지 않고 기록한다.
  - 끝 승인 직전에 「이미 통과」인 AC가 있으면 `이미 통과한 테스트로 판정된 수용 기준: {AC들}`을 알린다.
  - 승인을 거부하면 모든 AC의 `Outcome`은 `NotRun`이다.
- **AC 결과:**
  - `Red`: 첫 연결 작업 실행의 `RunResult.TestFirst.Red[test]`. 그 작업이 돌지 않았으면 `NotChecked`, 이어서 하기면 `Skipped`.
  - `Outcome`:
    - 연결 작업 없음 → `Unverified`
    - 마지막 연결 작업이 종료 0으로 끝나 남음 → `Met`
    - 마지막 연결 작업이 종료 2이거나 돌지 않았음(앞에서 멈춤, 끝 승인 거부, 취소) → `NotRun`
    - 그 밖 → `NotMet`
  - `GreenRunId`는 마지막 연결 작업의 run-id, `BreaksExcluded`는 그 실행의 값이다.
  - 그룹 작업이 마지막 연결 작업인 AC는 Task 6이 채운다. 여기서는 그룹 작업의 `Outcome`을 그룹 결과(종료 0 → `Met`)로만 둔다.
- `apply.json`(`Finish`)에 `Criteria`를 쓴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `Executor_PutsTestsOnRequest`: `cs` 작업에 `VerifyTests`·`TestChecks`·`ExpectBreaks`가 실리고, `sql` 작업에는 없다.
  - `Apply_SingleLinkedTask_RedAndGreen`: 가짜 실행기가 받은 `TaskRunOptions.Tests.Checks`가 `Red | Green`
  - `Apply_TaskLastForAc1FirstForAc2`
  - `Apply_Resume_RedSkippedGreenChecked`: T1 `[x]`, T3 남음 → T3 역할 `Green`만, `Criteria[0].Red == Skipped`
  - `Apply_RedDeclined_StopsEvenWithContinue`: `--continue`, T1 결과가 종료 2 + `TestRedDeclined` → T2는 실행되지 않음, apply 종료 2
  - `Apply_ReviewAtEnd_ListsAlreadyPassedAcs` / `Apply_ReviewAtEnd_Rejected_AllNotRun`
  - `Apply_Criteria_MetNotMetNotRunUnverified`: 네 경우를 한 명세로
  - `Apply_NoTestCriteria_NoCriteriaField`: `test:` 없는 명세는 `apply.json`에 `criteria`가 없다(보존).
- [ ] **Step 2:** `--filter "FullyQualifiedName~SpecRunnerTestFirstTests|FullyQualifiedName~FsmTaskExecutor"` → FAIL
- [ ] **Step 3:** 구현한다.
- [ ] **Step 4:** PASS, `--filter "FullyQualifiedName~Specs"` 전체 PASS
- [ ] **Step 5: 커밋** `feat(apply): link acceptance-criterion tests to tasks, criteria results in apply.json`

### Task 5: 회귀 판정 공유와 컴파일 RED 판정

**Files:**
- Create: `src/PellyCoder.Core/Testing/TestRegressions.cs`, `src/PellyCoder.Core/Testing/CompileRed.cs`
- Modify: `src/PellyCoder.Core/Verification/TestStep.cs` (회귀 판정을 `TestRegressions`로 옮기되 동작은 그대로)
- Test: `tests/PellyCoder.Core.Tests/Testing/TestRegressionsTests.cs`, `tests/PellyCoder.Core.Tests/Testing/CompileRedTests.cs`

**Interfaces:**
- Consumes: V2-2a `TestRun`, `TestCaseResult`, `TestNames`, `TestMethodDecl`
- Produces:
  - `static (IReadOnlyList<TestCaseResult> Failures, bool ExitOnlyFailure) TestRegressions.Find(TestRun before, TestRun after, IReadOnlyList<string> excluded)`
    - `TestStep`이 지금 하는 일을 그대로 한다: 기준에서 실패하지 않던 테스트가 새로 실패한 것, 그리고 호스트가 죽어 빠진 것(`NotRunFeedback`).
    - `excluded`(`breaks`·GREEN 이름)와 `Matches`인 결과는 뺀다.
    - `ExitOnlyFailure`는 이름을 댈 수 없는 종료 코드 실패(v1 `exitFailures`)다. `TestStep`은 두 값을 지금과 같은 뜻으로 쓴다.
  - `static IReadOnlyList<TestCaseResult> TestRegressions.Strict(TestRun after, IReadOnlyList<string> excluded)`: 컴파일 RED 프로젝트용이다. `excluded`가 아닌 실패는 모두 회귀다. 결과가 없으면 「테스트별 결과 필수」로 판정 불가이며, 그 판단은 호출자가 한다.
  - `sealed record CompileRedVerdict(bool IsCompileRed, string? Unverifiable)`
  - `static CompileRedVerdict CompileRed.Judge(IFileSystem fs, string workspaceRoot, string testProject, IReadOnlyList<Diagnostic> buildErrors, IReadOnlyList<TestMethodDecl> declsInProject)`
    - 컴파일 RED는 다음 셋을 모두 만족할 때다.
      - 모든 오류가 컴파일러 오류(`BuildMessages.IsCompilerError`)다.
      - 모든 오류의 파일이 그 테스트 프로젝트 폴더 아래다.
      - 오류 하나 이상이 `declsInProject` 중 한 메서드 선언의 줄 범위 안에 있다(파일을 Roslyn으로 읽어 메서드 이름으로 선언을 찾는다).
    - 아니면 `Unverifiable`이다.
      - 비컴파일러 오류 → `테스트 프로젝트를 원본 상태에서 빌드할 수 없습니다: {코드}`
      - 테스트 프로젝트 밖 오류 → `테스트 프로젝트 밖에 원래 컴파일 오류가 있습니다: {파일}`
      - 지정 테스트와 상관없는 오류만 있음 → `테스트 프로젝트의 컴파일 오류가 지정한 테스트 안에 없습니다: {파일}({줄})`

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `Find_NewFailureCounts_PreexistingDoesNot`, `Find_ExcludedByMatch`, `Find_HostCrash_LostPassingTests`(V2-2a `TestStep`의 「cut short」 규칙과 같은 입력·출력)
  - `Strict_AnyFailureButExcluded`
  - `CompileRed_ErrorInsideTest_IsCompileRed`, `CompileRed_ErrorOutsideTestMethod_Unverifiable`, `CompileRed_NonCompilerError_Unverifiable`, `CompileRed_ErrorOutsideProject_Unverifiable`
  - 기존 `TestFlowTests`·`TestFirstFlowTests`가 바뀌지 않고 통과한다(옮긴 뒤에도 v1·V2-2a 동작 그대로).
- [ ] **Step 2:** FAIL 확인
- [ ] **Step 3:** 구현한다.
- [ ] **Step 4:** PASS, `dotnet test tests/PellyCoder.Core.Tests` 전체 PASS
- [ ] **Step 5: 커밋** `refactor(testing): shared regression rules; feat(testing): compile-RED verdict`

### Task 6: 그룹 테스트 (`IGroupTester`)와 그룹 흐름 연결

**Files:**
- Create: `src/PellyCoder.Core/Specs/GroupTester.cs`
- Modify: `src/PellyCoder.Core/Specs/SpecRunner.cs` (`RunGroupAsync`·`RunGroupTasksAsync`), `Specs/SpecRunnerFactory.cs`, `Specs/ApplySummary.cs`(`GroupFailureKinds`), `Reporting/FailureKinds.cs`, CLI 조립(`ApplyCommand.cs` 또는 `Composition.cs`)
- Test: `tests/PellyCoder.Core.Tests/Specs/GroupTesterTests.cs`, `tests/PellyCoder.Core.Tests/Specs/SpecRunnerGroupTestFirstTests.cs`

**Interfaces:**
- Consumes: Task 2 `AcLink`·`TaskTests`, Task 4 `AcResult`, Task 5 `TestRegressions`·`CompileRed`. V2-2a `TestRunner.RunAsync`, `TestFirstJudge.Red/Green`, `FeedbackBuilder.ForGreen`·`ForBuild`, `TestMethodIndex.Find`, `TestProjectLocator`의 이름 규칙(`Test` 포함), `TestOptions.Projects`.
- Produces:
  - `sealed record GroupTestPlan(IReadOnlyList<string> Tests, IReadOnlyList<string> RedNames, IReadOnlyList<string> GreenNames, IReadOnlyList<string> Breaks)`
  - `sealed record GroupTestBaseline(IReadOnlyList<string> Projects, IReadOnlyList<TestRun?> Runs, IReadOnlySet<int> CompileRed, IReadOnlyDictionary<string, RedState> Red, string? Unverifiable)`
  - `sealed record GroupTestVerdict(string? Unverifiable, IReadOnlyList<GreenFailure> Green, IReadOnlyList<Diagnostic> TestBuildErrors, IReadOnlyList<TestCaseResult> Regressions, int BreaksExcluded)`
  - `interface IGroupTester`
    - `Task<GroupTestBaseline> BaselineAsync(Workspace ws, IReadOnlyList<string> scope, GroupTestPlan plan, CancellationToken ct)`
    - `Task<GroupTestVerdict> VerifyAsync(Workspace ws, GroupTestBaseline baseline, GroupTestPlan plan, CancellationToken ct)`
  - `SpecRunner` 생성자에 마지막 선택 인자 `IGroupTester? groupTests = null`
  - 실패 유형 상수 `GroupFailureKinds.TestGreenFailed = "GroupTestGreenFailed"`, `TestRegression = "GroupTestRegression"`, `TestUnverifiable = "GroupTestUnverifiable"`(`IsGroupKind`에 포함, `FailureKinds.All`에 등록)

`GroupTester` 규칙:
- **테스트 프로젝트:** `scope` 중 이름에 `Test`가 있는 프로젝트(`test.projects`가 있으면 그 목록과 `scope`의 교집합) ∪ `plan.Tests`의 선언 `Csproj`다. 순서는 앞의 것 다음 새 것이고, 중복은 `PathComparers.ForOs`로 없앤다.
- **기준:**
  - 프로젝트마다 `TestRunner.RunAsync`를 부른다.
  - 빌드 오류가 있는 프로젝트는 `CompileRed.Judge`를 거친다. 컴파일 RED면 `CompileRed`에 넣고, 아니면 `Unverifiable`이다.
  - 컴파일 RED가 아닌 프로젝트만으로 `TestFirstJudge.Red`를 부른다. 선언이 컴파일 RED 프로젝트에 있는 이름은 결과에서 찾지 않고 `RedCompile`이다.
  - 레거시 on 비Windows, 테스트별 결과 없음, 이름 없음·모호함은 V2-2a와 같은 문구로 `Unverifiable`이다.
  - 컴파일 RED 프로젝트가 있으면 `그 테스트 프로젝트의 모든 테스트가 통과해야 합니다 (원본 상태에서 빌드되지 않아 비교할 결과가 없음): {프로젝트}`를 알린다.
- **판정:**
  - 같은 프로젝트를 다시 돌린다.
  - 컴파일 RED 프로젝트가 여전히 빌드되지 않으면 `TestBuildErrors`에 담는다(GREEN 실패로 다룬다).
  - GREEN은 `plan.GreenNames`만 `TestFirstJudge.Green`으로 본다.
  - 회귀는 보통 프로젝트에 `TestRegressions.Find`(제외 = `Breaks` ∪ 모든 `plan.Tests`), 컴파일 RED 프로젝트에 `TestRegressions.Strict`를 쓴다.

SpecRunner 그룹 흐름:
- 그룹 작업들의 `TaskTests`로 계획을 만든다.
  - `RedNames`는 그룹 안에 첫 연결 작업이 있는 AC의 테스트, `GreenNames`는 그룹 안에 마지막 연결 작업이 있는 AC의 테스트다.
  - `Tests`가 비면 그룹 테스트를 하지 않는다(지금 동작 그대로).
- **편집 전:** 그룹 기준 빌드 뒤에 `BaselineAsync`를 부른다.
  - `Unverifiable` → `UnverifiableBeforeEditAsync`와 같은 길로 종료 4, 실패 유형 `GroupTestUnverifiable`
  - 「이미 통과」가 있으면 그룹 밖 작업과 같은 문구로 알리고, 대화형이고 `--yes`가 아니면 묻는다. 거부하면 편집 없이 종료 2(`TestRedDeclined`)이고, Task 4와 같이 `--continue`여도 apply를 멈춘다.
  - `--review-at-end`면 묻지 않고 기록한다.
- **편집 뒤:** 그룹 빌드에 새 오류가 없으면 `VerifyAsync`를 부른다.
  - `Unverifiable` → `UndoGroupAsync(…, GroupFailureKinds.TestUnverifiable, 4)`
  - GREEN 실패나 `TestBuildErrors`가 있으면 → 실패한 테스트마다 그 AC의 그룹 안 마지막 연결 작업에 피드백을 준다(`ForGreen`, 빌드 오류면 `ForBuild`). 그 작업만 다시 실행하고 그룹 빌드부터 다시 한다. 재실행 수는 빌드 오류 재실행과 같은 `reruns`·`max`이고, 다 쓰면 `UndoGroupAsync(…, GroupFailureKinds.TestGreenFailed, 1)`이다.
  - GREEN이고 회귀가 있으면 → `그룹 {id}: 테스트 회귀: {이름들}`을 알리고 `UndoGroupAsync(…, GroupFailureKinds.TestRegression, 1)`
- AC 결과(Task 4의 `AcResult`)에서 그룹 안에 첫 연결 작업이 있는 AC의 `Red`는 기준의 `Red`, `BreaksExcluded`는 판정의 값이다.
- `IGroupTester`가 null인데 계획이 비어 있지 않으면 편집 전에 종료 4다(`그룹 테스트 실행기를 사용할 수 없습니다.`).
- CLI 조립에서 실제 `GroupTester`(`TestRunner`, `IFileSystem`, `TestOptions`, `isWindows`)를 넘긴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `GroupTester` 단위(`ScriptedProcessRunner`와 TRX 픽스처):
    - `Baseline_CompileRed_WhenErrorsInsideTest`
    - `Baseline_UnrelatedCompileError_Unverifiable`
    - `Baseline_ProjectsAreScopeTestsPlusDeclaring`
    - `Verify_CompileRedStillNotBuilding_IsTestBuildErrors`
    - `Verify_StrictRegressionInCompileRedProject`
    - `Verify_BreaksExcluded`
  - SpecRunner(가짜 `IGroupVerifier`·`IGroupTester`·`ITaskExecutor`):
    - `Group_NoTests_UnchangedFlow`(그룹 테스트기를 부르지 않음)
    - `Group_GreenFails_RerunsLastLinkedTaskWithFeedback_ThenPasses`
    - `Group_GreenNeverPasses_UndoneExit1`(`GroupTestGreenFailed`, 파일 원래대로)
    - `Group_Regression_UndoneExit1`
    - `Group_TestsUnverifiableBeforeEdit_Exit4_NoEdit`, `Group_TestsUnverifiableAfterEdit_UndoneExit4`
    - `Group_AlreadyPassed_Declined_Exit2_NoEdit`
    - `Group_GreenForAcStartedOutsideGroup`: AC의 T1은 그룹 밖, T2·T3 그룹 → RED는 T1의 FSM 실행에서, GREEN만 그룹에서. `RedNames` 빈 목록, `GreenNames`에 그 테스트
    - `Group_RerunBudgetSharedWithBuildErrors`: 빌드 오류 재실행 1회 + GREEN 재실행으로 `max` 초과 → 실패
    - `Group_TesterMissing_Exit4`
- [ ] **Step 2:** FAIL 확인
- [ ] **Step 3:** 구현한다.
- [ ] **Step 4:** PASS, `dotnet test tests/PellyCoder.Core.Tests`·`tests/PellyCoder.Cli.Tests` 전체 PASS
- [ ] **Step 5: 커밋** `feat(apply): group test-first — baseline/compile RED, GREEN re-runs, regression rollback`

### Task 7: 리포트 — AC 단위 충족률, 미검증, `breaks:`로 뺀 수

**Files:**
- Modify: `src/PellyCoder.Core/Reporting/ApplyReport.cs`, `Reporting/RunReport.cs`, `Reporting/ReportSanitizer.cs`, `src/PellyCoder.Cli/Commands/ReportCommand.cs`
- Test: `tests/PellyCoder.Core.Tests/Reporting/ApplyReportTests.cs`, `ReportSanitizerTests.cs`, `RunReportTests.cs`, `tests/PellyCoder.Cli.Tests/ReportCommandTests.cs`

**Interfaces:**
- Consumes: Task 4 `ApplySummary.Criteria`·`AcResult`, Task 3 `TestFirstSummary.InApply`
- Produces:
  - `sealed record AcCounts(int Met, int Judged, int RedWarned, int RedChecked, int Unverified, int BreaksExcluded)`, `ApplyReportData`에 마지막 선택 인자 `AcCounts? Criteria = null`
  - `TestFirstCounts`에 마지막 선택 인자 `int BreaksExcluded = 0`(단독 실행 합)
  - 화면·비식별 md의 두 줄(V2-2a)은 단독 실행과 AC를 **합친** 수로 낸다. 줄을 둘 더한다.
    - `| 테스트로 검증되지 않은 수용 기준 | {Unverified}건 |`
    - `| breaks로 뺀 테스트 | {합}건 |`

규칙(AC):
- `RedChecked`는 `Red ∈ {Red, RedCompile, AlreadyPassed}`, `RedWarned`는 `Red == AlreadyPassed`다.
- `Judged`는 `Red ∈ {Red, RedCompile}` 그리고 `Outcome ∈ {Met, NotMet}`이다.
- `Met`은 `Judged` 중 `Outcome == Met`이다.
- `Unverified`는 `Outcome == Unverified`다.
- `BreaksExcluded`는 `Judged` AC의 합이다.
- `Skipped`(이어서 하기)·`NotChecked`·`NotRun`은 어디에도 세지 않는다.
- 비식별 출력에는 AC ID 없이 이 숫자들만 둔다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `ApplyReport_CriteriaCounts`: AC 여섯 개(Red/Met, RedCompile/NotMet, AlreadyPassed/Met, Skipped/Met, Red/NotRun, NotChecked/Unverified) → `Met 1, Judged 2, RedWarned 1, RedChecked 4, Unverified 1`
  - `ApplyReport_OldApplyJsonWithoutCriteria_Loads`
  - `Report_CombinesSingleRunsAndCriteria`: 단독 1/1 + AC 1/2 → `2/3`
  - `Report_UnverifiedAc_CountedSeparately`, `Report_BreaksExcludedRow`
  - `Sanitize_NoAcIds`: `apply.json`의 AC ID와 테스트 이름이 비식별 JSON·md에 없다.
- [ ] **Step 2:** FAIL 확인
- [ ] **Step 3:** 구현한다.
- [ ] **Step 4:** PASS, `tests/PellyCoder.Core.Tests`·`tests/PellyCoder.Cli.Tests` 전체 PASS
- [ ] **Step 5: 커밋** `feat(report): acceptance criteria rates from apply, unverified and breaks counts`

### Task 8: 실제 dotnet E2E와 문서

**Files:**
- Create: `tests/PellyCoder.Integration.Tests/E2E/ApplyTestFirstEndToEndTests.cs` (V2-2a `TestFirstEndToEndTests` 방식: 코퍼스 `Modern` 사본, `CannedLlmBackend`)
- Modify: `README.md`(`pelly apply`·`pelly spec` 절), `docs/PRD.md`(§12.4 머리의 구현 표시에 V2-2b 줄), 설계 문서 상태 줄
- Test: 위 E2E

**Interfaces:**
- Consumes: 앞 작업 전부(실제 조립 경로)

- [ ] **Step 1: 실패하는 테스트를 쓴다(실제 dotnet, macOS에서도 돎)**
  - `Apply_AcTest_RedThenGreen`
    - 사본 `Shop.Api.Tests`에 `[Fact] CancelAsync_WhenShipped_Throws`를 넣는다.
    - 명세 `changes/ship.md`를 둔다. `AC1` + `test: OrderServiceTests.CancelAsync_WhenShipped_Throws` + `breaks: OrderServiceTests.CancelAsync_WhenShipped_CurrentlyCancels`, 작업 `T1 cs Modern/Shop.Api/Services/OrderService.cs#CancelAsync … (AC1)`.
    - 캔드 응답은 Shipped 예외를 넣는 블록이다. `pelly apply --yes`로 실행한다.
    - 기대: 종료 0, `apply.json`의 `criteria[0]`이 `red == "Red"`, `outcome == "Met"`, `breaksExcluded == 1`, 명세가 보관됨
  - `Apply_AcTest_WrongEdit_NotMet`: 예외 없는 수정, 재시도 0 → 종료 1, `outcome == "NotMet"`, 파일 원본
  - 그룹·컴파일 RED의 실제 dotnet E2E는 이 계획에 넣지 않는다(Claude 결정: 가짜로 Task 6에서 고정, 코퍼스 사례는 V2-2c 벤치마크에서).
- [ ] **Step 2:** `dotnet test tests/PellyCoder.Integration.Tests --filter "FullyQualifiedName~ApplyTestFirstEndToEndTests"` → FAIL(또는 연결 전이면 실패 이유를 보고서에 적는다)
- [ ] **Step 3:** 문서를 쓴다.
  - README에 쓸 것:
    - 명세의 `test:`·`breaks:` 문법
    - 연결 작업 규칙(첫 작업 RED, 마지막 작업 GREEN, SQL 제외)
    - `spec check`의 새 오류·경고
    - 그룹의 컴파일 RED와 엄격 회귀
    - `--resume`·`--continue`·`--review-at-end`에서의 동작
    - 새 실패 유형
    - 리포트 줄
  - PRD §12.4에는 `> **구현(V2-2b): 2026-10-26.** …`을 붙인다.
- [ ] **Step 4:** E2E PASS, 그다음 `dotnet build -warnaserror`(종료 0)와 `dotnet test`(실패 0) 전체
- [ ] **Step 5: 커밋** `feat(apply): test-first E2E over the corpus; docs`
