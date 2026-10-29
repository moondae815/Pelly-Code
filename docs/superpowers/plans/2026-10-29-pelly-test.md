# `pelly test` (현재 동작 고정 테스트) 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `pelly test <FILE> --method <NAME>`이 C# 메서드 하나의 현재 동작을 고정하는 테스트 1~5개를 모델로 써서 기존 테스트 프로젝트에 넣고, 형태·호출·단언·환경 의존·빌드·현재 통과·재실행 일치·변이 검출로 판정한 뒤 사람이 승인하면 남긴다.

**Architecture:** Core에 `CharTests/` 폴더를 새로 두고 부품(프레임워크 표, 테스트 클래스 찾기·쓰기, 출력 파서, 정적 판정, 변이, 미완료 표시, 프롬프트, 실행 판정)을 나눈다. `CharTestOrchestrator`가 기존 `PreflightPhase`·`LocatePhase`를 그대로 불러 대상·작업 공간·테스트 프로젝트·원본 테스트 실행을 얻고, 그 뒤 시도 루프·검토·되돌리기·기록을 직접 한다. 기존 `FsmOrchestrator`와 `cs` 경로는 바꾸지 않는다(미완료 표시 확인과 additive 확장만).

**Tech Stack:** .NET 10, Roslyn, Spectre.Console.Cli, xUnit v3, Ollama(로컬 예비 측정).

**Spec:** `docs/superpowers/specs/2026-10-29-pelly-test-characterization-design.md`

## Global Constraints

- 작업 위치는 워크트리 `/Users/payletter/git-root/Pelly-Code-char-test`(브랜치 `char-test`). 셸 명령은 `cd /Users/payletter/git-root/Pelly-Code-char-test && …`. 메인 체크아웃에서 빌드·테스트하지 않는다.
- 게이트: `dotnet build -warnaserror` 종료 0, `dotnet test` 실패 0(건너뜀은 SQL 서버·로컬 모델·Windows 전용만).
- 운영 코드(대상 파일)는 실행 시작과 끝에 바이트가 같아야 한다. 변이 뒤 복원은 `finally`에서, 취소 토큰을 무시하고 한다.
- 거부·실패·취소 뒤 테스트 파일·csproj는 원래대로(새로 만든 파일은 삭제). 미완료 표시는 모든 파일을 남기거나 되돌린 뒤에만 지운다.
- 테스트별 결과(TRX)를 읽을 수 없는 실행 방식(`TestProjectKind.OtherTestingPlatform`, `{trx}` 없는 레거시 `test.command`)은 종료 4. 「판정 불가」를 성공으로 치지 않는다.
- 예산은 기존 `budget` 그대로: 입력 4,096, 피드백 296, 출력 2,048, `num_ctx` 8,192. 대상 본문이 혼자 예산을 넘으면 모델을 부르지 않고 종료 3.
- `--count` 1~5, 기본 3. `[HINT]` 200토큰 이내(`budget.Request`), 넘으면 종료 4.
- 종료 코드: 0 남김, 1 재시도 소진, 2 거부·취소, 3 대상·테스트 프로젝트·테스트 클래스 문제, 4 환경(원본 테스트 판정 불가, 변이 빌드 실패, 미완료 표시, 실행 방식, 프레임워크 판별 실패).
- 새 실패 유형 문자열(그대로): `CharTestShape`, `CharTestNoAssert`, `CharTestEnvDependent`, `CharTestBuild`, `CharTestFailsOnCurrent`, `CharTestRegression`, `CharTestFlaky`, `CharTestSurvivesMutation`, `CharTestMutationUnbuildable`, `CharTestBaselineUnusable`, `CharTestPending`.
- 미완료 표시 파일 이름 `chartest.pending`(`.pelly/runs/<runId>/` 아래), 변이 문장 `throw new System.NotImplementedException("pelly-mutation");`, async는 앞에 `await System.Threading.Tasks.Task.Yield();`.
- 프롬프트 버전 문자열 `cs-test-v1`.
- `HttpClient` 생성은 `src/PellyCoder.Cli/Composition.cs`에만(`NetworkPolicyTests`).
- 사용자 문구 한국어, 코드 식별자·주석 영어. 커밋 끝 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. push하지 않는다.

## Review Focus

1. **테스트 파일이 CRLF·BOM·탭 들여쓰기인 레거시 파일** — 넣은 멤버가 파일의 줄바꿈·인코딩·들여쓰기를 따르고, 되돌린 뒤 바이트가 원본과 같다. 테스트: Task 3 `Insert_PreservesCrlfBomAndTabs`, Task 9 `Reject_RestoresTestFileBytes`.
2. **모델이 기존 테스트와 같은 이름의 메서드를 냄**(코퍼스 대상엔 이미 테스트가 있다) — 판정 1에서 이름 충돌 피드백, 파일은 쓰지 않음. 테스트: Task 4 `Shape_NameCollision_Rejected`.
3. **변이 중 Ctrl+C** — 운영 파일이 원래 바이트로 돌아오고 미완료 표시가 지워진다. 테스트: Task 9 `CancelDuringMutation_RestoresTarget`.
4. **원본 상태에서 이미 실패하는 기존 테스트가 있음** — 그것만으로 멈추지 않고, 새 실패만 회귀로 본다. 테스트: Task 8 `BaselineFailures_AreNotRegressions`.
5. **대상이 `async Task`이고 본문이 `=>` 식** — 변이가 빌드되는 형태로 만들어진다(`async` 식 본문은 블록으로 바꾼다). 테스트: Task 5 `Mutate_AsyncExpressionBody_BlockWithYieldThrow`.

---

## 파일 구조

