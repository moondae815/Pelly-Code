# V2-2a 테스트 우선 검증 — 단독 실행 (`pelly cs --verify-test`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `pelly cs … --verify-test <이름> [--expect-break <이름>]`으로 지정한 테스트의 RED(패치 전 실패)·GREEN(패치 뒤 통과)·회귀 없음을 판정하고, GREEN이 아니면 자가 교정하고, 결과를 `summary.json`과 `pelly report`(수용 기준 충족률·RED 확인 경고율)에 남긴다.

**Architecture:**
- v1 `--test`의 부품(`TestRunner`, `TestProjectLocator`, 기준 테스트 병렬 실행, `TestStep` 회귀 판정)을 그대로 타고 판정만 더한다.
- 이름 맞추기(`TestNames`)·구문 색인(`TestMethodIndex`)·판정(`TestFirstJudge`)은 FSM과 무관한 순수 코드로 `Testing/`에 둔다. 단위 테스트가 대부분 여기서 끝난다.
- FSM에서는 세 곳만 바뀐다.
  - LOCATE: 테스트 우선 실행의 사전 조건과 테스트 프로젝트 합집합
  - APPLY: 첫 쓰기 직전, 기준 테스트를 기다리는 곳에서 RED 판정
  - VERIFY `TestStep`: GREEN·회귀·`breaks` 제외, 테스트 파일 해시 확인
- `--verify-test`가 없는 실행은 코드 경로가 지금과 같다.

**Tech Stack:** .NET 10, Roslyn(구문 분석만), System.IO.Abstractions, Spectre.Console.Cli, xunit v3.

**Spec:** `docs/superpowers/specs/2026-10-26-v2-2-test-first-design.md`. 이 계획의 범위는 다음과 같다(§12 V2-2a).
- 포함: §3, §5.1~§5.6, §6, §8의 `summary.json`·`pelly report`, §9, §11의 단위 테스트 중 단독 실행분
- 제외: `--expect-break`를 뺀 명세 쪽 `breaks:`, apply 연결(§7), 그룹·컴파일 RED 수용(§7.3), `apply.json`, 벤치마크(§11 E2E)
  - 이것들은 V2-2b·V2-2c에서 한다.
  - V2-2a에서 컴파일 RED는 늘 「그룹 밖」이므로 종료 4다(§5.3).

## Global Constraints

- **보존:** `--verify-test`가 없는 `pelly cs`·`pelly sql`·`pelly apply`는 프롬프트·동작·종료 코드·기존 테스트가 그대로다. v1 `--test` 회귀 판정도 그대로다.
- **게이트:** 워크트리 `/Users/payletter/git-root/Pelly-Code-v2-2`에서 `dotnet build -warnaserror` 종료 0, `dotnet test` 실패 0. 건너뜀은 SQL 서버·로컬 모델·Windows 전용만 허용한다. 메인 체크아웃에서 빌드·테스트하지 않는다.
- **테스트별 결과 필수(§5.1):** 테스트 우선 실행에서 `TestRun.PerTestResults == false`인 프로젝트는 종료 4다. 문구: `테스트별 결과가 없어 지정한 테스트를 판정할 수 없습니다 (레거시는 test.command에 {trx}를 넣으세요)`.
- **판정 불가 = 종료 4:** 테스트 우선 실행에서는 v1의 「알림 뒤 빌드로 판정」으로 내려가지 않는다.
- **이름 규칙(§5.2):**
  - `--verify-test`·`--expect-break` 값은 `\A[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*\z`다. 아니면 CLI가 종료 4로 거절한다.
  - 맞춤은 점 경계의 뒷부분 일치이고, 대소문자를 구별하며, 결과 이름의 인자 부분 `(...)`은 떼고 본다.
- **테스트 특성(§7.1):** `Fact`, `Theory`, `Test`, `TestCase`, `TestMethod`, `DataTestMethod`. `Attribute` 접미사와 네임스페이스 한정(`Xunit.Fact`)도 같은 것으로 본다.
- **색인 범위:** `SpecChecker.Scans`와 같은 상한(디렉터리 2000, 파일 5000, 파일당 512KB)과 `BoundedWalker`를 쓴다.
- **실패 유형(새로, `FailureKinds.All`에 등록):**

  | 값 | 뜻 |
  | :--- | :--- |
  | `TestGreenFailed` | 종료 1. 재시도를 다 써도 GREEN이 아님 |
  | `TestRegression` | 종료 1. GREEN은 됐지만 회귀가 있음 |
  | `TestNotFound` | 종료 4 |
  | `TestAmbiguous` | 종료 4 |
  | `TestUnverifiable` | 종료 4. 테스트별 결과 없음, 기준 실행 실패, 건너뜀, 컴파일 RED, 레거시 on 비Windows, csproj 없음, 테스트 프로젝트 없음, 대상 자신 |
  | `TestRedDeclined` | 종료 2 |
  | `TestFileChanged` | 종료 4 |