| 파일 | 책임 |
| :--- | :--- |
| `src/PellyCoder.Core/CharTests/CharTestRequest.cs` | 요청 record와 결과 상수 |
| `src/PellyCoder.Core/CharTests/TestFramework.cs` | 프레임워크 enum, 판별기, 프레임워크 표 |
| `src/PellyCoder.Core/CharTests/TestClassLocator.cs` | `<타입>Tests` 클래스 찾기, 새 파일 경로·네임스페이스 |
| `src/PellyCoder.Core/CharTests/TestClassWriter.cs` | 멤버·using 넣기, 새 파일 뼈대, 레거시 csproj `Compile Include` |
| `src/PellyCoder.Core/CharTests/CharTestOutputParser.cs` | `### USINGS`·`### MEMBERS` 파싱 |
| `src/PellyCoder.Core/CharTests/CharTestStaticJudge.cs` | 판정 1~3 |
| `src/PellyCoder.Core/CharTests/CharTestTarget.cs` | 대상 자격 검사(§4.1), 변이 만들기 |
| `src/PellyCoder.Core/CharTests/CharTestPending.cs` | 미완료 표시 쓰기·갱신·찾기 |
| `src/PellyCoder.Core/CharTests/DependencySignatureCollector.cs` | 생성자 매개변수·필드 타입의 인터페이스·추상 멤버 서명 |
| `src/PellyCoder.Core/CharTests/CharTestPromptBuilder.cs` | `cs-test` 프롬프트 |
| `src/PellyCoder.Core/Llm/Resources/prompts/system.cs-test.txt`, `fewshot.cs-test.txt` | 프롬프트 문구 |
| `src/PellyCoder.Core/CharTests/CharTestFeedback.cs` | 판정별 피드백(예산 안) |
| `src/PellyCoder.Core/CharTests/CharTestRunJudge.cs` | 판정 4~7(테스트 실행 기반) |
| `src/PellyCoder.Core/CharTests/CharTestOrchestrator.cs` | 전체 흐름 |
| `src/PellyCoder.Cli/Commands/TestCommand.cs` | CLI |
| 수정: `Models/EditModels.cs`, `Llm/QwenStreamClient.cs`, `Llm/PromptResources.cs`, `Fsm/RunContext`(RunRequest.cs), `Logging/RunLogger.cs`, `Reporting/*`, `Fsm/Phases/PreflightPhase.cs`, `Specs/SpecRunner.cs`(apply 시작), `Cli/CliApp.cs`, `Cli/Runners.cs`, `Cli/Composition.cs`, PowerShell 자동완성 | additive 확장 |

---

### Task 1: 기반 — 종류·요청·실패 유형·응답 접두

**Files:**
- Modify: `src/PellyCoder.Core/Models/EditModels.cs` (`EditKind`에 `CSharpTest` 추가)
- Modify: `src/PellyCoder.Core/Llm/QwenStreamClient.cs:48` (선택 인자 `answerPrefix`)
- Modify: `src/PellyCoder.Core/Llm/PromptResources.cs:37` (`VersionFor(CSharpTest)` → `"cs-test-v1"`)
- Modify: `src/PellyCoder.Core/Reporting/FailureKinds.cs` (Global Constraints의 11개 상수)
- Create: `src/PellyCoder.Core/CharTests/CharTestRequest.cs`
- Test: `tests/PellyCoder.Core.Tests/Llm/QwenStreamClientTests.cs`(기존 파일에 추가), `tests/PellyCoder.Core.Tests/CharTests/CharTestRequestTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record CharTestRequest(string FilePath, string? ClassName, string? MethodName, int? Line, string? Hint, int Count, string? TestProject, bool Yes, bool DryRun, int? MaxRetries, bool Verbose)` + `public const int MinCount = 1, MaxCount = 5, DefaultCount = 3;` (같은 파일의 `static class CharTestLimits`)
  - `GenerateEditAsync(string basePrompt, int attempt, CancellationToken ct, Action<TimeSpan>? onWaiting = null, string? answerPrefix = null)` — null이면 지금의 `"<<<<<<< SEARCH\n"`. 반환 `Output`은 접두 + 생성문.
  - `FailureKinds.CharTestShape` … `FailureKinds.CharTestPending`

- [ ] **Step 1: 실패 테스트** — `GenerateEdit_DefaultPrefix_IsSearchMarker`(기존 동작: 요청 `RawPrompt`가 `"<<<<<<< SEARCH\n"`로 끝나고 `Output`이 그것으로 시작), `GenerateEdit_CustomPrefix_UsedInPromptAndOutput`(`answerPrefix: "### USINGS\n"` → `RawPrompt.EndsWith("### USINGS\n")`, `Output.StartsWith("### USINGS\n")`). `ScriptedLlmBackend` 사용.
- [ ] **Step 2:** `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*QwenStreamClientTests"` → 새 테스트 컴파일 실패.
- [ ] **Step 3:** 구현. `CharTestRequest`는 데이터만. `PromptResources.VersionFor`는 `switch`로.
- [ ] **Step 4:** 같은 명령 → 통과. 전체 `dotnet build -warnaserror` 종료 0(`EditKind` switch 경고 확인 — 새 값이 들어간 switch가 있으면 `CSharpTest`를 `CSharp`과 같이 다룬다).
- [ ] **Step 5:** 커밋 `feat(chartest): EditKind.CSharpTest, request record, failure kinds, answer prefix`

### Task 2: 테스트 프레임워크 판별과 표

**Files:**
- Create: `src/PellyCoder.Core/CharTests/TestFramework.cs`
- Test: `tests/PellyCoder.Core.Tests/CharTests/TestFrameworkTests.cs`

**Interfaces:**
- Produces:
  - `public enum TestFramework { XunitV2, XunitV3, MsTest, NUnit }`
  - `public sealed record FrameworkInfo(TestFramework Framework, string TestAttribute, string? ClassAttribute, IReadOnlyList<string> Usings, IReadOnlyList<string> SkipMarkers, IReadOnlyList<string> ThrowsAssertions, string ExampleTest)` — `TestAttribute`: `Fact`/`Fact`/`TestMethod`/`Test`; `ClassAttribute`: null/null/`TestClass`/`TestFixture`; `Usings`: `Xunit` / `Xunit` / `Microsoft.VisualStudio.TestTools.UnitTesting` / `NUnit.Framework`; `SkipMarkers`: xUnit `Skip =`, MSTest `Ignore`·`Assert.Inconclusive`, NUnit `Ignore`·`Assert.Inconclusive`·`Assert.Ignore`; `ThrowsAssertions`: xUnit `Assert.Throws`·`Assert.ThrowsAsync`·`Assert.ThrowsAny`·`Assert.ThrowsAnyAsync`, MSTest `Assert.ThrowsException`·`Assert.ThrowsExceptionAsync`·`ExpectedException`, NUnit `Assert.Throws`·`Assert.ThrowsAsync`·`Assert.That`.
  - `public static class TestFrameworks { FrameworkInfo Info(TestFramework f); (TestFramework? Framework, string? Error) Detect(IFileSystem fs, string testCsproj); }` — SDK `PackageReference Include`와 같은 폴더 `packages.config`의 `package id`를 본다: `xunit.v3*` → V3, `xunit`(정확히)·`xunit.core` → V2, `MSTest.TestFramework` → MsTest, `NUnit` → NUnit. 둘 이상의 프레임워크 → 오류 「테스트 프레임워크가 여럿입니다: …」, 없음 → 「테스트 프레임워크를 찾을 수 없습니다」.