- **피드백 끝 문장(§5.5):** `The test cannot be changed. Make it pass by implementing the request.`. 재생 실험은 V2-2c 벤치마크 때 한다. 지금은 이 문장으로 고정한다(Claude 결정).
- **문구·커밋:**
  - 사용자 문구는 한국어, 모델 문구·코드 주석은 영어다.
  - 커밋 꼬리는 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`다.
  - 경로 단언에 `/`를 박지 않는다(Windows CI).
  - 비식별 리포트에는 테스트 이름이 들어가지 않는다.

## Review Focus

1. **테스트 호스트가 죽어 결과가 비거나 지정 테스트만 빠진 실행:** GREEN으로 세면 안 된다. 「결과에 없음」은 실패다(Task 3 `Green_MissingFromRun_Fails`, Task 5 `VerifyTest_HostCrash_IsGreenFailure`).
2. **같은 메서드 이름이 두 테스트 클래스에 있을 때(`Cancel_Throws`가 `OrderServiceTests`·`OrderControllerTests`에 둘 다):** 짧은 이름을 주면 모호함으로 종료 4다. 하나를 고르면 안 된다(Task 2 `Find_SameMethodInTwoClasses_ReturnsBoth`, Task 3 `Red_TwoFullNames_IsAmbiguous`).
3. **이름이 다른 테스트의 접두어일 때(`Cancel`과 `Cancel_WhenShipped`), 접미가 겹칠 때(`XOrderServiceTests`):** 맞으면 안 된다(Task 2 `Matches_*`).
4. **Theory 데이터 행 일부만 실패:** 메서드 전체가 GREEN 실패다(Task 3 `Green_OneTheoryRowFails_Fails`).
5. **`--expect-break` 테스트가 원래도 실패하거나 결과에 없을 때:** 경고만 하고 실행은 계속한다. 다른 테스트의 회귀는 여전히 잡는다(Task 5 `ExpectBreak_ExcludesOnlyThatTest`).

---

## File Structure

- `src/PellyCoder.Core/Testing/`
  - `TrxParser.cs` (수정): 건너뜀 이름, 메시지 전체, 스택, `TestMethod`로 만든 전체 이름
  - `TestRunner.cs` (수정): `TestRun.Skipped`
  - `TestNames.cs` (새): 이름 맞추기
  - `TestMethodIndex.cs` (새): 테스트 특성이 붙은 메서드의 구문 색인
  - `TestFirstJudge.cs` (새): RED·GREEN 판정, 상태 enum
- `src/PellyCoder.Core/Fsm/`
  - `RunRequest.cs` (수정): `VerifyTests`, `ExpectBreaks`, `RunContext.TestFirst`
  - `Phases/LocatePhase.cs` (수정): 사전 조건, 테스트 프로젝트 합집합
  - `Phases/ApplyPhase.cs` (수정): RED 판정·확인
  - `Transitions.cs` (수정): `(Apply, Rejected)` → 종료 2
- `src/PellyCoder.Core/Verification/`
  - `TestStep.cs` (수정): GREEN·회귀·`breaks`·해시
  - `IVerifierStep.cs` (수정): `StepResult.Fail`의 실패 유형
  - `Fsm/Phases/VerifyPhase.cs` (수정): 실패 유형 반영
- `src/PellyCoder.Core/Feedback/FeedbackBuilder.cs` (수정): `ForGreen`
- `src/PellyCoder.Core/Logging/RunLogger.cs` (수정): `RunSummary.TestFirst`
- `src/PellyCoder.Core/Reporting/` (수정): `FailureKinds.cs`, `RunReport.cs`, `ReportSanitizer.cs`
- `src/PellyCoder.Cli/Commands/CsCommand.cs`, `ReportCommand.cs` (수정)
- 문서: `README.md`의 `pelly cs` 절, `docs/PRD.md` §12.4 구현 표시, 설계 §8의 분모 문장

---

### Task 1: TRX에서 건너뜀·메시지 전체·스택·전체 이름 읽기

**Files:**
- Modify: `src/PellyCoder.Core/Testing/TrxParser.cs`, `src/PellyCoder.Core/Testing/TestRunner.cs`
- Test: `tests/PellyCoder.Core.Tests/Testing/TrxParserTests.cs`, `tests/fixtures/tests/` (새 픽스처 `skipped-and-stack.trx`, `mstest-short-names.trx`)

**Interfaces:**
- Produces:
  - `TestCaseResult(string FullName, bool Passed, string? Message, string? Detail = null, string? StackTrace = null)`
    - `Message`는 지금처럼 첫 줄이다.
    - `Detail`은 `ErrorInfo/Message` 전체(`\r\n`→`\n`)다.
    - `StackTrace`는 `ErrorInfo/StackTrace` 전체다.
  - `TrxReport(IReadOnlyList<TestCaseResult> Results, bool RunAborted, IReadOnlyList<string> Skipped)`, `TrxParser.ParseFull(string trxXml) -> TrxReport`. `ParseReport`는 `ParseFull`을 불러 앞의 둘을 돌려준다(기존 호출부 그대로).
  - `TestRun`에 마지막 선택 인자 `IReadOnlyList<string>? Skipped = null`. TRX를 읽은 실행에서 채운다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `ParseFull_KeepsSkippedNames`: `outcome="NotExecuted"`인 `A.Skipped`가 `Skipped == ["A.Skipped"]`에 있고 `Results`에는 없다.
  - `ParseFull_ReadsDetailAndStack`: 실패 결과의 `Detail`이 두 줄(`Assert.Equal() Failure: Values differ\nExpected: 28000`)이고, `StackTrace`에 `at Shop.Api.Services.OrderService.CalculateRefundAsync(Int32 orderId) in /work/Shop.Api/Services/OrderService.cs:line 42`가 있다.
  - `ParseFull_ShortTestName_UsesTestMethodClass`: `testName="Cancel_Throws"`이고 `TestDefinitions`의 `TestMethod className="Shop.Tests.OrderTests" name="Cancel_Throws"`(`testId`로 연결)이면 `FullName == "Shop.Tests.OrderTests.Cancel_Throws"`다.
  - `ParseFull_DottedTestName_Unchanged`: `testName`에 점이 있으면 그대로 쓴다(지금 동작).
- [ ] **Step 2:** `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~TrxParserTests"` → 새 테스트 4개 FAIL(컴파일 오류 포함)
- [ ] **Step 3:** 구현한다. 전체 이름 규칙: `testName`에 `.`이 없고 같은 `testId`(없으면 `executionId`)의 `TestMethod`가 있으면 `className + "." + testName`이다. 짧은 이름 픽스처는 MSTest 모양으로 손으로 만든다(어댑터 실측은 이 계획 범위 밖).
- [ ] **Step 4:** 같은 필터로 PASS, 그리고 `dotnet test tests/PellyCoder.Core.Tests` 전체 PASS(기존 `TestFlowTests` 포함)
- [ ] **Step 5: 커밋** `feat(testing): TRX skipped names, full message, stack trace, class-qualified short names`

### Task 2: 이름 맞추기와 테스트 메서드 색인

**Files:**
- Create: `src/PellyCoder.Core/Testing/TestNames.cs`, `src/PellyCoder.Core/Testing/TestMethodIndex.cs`
- Test: `tests/PellyCoder.Core.Tests/Testing/TestNamesTests.cs`, `tests/PellyCoder.Core.Tests/Testing/TestMethodIndexTests.cs`

**Interfaces:**
- Produces:
  - `static class TestNames`
    - `bool IsValid(string name)`: Global Constraints의 정규식
    - `string WithoutArguments(string fullName)`: 첫 `(`부터 뗀다. 앞뒤 공백도 뗀다.
    - `bool Matches(string specName, string resultFullName)`: `WithoutArguments(result) == spec`이거나 `.` + spec으로 끝난다. Ordinal 비교다.
  - `sealed record TestMethodDecl(string FullName, string File, string? Csproj)`
    - `FullName`은 `네임스페이스.타입(중첩은 바깥.안).메서드`다. 파일 범위 네임스페이스도 받는다.
    - `Csproj`는 `ProjectFileLocator.FindNearestCsproj` 결과다.
  - `static class TestMethodIndex`
    - `IReadOnlyList<TestMethodDecl> Find(IFileSystem fs, string workspaceRoot, string name)`
    - 테스트 특성이 붙은 메서드 중 `TestNames.Matches(name, decl.FullName)`인 것을 전체 이름 순으로 돌려준다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `Matches_DotBoundarySuffix`: `("OrderServiceTests.Cancel", "Shop.Tests.OrderServiceTests.Cancel")` → true
  - `Matches_RejectsPartialSegment`: `("OrderServiceTests.Cancel", "Shop.Tests.XOrderServiceTests.Cancel")` → false
  - `Matches_RejectsPrefix`: `("Cancel", "Shop.T.Cancel_WhenShipped")` → false
  - `Matches_IgnoresArguments`: `("T.M", "N.T.M(x: 1, y: \"a\")")` → true
  - `Matches_IsCaseSensitive`: `("t.m", "N.T.M")` → false
  - `IsValid_RejectsSpacesAndParentheses`: `"T.M(1)"` → false, `"T M"` → false, `"_a.B1"` → true
  - `Find_OnlyAttributedMethods`: `[Fact] Cancel()`가 있는 테스트 파일과 특성 없는 제품 메서드 `Cancel()`이 함께 있어도, `Find("Cancel")`은 테스트 하나만 돌려준다.
  - `Find_SameMethodInTwoClasses_ReturnsBoth`: `OrderServiceTests.Cancel_Throws`와 `OrderControllerTests.Cancel_Throws` → `Find("Cancel_Throws")`는 2개, `Find("OrderServiceTests.Cancel_Throws")`는 1개
  - `Find_RecognizesAttributeForms`: `[Xunit.Fact]`, `[FactAttribute]`, `[TestMethod]`, `[Theory, InlineData(1)]`가 모두 잡힌다.
  - `Find_NestedAndFileScopedNamespace`: `namespace A.B;` 안 `Outer.Inner.M` → `FullName == "A.B.Outer.Inner.M"`
  - `Find_ReportsNearestCsproj`: 테스트 파일 위 폴더의 `X.Tests.csproj` 경로
- [ ] **Step 2:** `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~TestNamesTests|FullyQualifiedName~TestMethodIndexTests"` → FAIL
- [ ] **Step 3:** 구현한다. 파일 읽기는 `SpecChecker.Scans.Read`와 같은 규칙이다. 그 반복자를 `Files/` 아래 공유 정적 함수로 옮겨 둘이 함께 써도 된다. 그 경우 `SpecChecker` 동작은 그대로여야 한다.
- [ ] **Step 4:** 같은 필터로 PASS, `SpecCheckerTests` PASS
- [ ] **Step 5: 커밋** `feat(testing): test name matching and attributed test method index`

### Task 3: RED·GREEN 판정 (순수 함수)