- [ ] **Step 1: 실패 테스트** — `Detect_ModernCorpus_IsXunitV3`(`eval/corpus/Modern/Shop.Api.Tests/Shop.Api.Tests.csproj`, `CorpusLocation` 사용), `Detect_LegacyCorpus_IsMsTestFromPackagesConfig`, `Detect_XunitV2PackageReference`, `Detect_NUnit`, `Detect_Two_Error`, `Detect_None_Error`(가짜 파일 시스템 `MockFileSystem`), `Info_MsTest_HasTestClassAttribute`.
- [ ] **Step 2:** `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*TestFrameworkTests"` → 컴파일 실패.
- [ ] **Step 3:** 구현(XML은 `XDocument`, 네임스페이스 무시하고 `LocalName`으로).
- [ ] **Step 4:** 같은 명령 → 7/7 통과.
- [ ] **Step 5:** 커밋 `feat(chartest): test framework detection and table`

### Task 3: 테스트 클래스 찾기와 쓰기

**Files:**
- Create: `src/PellyCoder.Core/CharTests/TestClassLocator.cs`, `src/PellyCoder.Core/CharTests/TestClassWriter.cs`
- Test: `tests/PellyCoder.Core.Tests/CharTests/TestClassLocatorTests.cs`, `TestClassWriterTests.cs`