**Files:**
- Create: `src/PellyCoder.Core/Testing/TestFirstJudge.cs`
- Test: `tests/PellyCoder.Core.Tests/Testing/TestFirstJudgeTests.cs`

**Interfaces:**
- Consumes: Task 1 `TestRun`·`TestCaseResult`, Task 2 `TestNames`
- Produces:
  - `enum RedState { NotChecked, Red, AlreadyPassed }`, `enum GreenState { NotChecked, Passed, Failed }`
  - `sealed record RedTest(string Name, string FullName, int ProjectIndex, RedState State, string? FirstMessage)`
  - `sealed record RedResult(IReadOnlyList<RedTest> Tests, string? Unverifiable, string? FailureKind)`
    - `Unverifiable`이 null이 아니면 종료 4 문구다.
    - `FailureKind`는 `TestNotFound`·`TestAmbiguous`·`TestUnverifiable` 중 하나다.
  - `static RedResult TestFirstJudge.Red(IReadOnlyList<string> projects, IReadOnlyList<TestRun?> baselines, IReadOnlyList<string> names)`
  - `sealed record GreenFailure(string FullName, string? Detail, string? StackTrace)`. 결과에 없거나 건너뛴 경우 `Detail`은 `"not run"`이다.
  - `static IReadOnlyList<GreenFailure> TestFirstJudge.Green(IReadOnlyList<RedTest> tests, IReadOnlyList<TestRun?> runs)`. 빈 목록이면 GREEN이다.
  - `static IReadOnlyList<string> TestFirstJudge.BreakWarnings(IReadOnlyList<TestRun?> baselines, IReadOnlyList<string> breaks)`: 기준에서 통과하는 결과가 없는 `breaks` 이름의 경고 문구들

RED 규칙(설계 §5.3 표, V2-2a판). 위에서부터 먼저 맞는 것을 쓴다.
1. 기준 실행이 null이다(실행 실패·시간 초과) → `TestUnverifiable`, `기준 테스트를 실행하지 못해 지정한 테스트를 판정할 수 없습니다`
2. `BuildErrors.Count > 0` → `TestUnverifiable`, `테스트 프로젝트가 원본 상태에서 빌드되지 않습니다 — 시그니처를 바꾸는 작업은 V2-2b의 그룹 지원을 기다려야 합니다`
3. `!PerTestResults` → `TestUnverifiable`, 「테스트별 결과 필수」 문구
4. 이름마다 모든 프로젝트 결과에서 `Matches`인 결과를 모은다. 전체 이름(인자 뗀 것)이 2개 이상 → `TestAmbiguous`, `테스트 이름 {name}이(가) 여러 테스트와 맞습니다: {후보들, 쉼표}`
5. 0개이고 어느 `Skipped`에 맞으면 → `TestUnverifiable`, `테스트 {name}이(가) 건너뛰도록 표시되어 있습니다`
6. 0개 → `TestNotFound`, `테스트 {name}을(를) 테스트 실행 결과에서 찾지 못했습니다 (test.projects 설정을 확인하세요)`
7. 결과 하나라도 실패 → `Red`(`FirstMessage`는 첫 실패의 `Message`). 모두 통과 → `AlreadyPassed`

규칙 1~3은 지정 테스트가 든 프로젝트를 모를 때 판정할 수 없으므로 **모든** 프로젝트에 적용한다(Claude 결정).

- [ ] **Step 1: 실패하는 테스트를 쓴다.** 각 규칙에 하나씩 쓴다.
  - `Red_BaselineNull_Unverifiable`, `Red_BuildErrors_Unverifiable`, `Red_ExitCodeOnly_Unverifiable`
  - `Red_TwoFullNames_IsAmbiguous`, `Red_Skipped_Unverifiable`, `Red_Missing_NotFound`
  - `Red_Failing_IsRed`(`FirstMessage == "Assert.Equal() Failure: Values differ"`), `Red_Passing_IsAlreadyPassed`, `Red_TheoryRows_OneFails_IsRed`
  - `Green_AllPass_Empty`, `Green_MissingFromRun_Fails`(`Detail == "not run"`), `Green_Skipped_Fails`, `Green_OneTheoryRowFails_Fails`, `Green_NullRun_Fails`
  - `BreakWarnings_FailingOrMissing_Warns`(두 경우 각각 문구 `깨질 것으로 정한 테스트 {name}이(가) 원본 상태에서 통과하지 않습니다`)
- [ ] **Step 2:** `--filter "FullyQualifiedName~TestFirstJudgeTests"` → FAIL
- [ ] **Step 3:** 구현한다.
- [ ] **Step 4:** PASS
- [ ] **Step 5: 커밋** `feat(testing): RED/GREEN verdicts for test-first verification`

### Task 4: 요청·LOCATE·APPLY — 사전 조건과 RED 확인

**Files:**
- Modify: `src/PellyCoder.Core/Fsm/RunRequest.cs`, `Fsm/Phases/LocatePhase.cs`, `Fsm/Phases/ApplyPhase.cs`, `Fsm/Transitions.cs`
- Test: `tests/PellyCoder.Core.Tests/Fsm/TestFirstFlowTests.cs` (새, `TestFlowTests`의 픽스처·`ScriptedProcessRunner`·`EnqueueTests` 방식을 따른다), `TransitionsTests.cs`

**Interfaces:**
- Consumes: Task 2 `TestMethodIndex.Find`, Task 3 `TestFirstJudge.Red`·`BreakWarnings`
- Produces:
  - `RunRequest`에 마지막 선택 인자 `IReadOnlyList<string>? VerifyTests = null, IReadOnlyList<string>? ExpectBreaks = null`, 그리고 `bool TestFirst => VerifyTests is { Count: > 0 }`
  - `sealed class TestFirstState { IReadOnlyList<string> Names; IReadOnlyList<string> Breaks; IReadOnlyDictionary<string,string> TestFileHashes; RedResult? Red; GreenState Green = NotChecked; int Regressions; int BreaksExcluded }`
    - `TestFileHashes`의 키는 선언 파일 전체 경로, 값은 SHA-256 hex다.
  - `RunContext.TestFirst`(`TestFirstState?`): 테스트 우선 실행에서 LOCATE가 만든다.

LOCATE(`StartBaselineAsync`)에서 테스트 우선 실행이면, `--dry-run`이 아닐 때 다음 순서로 검사한다. 걸리면 `ctx.Messages`에 문구를 넣고 `FailureKind`를 정하고 `PhaseOutcome.EnvError`로 끝낸다.
1. csproj 없음 → `TestUnverifiable`, `csproj가 없어 테스트 우선 검증을 할 수 없습니다`. 지금의 「구문 검사만」 대신이다.
2. 레거시 && 비Windows → `TestUnverifiable`, `레거시 테스트는 Windows에서만 실행합니다 — 테스트 우선 검증을 할 수 없습니다`
3. 이름마다 `TestMethodIndex.Find(fs, root, name)`
   - 0개 → `TestNotFound`(Task 3 규칙 6 문구)
   - 서로 다른 `FullName` 2개 이상 → `TestAmbiguous`
   - 선언 파일이 `ctx.FilePath`이고 메서드 이름이 대상 멤버 이름이면 → `TestUnverifiable`, `--verify-test가 수정 대상 멤버 자신을 가리킵니다`
   - 선언에 `Csproj`가 없으면 → `TestUnverifiable`, `테스트 {name}이(가) 어떤 테스트 프로젝트에도 속하지 않습니다`
4. 테스트 프로젝트 = `TestProjectLocator.Find` 결과 ∪ 선언들의 `Csproj`. 순서는 앞쪽 결과 다음 새 것이고, `PathComparers.ForOs`로 중복을 없앤다. 그 뒤 v1과 같이 기준 테스트를 시작한다.
   - 테스트 우선 실행이면 `RunTests`가 false여도 테스트를 돌린다.
   - v1의 「테스트 프로젝트를 찾지 못해 건너뜁니다」는 생기지 않는다. 합집합에 선언 프로젝트가 늘 있기 때문이다.
5. 선언 파일 해시를 `TestFileHashes`에 넣는다. 선언 파일이 대상 파일 자신이면(pelly가 그 파일을 고치므로) 파일 전체 대신 그 테스트 메서드 선언 텍스트의 해시를 넣는다. `--expect-break` 이름은 색인으로 찾지 않는다(결과로만 본다).

`--dry-run`이면 위를 건너뛰고 `테스트 우선 검증: {이름들} (dry-run에서는 실행하지 않음)`을 알린다.

APPLY: `AwaitBaselineTestsAsync`가 `Task<PhaseOutcome?>`를 돌려주게 바꾼다. v1 경로는 늘 null이다. 테스트 우선 실행이면 기다린 뒤 다음을 한다.
- `TestFirstJudge.Red`를 부른다.
- `Unverifiable`이면 문구·`FailureKind`를 정하고 `EnvError`로 끝낸다. 이때 v1의 「기준 테스트를 실행할 수 없음」 알림은 같은 사유로 겹쳐 내지 않는다.
- `AlreadyPassed`가 하나라도 있으면:
  - `이 테스트는 변경을 검증하지 못합니다 (원본 상태에서 이미 통과): {이름들}`을 알린다.
  - `ui.IsInteractive && !Request.Yes`이면 `ConfirmAsync("그래도 계속할까요?", false)`로 묻는다. 거부하면 `FailureKind = TestRedDeclined`, `PhaseOutcome.Rejected`다.
- `BreakWarnings`를 알린다.
- `Request.Verbose`이면 Red 테스트마다 `RED 확인: {FullName} 실패 — {FirstMessage}`를 알린다.
- `Transitions`에 `(RunPhase.Apply, PhaseOutcome.Rejected) => Fail(ExitCodes.UserCancelled)`를 더한다.