**Interfaces:**
- Consumes: Task 2 `FrameworkInfo`.
- Produces:
  - `public sealed record TestClassLocation(string FilePath, string ClassName, bool IsNew, string Namespace, string? Error)` — `Error`가 있으면 종료 3.
  - `TestClassLocator.Find(IFileSystem fs, string testCsproj, string targetTypeName, LanguageVersion version) -> TestClassLocation` — 테스트 프로젝트 폴더 아래 `.cs`(bin·obj 제외, `BoundedWalker` 재사용)에서 이름 `<타입>Tests` 클래스: 하나면 그 파일(partial이면 파일 이름 `<타입>Tests.cs`인 부분, 없으면 첫 부분), 없으면 새 파일 `<테스트 프로젝트 폴더>/<타입>Tests.cs`, 그 경로에 파일이 이미 있는데 클래스가 다르면 `Error = "같은 이름의 파일이 있지만 <타입>Tests 클래스가 아닙니다: <경로>"`. 네임스페이스: 기존 파일에서 가장 많이 쓴 것 → `<RootNamespace>` → 어셈블리 이름(`AssemblyName` 또는 csproj 파일 이름).
  - `TestClassWriter.Insert(string fileText, string className, IReadOnlyList<string> usings, IReadOnlyList<string> members) -> string` — `fileText`는 원래 줄바꿈 그대로 받는다. 클래스 닫는 중괄호 앞에 빈 줄 하나 + 멤버(클래스 멤버 들여쓰기 = 기존 첫 멤버의 들여쓰기, 없으면 클래스 들여쓰기 + 4칸 또는 탭이면 탭 하나), using은 기존 using 묶음 끝에 없는 것만. 결과는 원래 줄바꿈(CRLF/LF)으로. BOM은 호출자가 `FileGuard`로 보존.
  - `TestClassWriter.NewFile(string ns, string className, FrameworkInfo fw) -> string` — LF. file-scoped가 아닌 블록 네임스페이스(레거시 C# 7.3 호환), 클래스는 `public`, `ClassAttribute`가 있으면 붙임.
  - `TestClassWriter.AddCompileInclude(string csprojText, string relativePath) -> string?` — 비 SDK csproj에서 마지막 `<Compile Include=` 줄 다음에 같은 들여쓰기·줄바꿈으로 `<Compile Include="<relativePath>" />`. SDK csproj(루트에 `Sdk=` 속성)면 null(바꿀 필요 없음). `Compile Include`가 하나도 없는 비 SDK면 `</Project>` 앞에 `<ItemGroup>` 하나를 새로.

- [ ] **Step 1: 실패 테스트** — Locator: `Find_ModernOrderService_ExistingClass`(코퍼스 `OrderServiceTests.cs`, `IsNew=false`), `Find_NoClass_NewFileWithMajorityNamespace`, `Find_FileExistsWithOtherClass_Error`, `Find_SkipsBinObj`. Writer: `Insert_AppendsBeforeClosingBrace`, `Insert_AddsOnlyMissingUsings`, `Insert_PreservesCrlfBomAndTabs`(CRLF·탭 입력 → 출력 줄바꿈 전부 CRLF, 새 멤버 줄 앞이 탭), `NewFile_MsTest_HasTestClassAndBlockNamespace`, `AddCompileInclude_LegacyCorpus_AfterLastCompile`(Legacy `Shop.Tests.csproj` 텍스트 → `OrderServiceTests.cs` 줄 뒤에 새 줄, 나머지 바이트 동일), `AddCompileInclude_Sdk_ReturnsNull`.
- [ ] **Step 2:** `--filter-class "*TestClassLocatorTests"`, `"*TestClassWriterTests"` → 컴파일 실패.
- [ ] **Step 3:** 구현(Roslyn으로 클래스 위치·들여쓰기, 텍스트 삽입은 문자열 연산).
- [ ] **Step 4:** 두 명령 → 통과.
- [ ] **Step 5:** 커밋 `feat(chartest): locate and write the test class`

### Task 4: 출력 파서와 정적 판정(판정 1~3)

**Files:**
- Create: `src/PellyCoder.Core/CharTests/CharTestOutputParser.cs`, `src/PellyCoder.Core/CharTests/CharTestStaticJudge.cs`
- Test: `tests/PellyCoder.Core.Tests/CharTests/CharTestOutputParserTests.cs`, `CharTestStaticJudgeTests.cs`

**Interfaces:**
- Consumes: Task 2 `FrameworkInfo`.
- Produces:
  - `public sealed record CharTestOutput(IReadOnlyList<string> Usings, IReadOnlyList<string> Members)` — `Members`는 멤버별 원문.
  - `CharTestOutputParser.Parse(string output, LanguageVersion version) -> (CharTestOutput? Output, string? Error)` — 출력은 `### USINGS\n`로 시작(접두). `### MEMBERS` 줄이 없으면 오류. 코드 펜스(```` ``` ````) 줄은 버린다. USINGS 구역의 줄은 `using X;` 모양만 받고 나머지는 오류. MEMBERS는 Roslyn `ParseCompilationUnit("class __P {\n" + 본문 + "\n}")`로 파싱, 구문 오류면 오류(첫 오류 메시지).
  - `public sealed record CharTestRejection(string Kind, string? TestName, string Reason)` — `Kind`는 `FailureKinds.CharTestShape`/`CharTestNoAssert`/`CharTestEnvDependent`.
  - `CharTestStaticJudge.Judge(CharTestOutput output, FrameworkInfo fw, int count, string targetMethodName, IReadOnlySet<string> existingMemberNames, LanguageVersion version) -> IReadOnlyList<CharTestRejection>` — 설계 §4.4 판정 1~3 그대로:
    - 1: 테스트 메서드(특성 이름이 `fw.TestAttribute` 또는 `…Attribute`, `Theory`/`TestCase`는 xUnit·NUnit에서 테스트로 셈) 수 1~`count`, 그 밖에는 `private` 메서드·`private` 중첩 클래스만, 이름이 `existingMemberNames`·서로와 겹치지 않음, `fw.SkipMarkers` 없음.
    - 2: 각 테스트 본문(또는 같은 클래스에 새로 넣는 private 도우미 한 단계)에 `targetMethodName` 이름의 호출이 있음. 단언: `Assert.`로 시작하는 호출 또는 `fw.ThrowsAssertions`의 특성·호출 하나 이상, 그리고 단언 호출 중 적어도 하나는 인자가 모두 리터럴이 아님(`ExpectedException` 특성은 그 자체로 인정).
    - 3: 새 코드(테스트·도우미·중첩 클래스 전부)에 `DateTime.Now`, `DateTime.UtcNow`, `DateTime.Today`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow`, `Guid.NewGuid`, `new Random`, `Random.Shared`, `Thread.Sleep`, `Task.Delay`, `File.`, `Directory.`, `SqlConnection`, `HttpClient`, `Environment.` 토큰(식별자 기준 — 문자열·주석은 무시).
- [ ] **Step 1: 실패 테스트** — Parser: `Parse_UsingsAndMembers`, `Parse_NoUsings_Ok`, `Parse_MissingMembersHeader_Error`, `Parse_StripsCodeFences`, `Parse_SyntaxError_Error`. Judge: `Shape_TooMany_Rejected`(count 3, 테스트 4개), `Shape_PublicHelper_Rejected`, `Shape_NameCollision_Rejected`(existing에 `CalculateRefundAsync_PaidOrder_RefundsItemsAndShippingFee`), `Shape_Skip_Rejected`(`[Fact(Skip = "x")]`), `Assert_None_Rejected`, `Assert_AllLiteral_Rejected`(`Assert.True(true)`만), `Assert_ThrowsCounts`, `Call_ViaPrivateHelper_Ok`, `Call_Missing_Rejected`, `Env_DateTimeNow_Rejected`, `Env_InStringLiteral_Ignored`, `Valid_MsTest_ExpectedException_Ok`.
- [ ] **Step 2:** `--filter-class "*CharTestOutputParserTests"`, `"*CharTestStaticJudgeTests"` → 컴파일 실패.
- [ ] **Step 3:** 구현.
- [ ] **Step 4:** 통과.
- [ ] **Step 5:** 커밋 `feat(chartest): output parser and static verdicts 1-3`

### Task 5: 대상 자격과 변이

**Files:**
- Create: `src/PellyCoder.Core/CharTests/CharTestTarget.cs`
- Test: `tests/PellyCoder.Core.Tests/CharTests/CharTestTargetTests.cs`

**Interfaces:**
- Consumes: `MemberCandidate`(`CSharp/MemberLocator.cs:12`).
- Produces:
  - `CharTestTarget.Check(string sourceLf, MemberCandidate target, LanguageVersion version, string? internalsVisibleToAssembly) -> (string MethodName, string TypeName, string? Error)` — 메서드가 아니면, 접근성이 public이 아니고 (internal이면서 `internalsVisibleToAssembly`가 null), 포함 타입이 abstract·제네릭, 반복기(`yield`), `extern`, 본문 없음 → 설계 §4.1 문구로 `Error`. `TypeName`은 가장 안쪽 포함 타입 이름.
  - `CharTestTarget.HasInternalsVisibleTo(IFileSystem fs, string productionCsproj, string testAssemblyName) -> bool` — 운영 프로젝트 폴더의 `.cs`에서 `[assembly: InternalsVisibleTo("<이름>")]`(공개 키 접미 허용)·SDK csproj의 `<InternalsVisibleTo Include="<이름>" />`.
  - `CharTestTarget.Mutate(string sourceLf, MemberCandidate target, LanguageVersion version) -> string` — 본문을 Global Constraints의 문장으로 바꾼 전체 텍스트(LF). 블록 본문 → `{ <문장> }`, 식 본문(비 async) → `=> throw new System.NotImplementedException("pelly-mutation");`, async(블록·식 모두) → `{ await System.Threading.Tasks.Task.Yield(); throw …; }`. 서명·특성·주석은 그대로.
- [ ] **Step 1: 실패 테스트** — `Check_PublicMethod_Ok`, `Check_Property_Error`, `Check_Private_Error`, `Check_InternalWithIvt_Ok`, `Check_GenericType_Error`, `Check_Iterator_Error`, `Mutate_Block`, `Mutate_ExpressionBody`, `Mutate_AsyncBlock_YieldThenThrow`, `Mutate_AsyncExpressionBody_BlockWithYieldThrow`, `Mutate_OnlyTargetChanged`(다른 멤버 텍스트 동일), `Mutate_ModernCalculateRefund_Compiles`(코퍼스 `OrderService.cs`를 변이 → Roslyn `CSharpCompilation`에 net 참조 없이 구문 진단 0개).
- [ ] **Step 2:** `--filter-class "*CharTestTargetTests"` → 컴파일 실패.
- [ ] **Step 3:** 구현(Roslyn `SyntaxNode.ReplaceNode`, 원래 들여쓰기는 `NormalizeWhitespace` 없이 trivia 유지).
- [ ] **Step 4:** 통과.
- [ ] **Step 5:** 커밋 `feat(chartest): target eligibility and mutation`

### Task 6: 미완료 표시와 다른 명령의 확인

**Files:**
- Create: `src/PellyCoder.Core/CharTests/CharTestPending.cs`
- Modify: `src/PellyCoder.Core/Fsm/Phases/PreflightPhase.cs`(작업 공간을 정한 직후, 잠금 뒤), `src/PellyCoder.Core/Specs/SpecRunner.cs`(apply가 작업 공간 잠금을 잡은 직후)
- Test: `tests/PellyCoder.Core.Tests/CharTests/CharTestPendingTests.cs`, `tests/PellyCoder.Core.Tests/Fsm/PreflightPhaseTests.cs`(추가), apply 쪽은 기존 `SpecRunner` 테스트 파일에 추가

**Interfaces:**
- Produces:
  - `public sealed record PendingFile(string Path, string Role, bool CreatedNew, string? BackupPath, string? OriginalSha256, bool Mutated)` — `Role`: `"test"`·`"csproj"`·`"target"`.
  - `public sealed class CharTestPending(IFileSystem fs)`: `string Write(Workspace ws, string runId, IReadOnlyList<PendingFile> files)`(경로 반환, 임시 파일 쓰고 이동), `void Delete(string path)`, `IReadOnlyList<(string MarkerPath, IReadOnlyList<PendingFile> Files)> FindAll(Workspace ws)`(`.pelly/runs/*/chartest.pending`, 읽을 수 없는 파일은 파일 목록 없이 포함).
  - `public static string Describe(IReadOnlyList<(string MarkerPath, IReadOnlyList<PendingFile> Files)> found)` — 「이전 `pelly test` 실행이 끝나지 않았습니다: <파일 목록> — 백업 <경로들>. 확인한 뒤 <표시 경로>를 지우세요」.
  - Preflight: 표시가 하나라도 있으면 `ctx.FailureKind = FailureKinds.CharTestPending`, 메시지 `Describe(...)`, `PhaseOutcome.EnvError`(종료 4). 표시가 없으면 지금과 똑같다.
- [ ] **Step 1: 실패 테스트** — `WriteThenFind_RoundTrips`, `Delete_RemovesMarker`, `Find_Unreadable_StillReported`, `Preflight_PendingMarker_EnvError`, `Preflight_NoMarker_Unchanged`(기존 Preflight 테스트 하나와 같은 결과), `Apply_PendingMarker_Exit4_NoTaskRun`.
- [ ] **Step 2:** 해당 클래스 필터로 실행 → 실패.
- [ ] **Step 3:** 구현.
- [ ] **Step 4:** 통과 + `dotnet test tests/PellyCoder.Core.Tests` 전체 실패 0.
- [ ] **Step 5:** 커밋 `feat(chartest): pending marker blocks cs, sql and apply`

### Task 7: 프롬프트(의존성 서명 수집 포함)

**Files:**
- Create: `src/PellyCoder.Core/CharTests/DependencySignatureCollector.cs`, `src/PellyCoder.Core/CharTests/CharTestPromptBuilder.cs`, `src/PellyCoder.Core/Llm/Resources/prompts/system.cs-test.txt`, `fewshot.cs-test.txt`
- Modify: `src/PellyCoder.Core/Llm/PromptResources.cs`(두 리소스 로드)
- Test: `tests/PellyCoder.Core.Tests/CharTests/DependencySignatureCollectorTests.cs`, `CharTestPromptBuilderTests.cs`

**Interfaces:**
- Consumes: `DtoCollector.Collect(fs, searchRoot, sourceLf, target, version, DtoLimits?)`, `QwenTokenCounter.Shared`, `QwenChatTemplate`(기존 `PromptBuilder`가 시스템·사용자 메시지를 감싸는 방식 그대로), `FrameworkInfo`, `PromptBuildResult(BasePrompt, Tokens, Error, Detail, Rules)`.
- Produces:
  - `DependencySignatureCollector.Collect(IFileSystem fs, string searchRoot, string sourceLf, MemberCandidate target, LanguageVersion version, DtoLimits? limits = null) -> string?` — 포함 타입의 생성자 매개변수 타입과 대상이 쓰는 필드·속성 타입 중 작업 공간에 선언된 interface·abstract class의 멤버 서명(타입마다 `interface IOrderRepository { Task<Order> GetByIdAsync(int orderId); … }` 한 덩어리, 본문 없음). 한도는 `DtoLimits` 재사용(타입 5, 멤버 30).
  - `public sealed record CharTestPromptInput(string TargetSource, string TargetDisplay, string TypeName, string Namespace, string Construction, string? TestHelpers, string? Dependencies, string? Dto, string? ExampleTest, IReadOnlyList<string> TestUsings, FrameworkInfo Framework, int Count, string? Hint, Feedback? Feedback)`
  - `CharTestPromptBuilder(ITokenCounter tokens, BudgetOptions budget, QwenChatTemplate template, PromptResources resources).Build(CharTestPromptInput input) -> PromptBuildResult` — 부분 순서와 빼는 순서는 설계 §4.3(번호 1·2는 빼지 않음; 넘치면 7, 6, 5, 4, 3 순으로 뺌; 1·2만으로 `budget.Context`를 넘으면 `PromptBudgetError`로 반환). REQUEST 문장(영어, 시스템 프롬프트와 같은 언어): `Write {Count} tests that pin the CURRENT behavior of the target. Assert what it does now, even if it looks wrong.` + 힌트가 있으면 `\nFocus: {Hint}`. FEEDBACK이 있으면 기존 형식(`### FEEDBACK`)으로 끝에.
  - 시스템 프롬프트(`system.cs-test.txt`) 규칙: 출력은 `### USINGS`(있으면 using 줄)와 `### MEMBERS`(C# 멤버)만, 테스트 특성 `{TestAttribute}`, public 테스트 메서드와 private 도우미·private 중첩 클래스만, 실제 시간·난수·파일·DB·네트워크 금지(가짜 객체 사용), 각 테스트는 대상을 부르고 결과나 효과를 단언, 이름은 `<Method>_<Condition>_<Result>`, C# 언어 버전 `{LangVersion}`. 문구 세부는 빌더 몫.
- [ ] **Step 1: 실패 테스트** — `Dependencies_ModernOrderService_IncludesIOrderRepository`(코퍼스, `GetByIdAsync` 서명 포함, 본문 없음), `Dependencies_NoProjectInterfaces_Null`, `Build_ContainsPartsInOrder`(`[READ-ONLY: target]` < `[READ-ONLY: construction]` < … 순서), `Build_OverBudget_DropsExampleThenUsingsThenDto`, `Build_TargetAloneTooLarge_BudgetError`, `Build_HintAppended`, `Build_FeedbackAppended`, `Build_TokensWithinInputTotal`(Modern `CalculateRefundAsync` 실제 입력 ≤ 4096).
- [ ] **Step 2:** 필터 실행 → 실패.
- [ ] **Step 3:** 구현.
- [ ] **Step 4:** 통과.
- [ ] **Step 5:** 커밋 `feat(chartest): prompt with dependency signatures`

### Task 8: 실행 판정(판정 4~7)과 피드백

**Files:**
- Create: `src/PellyCoder.Core/CharTests/CharTestRunJudge.cs`, `src/PellyCoder.Core/CharTests/CharTestFeedback.cs`
- Test: `tests/PellyCoder.Core.Tests/CharTests/CharTestRunJudgeTests.cs`, `CharTestFeedbackTests.cs`

**Interfaces:**
- Consumes: `TestRun`(`Testing/TestRunner.cs:17`), `TestRegressions.Find(before, after, excluded)`, `TestCaseResult(FullName, Passed, Message, …)`, `TestNames.Matches`, `FeedbackBuilder.ForBuild`, `Feedback(Text, Tokens)`.
- Produces:
  - `public sealed record RunVerdict(string? FailureKind, IReadOnlyList<string> FailingTests, IReadOnlyList<TestCaseResult> Details)` — `FailureKind` null이면 통과.
  - `CharTestRunJudge.OnCurrent(TestRun baseline, TestRun after, IReadOnlyList<string> newTestFullNames, string testFileFullPath) -> RunVerdict` — 판정 4·5: `after.BuildErrors` 중 테스트 파일 오류 → `CharTestBuild`, 다른 파일 오류 → `CharTestBuild`가 아니라 `FailureKind = "BuildUnverifiable"`(호출자가 종료 4로), 새 테스트 중 결과 없음·실패 → `CharTestFailsOnCurrent`, 기존 테스트 새 실패(`TestRegressions.Find(baseline, after, newTestFullNames)`) → `CharTestRegression`.
  - `CharTestRunJudge.Stable(TestRun first, TestRun second, IReadOnlyList<string> newTestFullNames) -> RunVerdict` — 판정 6: 새 테스트별 통과 여부가 다르면 `CharTestFlaky`.
  - `CharTestRunJudge.UnderMutation(TestRun mutated, IReadOnlyList<string> newTestFullNames) -> RunVerdict` — 판정 7: 빌드 오류 → `CharTestMutationUnbuildable`, 새 테스트 중 통과한 것 → `CharTestSurvivesMutation`(그 이름들), 결과 없음(실행 중단)도 `CharTestMutationUnbuildable`.
  - `CharTestRunJudge.BaselineUsable(TestRun baseline) -> string?` — 빌드 오류·`ReportMissing`·`RunAborted`·`TimedOut`·`Error` 중 하나면 이유 문구(→ `CharTestBaselineUnusable`, 종료 4). 기존 테스트 실패만 있으면 null.
  - `CharTestFeedback(FeedbackBuilder builder, ITokenCounter tokens, int budget)`: `ForRejections(IReadOnlyList<CharTestRejection>)`, `ForParse(string error)`, `ForBuild(...)`(기존 `ForBuild` 위임), `ForFailsOnCurrent(IReadOnlyList<TestCaseResult>)`(각 테스트 이름 + 단언 메시지 첫 3줄 + 끝에 `The current behavior is the truth. Change the assertion to the actual value.`), `ForRegression(...)`, `ForFlaky(IReadOnlyList<string>)`, `ForSurvivors(IReadOnlyList<string>)`(`These tests pass even when the target throws. Assert the target's return value or effect.`). 모두 `budget` 이하(기존 `Done` 규칙과 같게 자름).
- [ ] **Step 1: 실패 테스트** — `OnCurrent_AllNewPass_Ok`, `OnCurrent_NewFails_FailsOnCurrentWithMessage`, `OnCurrent_NewMissingFromResults_Fails`, `OnCurrent_BuildErrorInTestFile_CharTestBuild`, `OnCurrent_BuildErrorElsewhere_Unverifiable`, `BaselineFailures_AreNotRegressions`, `OnCurrent_ExistingNewlyFails_Regression`, `Stable_Differs_Flaky`, `UnderMutation_Survivor_Named`, `UnderMutation_BuildError_Unbuildable`, `Baseline_ReportMissing_Unusable`, `Baseline_OnlyTestFailures_Usable`, `Feedback_FailsOnCurrent_ContainsActualAndInstruction`, `Feedback_AllWithinBudget`(긴 메시지 50개 → `Tokens <= 296`).
- [ ] **Step 2:** 필터 실행 → 실패.
- [ ] **Step 3:** 구현(`TestRun`은 테스트에서 직접 만든다).
- [ ] **Step 4:** 통과.
- [ ] **Step 5:** 커밋 `feat(chartest): run verdicts 4-7 and feedback`

### Task 9: 실행기(`CharTestOrchestrator`)와 기록

**Files:**
- Create: `src/PellyCoder.Core/CharTests/CharTestOrchestrator.cs`, `src/PellyCoder.Core/CharTests/CharTestState.cs`
- Modify: `src/PellyCoder.Core/Fsm/RunRequest.cs`(`RunContext`에 `public CharTestState? CharTest { get; set; }`), `src/PellyCoder.Core/Logging/RunLogger.cs`(`RunSummary`에 끝 선택 인자 `CharTestSummary? CharTest = null`, `Summarize`가 채움), `src/PellyCoder.Core/Fsm/FsmOrchestratorFactory.cs`(`CreateCharTest` 팩터리)
- Test: `tests/PellyCoder.Core.Tests/CharTests/CharTestOrchestratorTests.cs`(가짜 LLM·가짜 프로세스), `tests/PellyCoder.Core.Tests/Logging/RunLoggerTests.cs`(추가)

**Interfaces:**
- Consumes: Task 1~8 전부, `PreflightPhase`, `LocatePhase`(그대로 호출: `RunRequest(FilePath, ClassName, MethodName, Line, Request: hint ?? "", Yes, DryRun: false, MaxRetries, Kind: EditKind.CSharp, Verbose, RunTests: true)` — `--test-project`가 있으면 `options.Test.Projects`를 그 경로 하나로 덮은 복사본을 쓴다), `ctx.BaselineTests`, `ctx.BuildTool`, `ctx.TestProjects`, `TestRunner.RunAsync(tool, testProject, workDir, trxPath, ct)`, `TestStep.TrxPath(ctx, name)`, `FileGuard.Backup/WriteAsync/RestoreAsync`, `QwenStreamClient.GenerateEditAsync(..., answerPrefix: "### USINGS\n")`, `RunLogger.Start/Attempt/Phase/Complete`, `ReviewPhase`와 같은 승인 규칙(`--yes`, 비대화형이면 거부).
- Produces:
  - `public sealed record CharTestSummary(int Requested, int Generated, int Kept, int PassedOnCurrent, bool? StableOnRerun, int KilledByMutation, string? RejectedBy, string Framework, bool NewFile)`
  - `public sealed class CharTestState` — 테스트 프로젝트·위치·프레임워크·새 테스트 이름·백업 경로·미완료 표시 경로·`CharTestSummary` 계산용 값.
  - `public sealed class CharTestOrchestrator { Task<int> RunAsync(CharTestRequest request, CancellationToken ct); }` — 생성자 인자는 `FsmOrchestratorFactory.CreateCharTest(fs, backend, ui, options, time, processes, isWindows)`가 조립.
  - 흐름(설계 §3·§4): Preflight → Locate → `CharTestTarget.Check`(종료 3) → 테스트 프로젝트 수 확인(0개·여럿 종료 3; 레거시인데 Windows 아님 → 종료 4 「레거시 테스트는 Windows에서만 실행합니다」) → 실행 방식 확인(`TestProjectKind.OtherTestingPlatform`·`{trx}` 없음 → 종료 4) → `TestFrameworks.Detect`(종료 4) → `TestClassLocator.Find`(종료 3) → 원본 테스트 실행 기다림, `BaselineUsable`(종료 4) → 시도 루프(최대 `MaxRetries + 1`):
    1. 프롬프트·생성(`ui.ShowStatus(RunPhase.Edit, …)`), 2. 파싱·정적 판정(실패 → 피드백, 다음 시도, 파일 안 씀), 3. 첫 쓰기 전 미완료 표시 → 테스트 파일(·csproj) 백업·쓰기, 4. 실행·`OnCurrent`(실패 → 테스트 파일 원래 내용으로 되쓰기, 피드백), 5. 재실행·`Stable`, 6. 표시 갱신(`Mutated=true`) → 대상 변이 쓰기 → 실행 → `finally`에서 대상 복원·해시 확인·표시 갱신 → `UnderMutation`. `CharTestMutationUnbuildable`은 재시도 없이 종료 4.
    → 통과: 검토(`ui.ShowDiff` 테스트 파일, 새 csproj면 csproj도) → 승인·`--yes` 종료 0 / 거부·`--dry-run` 되돌림 종료 2(드라이런은 종료 0) / 재시도 소진 되돌림 종료 1. 끝에서 대상 파일 해시가 처음과 다르면 종료 4 + `Crashed` 메시지.
    → 항상: 미완료 표시 삭제(되돌리기가 모두 성공했을 때만), 잠금 해제, 원본 실행 취소·대기(`FsmOrchestrator`의 `BaselineStopWait` 방식), `logger.Complete`, `--verbose`면 `ui.ShowRunSummary`.
- [ ] **Step 1: 실패 테스트**(`MockFileSystem` 위 작은 작업 공간 + `ScriptedLlmBackend` + `ScriptedProcessRunner`가 TRX 파일을 써 주는 방식, 기존 `TestStep` 테스트의 도우미 재사용):
  - `HappyPath_KeepsTests_Exit0_TargetUnchanged`
  - `StaticRejection_RetriesWithFeedback_FileNeverWritten`
  - `FailsOnCurrent_SecondAttemptFixes_Exit0`
  - `Survivor_Retries_ThenExhausted_Exit1_Restored`
  - `MutationUnbuildable_Exit4_NoRetry_Restored`
  - `Reject_RestoresTestFileBytes`(CRLF·BOM 테스트 파일)
  - `NewFile_Rejected_FileDeleted_CsprojRestored`(레거시 csproj)
  - `DryRun_Exit0_Restored`
  - `CancelDuringMutation_RestoresTarget`(변이 실행 중 토큰 취소 → 대상 바이트 원본, 표시 없음, 종료 2)
  - `NoTestProject_Exit3`, `TwoTestProjects_NoOption_Exit3`, `OtherTestingPlatform_Exit4`, `BaselineUnusable_Exit4_ModelNotCalled`, `PrivateTarget_Exit3`
  - `Summary_HasCharTestBlockAndKind`(`summary.json`의 `kind`가 `CSharpTest`, `charTest.kept`가 3)
- [ ] **Step 2:** `--filter-class "*CharTestOrchestratorTests"` → 컴파일 실패.
- [ ] **Step 3:** 구현.
- [ ] **Step 4:** 통과 + Core 테스트 전체 실패 0.
- [ ] **Step 5:** 커밋 `feat(chartest): orchestrator with mutation safety and run summary`

### Task 10: CLI `pelly test`와 보고서

**Files:**
- Create: `src/PellyCoder.Cli/Commands/TestCommand.cs`
- Modify: `src/PellyCoder.Cli/CliApp.cs:18` 근처(`config.AddCommand<TestCommand>("test").WithDescription("메서드의 현재 동작을 고정하는 테스트를 만듭니다")`), `src/PellyCoder.Cli/Runners.cs`(`ICharTestRunner`), `src/PellyCoder.Cli/Composition.cs`(구현 등록), PowerShell 자동완성 명령 목록, `src/PellyCoder.Core/Reporting/ReportSanitizer.cs`·보고서 렌더러(「특성 테스트」 줄)
- Test: `tests/PellyCoder.Cli.Tests/Commands/TestCommandTests.cs`, 기존 자동완성 테스트(명령 목록), `tests/PellyCoder.Core.Tests/Reporting/*`(추가)

**Interfaces:**
- Consumes: Task 9 `CharTestOrchestrator`, `RunSettings`, `RunArguments`의 파일·옵션 준비(`PrepareCs`와 같은 방식으로 `PrepareTest` 추가).
- Produces:
  - `TestSettings : RunSettings` — `<FILE>`, `[HINT]`, `--class`, `--method`, `--line`, `--count <N>`, `--test-project <PATH>`. 검증: `--line`과 `--class/--method` 함께 → 오류(cs와 같은 문구), `--method`·`--line` 둘 다 없음 → 「--method나 --line이 필요합니다」, `--count` 범위 밖 → 「--count는 1~5입니다」(모두 종료 4).
  - `public interface ICharTestRunner { Task<int> RunAsync(CharTestRequest request, PellyOptions options, CancellationToken ct); }`
  - 보고서: 종류 `CSharpTest` 실행이 있으면 「특성 테스트: 실행 N건, 남김 K건(성공률 x%), 평균 시도 a.b, 실패 유형 …」. 정화본은 개수·유형만.
- [ ] **Step 1: 실패 테스트** — `Test_RequiresMethodOrLine`, `Test_CountOutOfRange`, `Test_LineWithMethod_Rejected`, `Test_PassesRequestToRunner`(가짜 `ICharTestRunner`가 받은 `CharTestRequest` 필드 확인), 자동완성 명령 목록에 `test`, `Report_CharTestLine`, `Sanitize_CharTestHasNoNames`.
- [ ] **Step 2:** 필터 실행 → 실패.
- [ ] **Step 3:** 구현.
- [ ] **Step 4:** 통과 + `dotnet build -warnaserror`, `dotnet test` 전체 실패 0.
- [ ] **Step 5:** 커밋 `feat(cli): pelly test command and report line`

### Task 11: 통합·E2E 테스트

**Files:**
- Create: `tests/PellyCoder.Integration.Tests/CharTests/CharTestIntegrationTests.cs`, `tests/PellyCoder.Integration.Tests/E2E/CharTestEndToEndTests.cs`

**Interfaces:**
- Consumes: Task 9·10, `CannedLlmBackend`(정해진 응답), `M3aEndToEndTests.PrepareModernAsync`와 같은 코퍼스 복사·restore 방식(복사해 공용 도우미로 쓰지 말고 같은 패턴을 따름), `[OllamaFact]`, Windows 전용 특성(기존 `windows-integration`이 쓰는 것).
- Produces: 테스트만.
- [ ] **Step 1: 테스트 작성**(실제 빌드·테스트, 정해진 모델 응답):
  - `Modern_GoodTests_Kept_TargetBytesUnchanged` — `SearchAsync` 대상, 통과하는 테스트 2개 응답 → 종료 0, 테스트 파일에 두 메서드, `OrderService.cs` 바이트 원본과 같음.
  - `Modern_AssertsNothingUseful_SurvivesMutation_Exit1` — 대상을 부르고 `Assert.NotNull(service)`만 하는 응답 4번 → 판정 7 실패, 종료 1, 테스트 파일 원본 바이트.
  - `Modern_WrongExpected_ThenActual_Exit0` — 1차 응답이 틀린 기대값(판정 5 실패), 2차 응답이 실제 값 → 종료 0, 2차 프롬프트의 FEEDBACK에 실제 값 문자열이 들어 있음.
  - `Legacy_MsTest_NewFile_Kept`(Windows 전용) — Legacy 코퍼스에서 테스트 클래스가 없는 타입의 public 메서드 하나를 고르고(작업 중에 코퍼스를 보고 정한다, 이름은 계획 원장에 기록), 새 파일 + csproj `Compile Include` → 종료 0.
  - E2E `[OllamaFact]` `Modern_RealModel_CalculateRefund` — 종료 0 또는 1이어도 되지만 운영 파일 바이트 동일, 미완료 표시 없음, 종료 0이면 `charTest.killedByMutation == charTest.kept`.
- [ ] **Step 2:** `dotnet test tests/PellyCoder.Integration.Tests -- --filter-class "*CharTestIntegrationTests"` → 통과(구현은 끝나 있으므로 이 작업은 테스트 추가다. 실패하면 원인을 고치고 원장에 적는다). E2E는 `PELLY_TEST_OLLAMA=http://localhost:11434 PELLY_TEST_MODEL=qwen3.6:35b-a3b-q4_K_M`로 1회.
- [ ] **Step 3:** 커밋 `test(chartest): integration and e2e`

### Task 12: 문서·측정

**Files:**
- Modify: `README.md`(`pelly test` 절), `docs/PRD.md`(§12.4 「테스트 작성」 줄, 새 절 「V2-5. 현재 동작 고정 테스트」, §12.2 로드맵 행, 변경 이력 v1.22), `deploy/usage.md`(짧은 안내 + 「C# 수정은 `--test` 권장」), `deploy/field-results.md`(새 절: T1 테스트 프로젝트 유무, T2 vstest TRX, T3 실제 레거시 메서드 3건, T4 파일 잠깐 바뀜 수용)
- Create: `eval/benchmark/chartest/*.yaml`(8건: `id`, `file`, `method`, `count` — Modern·Legacy에서 테스트 없는 public 메서드), `eval/benchmark/chartest/run.sh`(각 사례를 코퍼스 사본에서 `pelly test … --yes`로 돌리고 `summary.json`을 모음), `docs/chartest-premeasure.md`
- Test: 없음(문서). 측정은 로컬 현장 모델로 Modern 사례만 각 3회.

- [ ] **Step 1:** 문서 작성.
- [ ] **Step 2:** 측정 실행: `eval/benchmark/chartest/run.sh --repeat 3`(Modern 사례만, 레거시는 「Windows 현장 측정」으로 표시). 결과를 `docs/chartest-premeasure.md`에 표로(성공률, 시도 수, 판정별 실패, 변이 검출, 시간) — 「예비」 표시.
- [ ] **Step 3:** `dotnet build -warnaserror`, `dotnet test` 전체 실패 0.
- [ ] **Step 4:** 커밋 `docs: pelly test in README, PRD, field checks and premeasure`