- [ ] **Step 1: 실패하는 테스트를 쓴다** (`TestFirstFlowTests`). 픽스처 `Demo.Tests/CalcTests.cs`에 `[Fact] public void Divide_ByZero_Throws()`를 둔다.
  - `VerifyTest_Red_ProceedsToEdit`: 기준 TRX에서 그 테스트가 실패하면 모델을 부르고, 파일을 쓴다(GREEN은 Task 5 몫이므로 이 테스트는 VERIFY TRX에서도 통과로 둔다).
  - `VerifyTest_AlreadyPassed_Yes_WarnsAndContinues`: `--yes`이면 경고 문구가 `_ui.Warnings`에 있고 종료 0
  - `VerifyTest_AlreadyPassed_Interactive_Declined_Exit2`: 대화형에서 확인 거부 → 종료 2, 파일 그대로, `summary.json`의 `failureKind == "TestRedDeclined"`
  - `VerifyTest_NotInIndex_Exit4_BeforeModel`: 모델 요청 0개, `TestNotFound`
  - `VerifyTest_Ambiguous_Exit4`: 두 클래스에 같은 메서드 → `TestAmbiguous`
  - `VerifyTest_TargetItself_Exit4`
  - `VerifyTest_LegacyOnMac_Exit4`(`_isWindows = () => false`), `VerifyTest_NoCsproj_Exit4`
  - `VerifyTest_ExitCodeOnlyProject_Exit4`: 테스트 프로젝트 csproj에 `<IsTestingPlatformApplication>true</IsTestingPlatformApplication>`
  - `VerifyTest_BaselineBuildFails_Exit4`: 테스트 프로젝트 빌드에 CS 오류
  - `VerifyTest_TestProjectNotReferencingTarget_IsStillRun`: 선언 프로젝트가 대상을 참조하지 않고 `test.projects`도 없어도 그 프로젝트의 테스트 명령이 나간다.
  - `VerifyTest_DryRun_NoProcesses`: 프로세스 호출 0, 알림 문구
  - `TransitionsTests`에 `(Apply, Rejected)` → 종료 2
- [ ] **Step 2:** `--filter "FullyQualifiedName~TestFirstFlowTests|FullyQualifiedName~TransitionsTests"` → FAIL
- [ ] **Step 3:** 구현한다.
- [ ] **Step 4:** 같은 필터 PASS, `TestFlowTests` 전체 PASS(v1 그대로)
- [ ] **Step 5: 커밋** `feat(fsm): --verify-test preconditions, test project union and RED check`

### Task 5: VERIFY — GREEN·회귀·`--expect-break`·테스트 파일 해시와 피드백

**Files:**
- Modify: `src/PellyCoder.Core/Verification/TestStep.cs`, `Verification/IVerifierStep.cs`, `Fsm/Phases/VerifyPhase.cs`, `Feedback/FeedbackBuilder.cs`
- Test: `tests/PellyCoder.Core.Tests/Fsm/TestFirstFlowTests.cs`, `tests/PellyCoder.Core.Tests/Feedback/FeedbackBuilderTests.cs`(있는 파일 이름을 따른다)

**Interfaces:**
- Consumes: Task 3 `TestFirstJudge.Green`, Task 4 `RunContext.TestFirst`
- Produces:
  - `StepResult.Fail(Feedback, IReadOnlyList<Diagnostic>, IReadOnlyList<Diagnostic>, string? failureKind = null)`. `VerifyPhase`는 `result.FailureKind`가 있으면 `{Name}Errors` 대신 그것을 쓴다.
  - `FeedbackBuilder.ForGreen(IReadOnlyList<GreenFailure> green, IReadOnlyList<TestCaseResult> regressions, string targetRelativePath) -> Feedback`
  - `FeedbackBuilder.GreenRequest` 상수(Global Constraints 문장)

`TestStep`에서 테스트 우선 실행일 때 v1과 다른 점:
- 기준이 null인 프로젝트를 건너뛰지 않는다(Task 4가 이미 막았으므로 오면 `InvalidOperationException`).
- 회귀 목록(`found`)에서 `ExpectBreaks` 중 하나와 `Matches`인 결과를 빼고, 뺀 개수를 `BreaksExcluded`에 둔다.
- `breaks` 테스트가 VERIFY 결과에서 여전히 통과하면 `깨질 것으로 정한 테스트가 여전히 통과합니다: {이름}`을 한 번 알린다.
- `TestFirstJudge.Green(ctx.TestFirst.Red.Tests, runs)`를 부른다. 이때 「이미 통과」 테스트도 GREEN 대상이다.
- 결과:
  - GREEN 실패가 있으면: `Fail(ForGreen(...), diagnostics, [], "TestGreenFailed")`. `Green = Failed`
  - GREEN이고 회귀가 있으면: v1 `ForTests` 피드백, `"TestRegression"`. `Green = Passed`
  - 둘 다 없으면: 해시를 확인한다. `TestFileHashes`의 파일을 다시 읽어 하나라도 다르면 `Environment("지정한 테스트 파일이 실행 중에 바뀌었습니다: {상대 경로}", "TestFileChanged")`, 아니면 Pass. `Green = Passed`
- 진단 코드는 v1처럼 `TEST`다.
- `Regressions`에는 마지막 판정의 회귀 개수를 둔다.

`ForGreen` 형식(설계 §5.5):
```
TEST NOT PASSING: {FullName}
{Detail 줄들}
  at {Type.Method} ({targetRelativePath}:{line})
…(실패 테스트마다 반복)
{회귀가 있으면 v1 ForTests 본문, 머리 줄 포함}
The test cannot be changed. Make it pass by implementing the request.
```
- 스택 줄 정규식: `^\s*at (?<m>.+?) in (?<f>.+?):line (?<l>\d+)\s*$`
  - `f`의 `\`·`/`를 맞춘 뒤 `targetRelativePath`로 끝나는 줄만 남기고, 실패 하나당 3줄까지 쓴다.
  - `m`은 인자 괄호를 떼고 마지막 두 마디(`Type.Method`)만 쓴다.
  - 비동기 상태 기계 `Ns.Type.<CancelAsync>d__3.MoveNext`는 `Type.CancelAsync`로 쓴다.
- 296 토큰을 넘으면 다음 순서로 줄인다(각 단계 뒤 `Fits`).
  1. 회귀 목록을 뒤에서부터 뺀다.
  2. GREEN 실패를 첫 하나만 남긴다.
  3. `Detail`을 줄 단위로 뒤에서 자른다.
  4. 스택을 줄인다.
  5. 마지막에는 v1 `Truncate`로 첫 줄을 자른다.
  - 머리 줄과 끝 문장은 남긴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `ForGreen_FormatsNameDetailAndTargetFrames`: 대상 파일 프레임만 나오고 테스트 파일 프레임은 없다. `Type.CancelAsync`로 줄였다.
  - `ForGreen_OverBudget_KeepsHeadAndRequest`: `Detail` 200줄 → 토큰 수 ≤ 296, 첫 줄은 `TEST NOT PASSING:`, 끝 줄은 `GreenRequest`
  - `ForGreen_WithRegressions_GreenFirst`
  - `VerifyTest_GreenFails_RetriesWithGreenFeedback_ThenPasses`: 둘째 프롬프트에 `TEST NOT PASSING: ` + 전체 이름, 종료 0
  - `VerifyTest_GreenNeverPasses_Exit1_RolledBack`: `failureKind == "TestGreenFailed"`, 파일이 원본
  - `VerifyTest_Regression_Exit1`: GREEN은 통과, 다른 테스트가 새로 실패 → `TestRegression`
  - `VerifyTest_HostCrash_IsGreenFailure`: VERIFY에서 TRX 없음 + 종료 코드 ≠ 0 → GREEN 실패 피드백
  - `ExpectBreak_ExcludesOnlyThatTest`: `breaks` 테스트만 새로 실패 → 종료 0. `breaks` + 다른 테스트 실패 → `TestRegression`
  - `ExpectBreak_StillPassing_Notice`
  - `VerifyTest_TestInTargetFile_HashIgnoresTargetEdit`: 테스트 메서드가 대상과 같은 파일에 있으면 대상 수정만으로는 `TestFileChanged`가 아니다(종료 0)
  - `VerifyTest_TestFileEditedDuringRun_Exit4`: VERIFY 테스트 실행 콜백 안에서 `CalcTests.cs`를 고친다 → `TestFileChanged`, 롤백
- [ ] **Step 2:** FAIL 확인 (`TestFirstFlowTests`, `FeedbackBuilderTests`)
- [ ] **Step 3:** 구현한다.
- [ ] **Step 4:** PASS, `dotnet test tests/PellyCoder.Core.Tests` 전체 PASS
- [ ] **Step 5: 커밋** `feat(verify): GREEN and regression verdicts, expected breaks, test file guard, GREEN feedback`

### Task 6: 실행 기록과 리포트 KPI

**Files:**
- Modify: `src/PellyCoder.Core/Logging/RunLogger.cs`, `Reporting/FailureKinds.cs`, `Reporting/RunReport.cs`, `Reporting/ReportSanitizer.cs`, `src/PellyCoder.Cli/Commands/ReportCommand.cs`
- Test: `tests/PellyCoder.Core.Tests/Reporting/RunReportTests.cs`, `ReportSanitizerTests.cs`, `FailureKindsTests.cs`, `tests/PellyCoder.Cli.Tests/ReportCommandTests.cs`, `TestFirstFlowTests`(summary 단언 하나)

**Interfaces:**
- Consumes: Task 4·5 `TestFirstState`
- Produces:
  - `sealed record TestFirstSummary(int Tests, RedState Red, GreenState Green, int Regressions, int BreaksExcluded)`
    - `Red`는 하나라도 `AlreadyPassed`면 `AlreadyPassed`, RED 판정을 했으면 `Red`, 아니면 `NotChecked`다.
  - `RunSummary`에 마지막 선택 인자 `TestFirstSummary? TestFirst = null`. 테스트 우선 실행에서만 채운다.
  - `sealed record TestFirstCounts(int Met, int Judged, int RedWarned, int RedChecked)`, `RunReportData`에 마지막 선택 인자 `TestFirstCounts? TestFirst = null`(테스트 우선 실행이 없으면 null)
  - `SanitizedRun`에 `string? Red, string? Green, int? TestRegressions`(enum 이름만), `SanitizedAggregate`에 `TestFirstCounts? TestFirst = null`

KPI 규칙(설계 §8을 실행 단위로 구체화, Claude 결정).
- `RedChecked`는 `Red != NotChecked`인 실행 수, `RedWarned`는 `Red == AlreadyPassed`인 실행 수다.
- `Judged`는 `Red == Red && ExitCode != 2`인 실행 수다.
  - RED를 통과하고 사람이 취소·거부하지 않은 실행이다. 패치 실패 등으로 GREEN까지 못 간 실행도 분모에 든다.
  - 「GREEN까지 간 것만」으로 세면, 모델이 패치를 못 만든 실행이 분모에서 빠져 KPI가 부푼다.
  - 이 문장으로 설계 §8의 「GREEN 확인까지 간 AC 중」을 고친다.
- `Met`은 `Judged` 중 `Green == Passed && Regressions == 0`인 실행 수다.
- 화면과 비식별 md에 두 줄을 더한다.
  - `| 수용 기준 충족률 | {Met/Judged %} ({Met}/{Judged}) |`
  - `| RED 확인 경고율 | {RedWarned/RedChecked %} ({RedWarned}/{RedChecked}) |`
  - 분모가 0이면 `-`다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `FailureKinds_IncludesTestFirstKinds`(7개)
  - `Build_TestFirstCounts`: 실행 다섯 개(Red/Passed/0, Red/Failed, Red/NotChecked 종료 1, AlreadyPassed/Passed, Red/Passed/회귀 1) → `Met 1, Judged 4, RedWarned 1, RedChecked 5`
  - `Build_NoTestFirstRuns_Null`
  - `Sanitize_TestFirstHasNoNames`: 원본 summary의 메시지에 테스트 이름이 있어도 JSON·md 어디에도 없다. `red`·`green` 값은 enum 이름이다.
  - `ReportCommand_ShowsAcceptanceRate`
  - `VerifyTest_Summary_HasTestFirst`: `summary.json`의 `testFirst.green == "Passed"`
- [ ] **Step 2:** FAIL 확인
- [ ] **Step 3:** 구현한다. 예전 `summary.json`(필드 없음)도 그대로 읽혀야 한다.
- [ ] **Step 4:** PASS, `dotnet test` 전체 PASS
- [ ] **Step 5: 커밋** `feat(report): test-first summary, acceptance and RED warning rates`

### Task 7: CLI 옵션, 실제 dotnet E2E, 문서

**Files:**
- Modify: `src/PellyCoder.Cli/Commands/CsCommand.cs`, `README.md`, `docs/PRD.md`(§12.4 머리에 구현 표시, FR-00 `pelly cs` 전용 옵션), `docs/superpowers/specs/2026-10-26-v2-2-test-first-design.md`(§8 분모 문장, 상태 줄)
- Test: `tests/PellyCoder.Cli.Tests/CsCommandTests.cs`, `tests/PellyCoder.Integration.Tests/E2E/TestFirstEndToEndTests.cs` (새, `M3aEndToEndTests`·`CannedLlmBackend` 방식)

**Interfaces:**
- Consumes: Task 4 `RunRequest.VerifyTests`·`ExpectBreaks`
- Produces:
  - `CsSettings.VerifyTest`(`[CommandOption("--verify-test <NAME>")] string[]?`, 설명 `패치 전 실패·패치 뒤 통과해야 하는 테스트 (여러 번 지정 가능)`)
  - `CsSettings.ExpectBreak`(`[CommandOption("--expect-break <NAME>")] string[]?`, 설명 `이 변경으로 실패해도 되는 기존 테스트 (--verify-test와 함께)`)

CLI 검사(모두 종료 4, 서버 호출 전):
- 이름이 `TestNames.IsValid`가 아님 → `테스트 이름 형식이 아닙니다: {값}`
- `--expect-break`만 있음 → `--expect-break는 --verify-test와 함께 써야 합니다`
- 같은 이름이 두 옵션에 다 있음 → `같은 테스트를 --verify-test와 --expect-break에 함께 줄 수 없습니다: {값}`

중복 이름은 한 번만 넘긴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `CsCommandTests`: 위 세 거절, 그리고 정상 값이 `RunRequest.VerifyTests`·`ExpectBreaks`로 넘어가는지
  - `TestFirstEndToEndTests.ShippedCancel_RedThenGreen`(SDK, macOS에서도 돎):
    - 코퍼스 `Modern` 사본을 쓴다.
    - `Shop.Api.Tests`에 `[Fact] CancelAsync_WhenShipped_Throws`(Shipped 주문 취소 시 `InvalidOperationException`)를 넣는다.
    - `CannedLlmBackend`가 `OrderService.CancelAsync`에 Shipped 예외를 넣는 블록을 준다.
    - `--verify-test OrderServiceTests.CancelAsync_WhenShipped_Throws --expect-break OrderServiceTests.CancelAsync_WhenShipped_CurrentlyCancels --yes`
    - 기대: 종료 0, `summary.json`의 `testFirst.red == "Red"`, `green == "Passed"`, `breaksExcluded == 1`
  - `TestFirstEndToEndTests.WrongEdit_GreenFails_Exit1`: 캔드 응답이 예외 없이 로그만 넣음(재시도 0) → 종료 1, `TestGreenFailed`, 파일 원본
- [ ] **Step 2:** `dotnet test tests/PellyCoder.Cli.Tests --filter "FullyQualifiedName~CsCommandTests"`, `dotnet test tests/PellyCoder.Integration.Tests --filter "FullyQualifiedName~TestFirstEndToEndTests"` → FAIL
- [ ] **Step 3:** 구현한다.
  - README `pelly cs` 절에 쓸 것:
    - `--verify-test`·`--expect-break` 사용법
    - 종료 4 사유 목록
    - v1 `--test`와 다른 점: 판정 불가 시 빌드로 내려가지 않는다.
    - 알려진 한계(설계 §10)
  - PRD §12.4 머리에 `> **구현(V2-2a): <날짜>.** …`(V2-1a 표시와 같은 모양)을 붙인다.
    - 필터 대신 전체 실행이라는 점을 적는다.
    - `vstest.console` 대신 `test.command`라는 점을 적는다.
- [ ] **Step 4:** 위 필터 PASS, 그다음 `dotnet build -warnaserror`(종료 0)와 `dotnet test`(실패 0) 전체
- [ ] **Step 5: 커밋** `feat(cli): pelly cs --verify-test/--expect-break, E2E, docs`
