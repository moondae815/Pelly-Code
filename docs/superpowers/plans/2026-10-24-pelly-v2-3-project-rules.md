# Pelly-Coder V2-3 (프로젝트 규칙 — AGENTS.md) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 코드베이스를 분석해 작업 공간 루트 `AGENTS.md`에 짧은 프로젝트 규칙 초안을 만들고(`pelly rules init`), `pelly cs`/`pelly sql` 실행 때 지금 작업과 관련된 규칙만 골라 프롬프트에 넣으며, `forbid:` 패턴은 VERIFY에서 코드로 강제한다.

**Architecture:**
* 규칙은 `AGENTS.md`의 `## Pelly rules` 절에만 둔다. 같은 파일의 다른 내용(다른 도구용 설명)은 Pelly가 읽지 않는다. 그래서 Codex·Claude Code 같은 다른 도구와 파일 하나를 같이 쓸 수 있다.
* 초안 생성은 **모델을 부르지 않는 결정론적 분석**이다(Roslyn·정규식으로 패턴을 세고 기준을 넘는 것만 규칙으로 씀). 활성 3B 모델에게 코드베이스 요약을 맡기지 않는다(PRD §12.1 "프로세스는 코드로", §12.7).
* 실행 때: PREFLIGHT가 규칙을 읽고 → EDIT가 SLICE 텍스트로 규칙을 골라 `### RULES` 구간을 넣고 → VERIFY의 `RulesStep`이 REPLACE에 새로 추가된 줄을 `forbid` 정규식으로 검사해 위반이면 자가 교정 피드백으로 돌린다.

**Tech Stack:** .NET 10, Roslyn(기존), Spectre.Console.Cli(기존, `AddBranch`), System.IO.Abstractions(기존), xUnit v3

**Spec:** PRD `docs/PRD.md` §12.5(V2-3 프로젝트 규칙 파일), §12.1(원칙), §12.6(KPI), §12.7(하지 않을 것). 아래 "설계 결정"이 PRD와 다른 점을 적는다. 별도 설계 문서는 없다.

## 설계 결정 (PRD §12.5와 다른 점 · 이번 계획에서 정함)
1. **파일 위치:** PRD의 `pelly.rules.md` 대신 작업 공간 루트(`WorkspaceLocator.Resolve`의 `Root`)의 `AGENTS.md` 안 `## Pelly rules` 절. 하위 폴더의 `AGENTS.md`는 읽지 않는다.
2. **프롬프트 위치:** 규칙은 시스템 메시지가 아니라 사용자 메시지의 `### CONTEXT` 뒤, `### REQUEST` 앞에 `### RULES`로 넣는다. 시스템 부분이 실행마다 같아야 Ollama prefix 캐시가 유지된다(S2). 예산은 PRD대로 시스템 예산에서 나눈다: `budget.system` 기본값 600 → 450, 새 키 `budget.rules` 기본값 150.
3. **`forbid` 적용 범위:** 프롬프트에 선택된 규칙만이 아니라 **절의 모든 `forbid` 규칙**을 검사한다(규칙 위반 잔존율 0% 불변 조건, §12.6).
4. **정규식 시간 초과:** `forbid` 검사가 시간 초과(100ms)면 검사하지 못한 것이므로 통과로 보지 않고 환경 오류(종료 4)로 멈춘다(fail closed).
5. **초안은 사람이 검토:** `pelly rules init`은 새 절 내용을 보여주고 승인을 받은 뒤 쓴다. 규칙마다 근거를 `<!-- 근거: … -->` 주석 줄로 남긴다.
6. **규칙 도입 효과 KPI(+5%p)** 측정은 모델 실행이 필요해 이 계획 밖이다(코퍼스에는 `AGENTS.md`가 없으므로 기존 벤치마크 결과는 바뀌지 않는다).

## Global Constraints
* 규칙 줄 문법: `- [<태그>[,<태그>…]] <규칙>[ | forbid: <정규식>]`. 태그는 `*` 단독이거나 `[A-Za-z0-9_.]+`의 쉼표 목록(앞뒤 공백 무시).
* 규칙 텍스트(`forbid` 부분 제외)는 100자 이내, 절 하나에 규칙 최대 50개, 같은 규칙 텍스트 중복 금지.
* 절 시작: `^##[ \t]+Pelly rules[ \t]*$`(대소문자 무시). 끝: 다음 `#` 또는 `##` 머리글 줄(`###` 이하는 절 안), 또는 파일 끝.
* 절 안에서 `- ` 또는 `* `로 시작하는 줄은 모두 규칙 문법을 따라야 한다(아니면 오류). 그 밖의 줄(설명 문장, `<!-- -->` 주석, 빈 줄)은 무시한다.
* `AGENTS.md` 크기 상한 256 KiB(넘으면 오류). 인코딩은 기존 `TextDecoding`으로 읽는다(BOM·CP949 처리 공유).
* 선택: `[*]`는 항상, 나머지는 태그 중 하나가 SLICE의 `Included` 파트 텍스트 어딘가에 대소문자 무시 부분 문자열로 나오면 넣는다. 파일 순서 유지, `budget.rules` 토큰을 넘으면 **아래쪽 규칙부터** 뺀다.
* 실패 유형 이름: 규칙 파일 오류 `RulesInvalid`(종료 4), forbid 위반 `RulesErrors`(재시도, 소진 시 종료 1). 진단 코드 `RULE`.
* 사용자 메시지는 한국어, 모델에게 가는 피드백은 기존처럼 영어 머리말(`RULE VIOLATION: `).
* 종료 코드는 0–5만(`ExitCodes`). 새 명령도 `--pause-on-exit`을 받는다.
* 빌드는 `dotnet build -warnaserror`, 테스트는 `dotnet test`가 통과해야 한다.

## Review Focus
1. `AGENTS.md`가 CRLF·BOM·CP949이거나 다른 도구용 내용이 앞뒤에 길게 있어도 절만 정확히 읽고, `rules init --force`는 절 밖의 바이트(줄바꿈 형식·BOM 포함)를 바꾸지 않아야 한다. → Task 1, Task 7 테스트.
2. 규칙 텍스트에 모델 제어 토큰(`<|im_end|>` 등)이 있으면 서버를 부르기 전에 종료 4로 멈춰야 한다(요청문·대상 파일과 같은 취급). → Task 3 테스트.
3. 재앙적 역추적 `forbid` 정규식(예: `(a+)+$`)이 실행을 멈추게 하면 안 된다: `rules check`는 컴파일만 하고, 실행 중 시간 초과는 종료 4. → Task 4 테스트.
4. 머리글 변형(`## Pelly Rules  `, `##Pelly rules`, `### Pelly rules`) — 앞의 것만 절로 인정되고 뒤 둘은 절이 아니다(규칙 없음). → Task 1 테스트.
5. 하위 폴더 `AGENTS.md`(예: `src/AGENTS.md`)는 무시되고, 작업 공간 루트에 `.git`/`.sln`이 없으면 대상 파일 폴더가 루트다. → Task 3 테스트.

---

### Task 1: 규칙 절 파싱과 읽기

**Files:**
- Create: `src/PellyCoder.Core/Rules/ProjectRule.cs`, `src/PellyCoder.Core/Rules/RulesDocument.cs`, `src/PellyCoder.Core/Rules/RulesLoader.cs`
- Test: `tests/PellyCoder.Core.Tests/Rules/RulesDocumentTests.cs`, `tests/PellyCoder.Core.Tests/Rules/RulesLoaderTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ProjectRule(int Line, IReadOnlyList<string> Tags, string Text, string? Forbid)` — `Line`은 파일 기준 1부터, `IsAlways => Tags is ["*"]`, `Forbid`는 정규식 원문.
  - `public sealed record RulesError(int Line, string Message)`
  - `public sealed record RulesSection(int StartLine, int EndLine)` — 머리글 줄 포함, `EndLine`은 절의 마지막 줄(1부터, 포함).
  - `public static class RulesDocument`: `RulesSection? FindSection(string textLf)`, `(IReadOnlyList<ProjectRule> Rules, IReadOnlyList<RulesError> Errors) Parse(string textLf)`, `string ReplaceSection(string textLf, string newSectionLf)`(절이 없으면 끝에 빈 줄 하나 두고 덧붙임).
  - `public sealed record RulesLoadResult(string? Path, IReadOnlyList<ProjectRule> Rules, IReadOnlyList<RulesError> Errors)` — 파일·절이 없으면 `Path` null 또는 빈 `Rules`, 오류 없음.
  - `public static class RulesLoader`: `const string FileName = "AGENTS.md"`, `const int MaxBytes = 256 * 1024`, `RulesLoadResult Load(IFileSystem fs, Workspace ws)`.

- [ ] **Step 1: 실패하는 테스트 작성** — `RulesDocumentTests`:
  - `Parse_ReadsTagsTextAndForbid`: `"# A\n## Pelly rules\n- [async] .Result 금지 | forbid: \\.Result\\b\n"` → 규칙 1개, `Line == 3`, `Tags == ["async"]`, `Text == ".Result 금지"`, `Forbid == @"\.Result\b"`.
  - `Parse_AlwaysTag`: `- [*] 주석은 한국어` → `IsAlways`. `- [*,async] x` → 오류(`*`는 단독).
  - `Parse_SectionEndsAtNextLevel2Heading_KeepsLevel3Inside`: `## Pelly rules` / `### C#` / `- [a] x` / `## Other` / `- [b] y` → 규칙은 `x`만.
  - `Parse_IgnoresProseAndComments`: 설명 문장, `<!-- 근거: … -->`, 빈 줄은 무시.
  - `Parse_ErrorOnMalformedListItem`: `- [async .Result` 와 `* 그냥 문장` → 각 줄 번호로 오류.
  - `Parse_ErrorOnTooLongText` (101자), `Parse_ErrorOnDuplicateText`, `Parse_ErrorOnBadRegex` (`forbid: (`), `Parse_ErrorOnMoreThan50Rules`.
  - `FindSection_HeadingVariants` (Review Focus 4): `"## Pelly Rules  "` → 찾음, `"##Pelly rules"`·`"### Pelly rules"` → null.
  - `ReplaceSection_AppendsWhenMissing`, `ReplaceSection_ReplacesOnlySection`: 절 앞뒤 텍스트가 그대로.
  - `RulesLoaderTests`(MockFileSystem): 파일 없음 → 규칙 0·오류 0; 절 없음 → 같음; CRLF+UTF-8 BOM 파일 → 정상(Review Focus 1); CP949 파일 → 한글 규칙 정상; 256 KiB 초과 → 오류 1개(`Line` 0); `src/AGENTS.md`만 있음 → 규칙 0(Review Focus 5).
- [ ] **Step 2: 실행해 실패 확인** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~Rules"` → 컴파일 실패.
- [ ] **Step 3: 구현** — 정규식은 `GeneratedRegex`; 줄 문법 `^- \[(?<tags>[^\]]+)\][ \t]+(?<text>.+?)(?:[ \t]+\|[ \t]+forbid:[ \t]+(?<forbid>.+?))?[ \t]*$`. `forbid` 컴파일 확인은 `new Regex(p, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100))`. 읽기는 `TextDecoding`으로 디코딩 후 `\r\n`→`\n`.
- [ ] **Step 4: 통과 확인** — 같은 명령 → PASS.
- [ ] **Step 5: 커밋** — `feat(rules): parse the Pelly rules section of AGENTS.md`

### Task 2: 규칙 예산·선택·프롬프트 구간

**Files:**
- Create: `src/PellyCoder.Core/Rules/RuleSelector.cs`
- Modify: `src/PellyCoder.Core/Config/PellyOptions.cs` (`BudgetOptions`), `src/PellyCoder.Core/Config/PellyOptionsLoader.cs` (`Validate`), `src/PellyCoder.Core/Llm/PromptBuilder.cs`
- Test: `tests/PellyCoder.Core.Tests/Rules/RuleSelectorTests.cs`, `tests/PellyCoder.Core.Tests/Llm/PromptBuilderTests.cs`, `tests/PellyCoder.Core.Tests/Llm/PromptBuilderSqlTests.cs`, `tests/PellyCoder.Core.Tests/Config/PellyOptionsLoaderTests.cs`

**Interfaces:**
- Consumes: `ProjectRule` (Task 1), `Slice.Included` 파트의 `Text`.
- Produces:
  - `BudgetOptions.Rules` (기본 150), `BudgetOptions.System` 기본 450. 검증 합: `system+rules+request+context+feedback <= inputTotal`(메시지에 `rules` 포함).
  - `public sealed record RuleSelection(IReadOnlyList<ProjectRule> Included, IReadOnlyList<ProjectRule> Dropped, int Tokens)` — `Dropped`는 태그가 맞았지만 예산 때문에 빠진 것만.
  - `public static class RuleSelector`: `RuleSelection Select(IReadOnlyList<ProjectRule> rules, Slice slice, ITokenCounter tokens, int budget)`; `static string Render(IEnumerable<ProjectRule> rules)` → 규칙마다 `- <Text>` 한 줄, `\n`으로 연결.
  - `PromptBuilder.BuildCSharp(..., Feedback? feedback, IReadOnlyList<ProjectRule>? rules = null)`, `BuildSql(...)` 동일. 선택 결과가 비어 있지 않으면 `### CONTEXT` 파트들 뒤, `### REQUEST` 앞에 `### RULES` + `Render(Included)`.
  - `PromptBuildResult`에 `RuleSelection? Rules = null` 추가(마지막 위치 매개변수).

- [ ] **Step 1: 실패하는 테스트 작성**
  - `Select_AlwaysRulesAlwaysIncluded`, `Select_TagMatchesSliceTextCaseInsensitive` (`[dapper]` + usings 파트 `using Dapper;` → 포함, 태그 `ASYNC` + 텍스트 `async Task` → 포함), `Select_UnmatchedTagExcluded`(Dropped에도 없음), `Select_OverBudgetDropsFromBottom` (예산을 규칙 둘만 들어가게 잡고 셋이 맞으면 세 번째가 `Dropped`), `Select_KeepsFileOrder`.
  - `PromptBuilderTests.BuildCSharp_InsertsRulesBetweenContextAndRequest`: 프롬프트에서 `"### RULES\n- 주석은 한국어로 작성한다\n### REQUEST"` 순서가 보이고, 시스템 메시지 부분은 규칙 없을 때와 바이트 단위로 같다(설계 결정 2).
  - `BuildCSharp_NoRules_NoRulesSection` (기존 호출 결과와 동일), `BuildSql_InsertsRules`.
  - `PellyOptionsLoaderTests`: 기본값 `Budget.System == 450`, `Budget.Rules == 150`; `budget:rules`를 키워 합이 `inputTotal`을 넘으면 오류 문구에 `rules` 포함. 기존 `Assert.Equal(600, o.Budget.System)`은 450으로 고친다.
- [ ] **Step 2: 실패 확인** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~RuleSelector|FullyQualifiedName~PromptBuilder|FullyQualifiedName~PellyOptionsLoader"` → FAIL.
- [ ] **Step 3: 구현** — 토큰은 `Render`한 줄마다 `tokens.Count(line + "\n")`로 세어 누적.
- [ ] **Step 4: 통과 확인** — 같은 명령, 그리고 `dotnet test tests/PellyCoder.Core.Tests` 전체 PASS.
- [ ] **Step 5: 커밋** — `feat(rules): select rules by slice text and add a RULES section to the edit prompt`

### Task 3: 실행 연결 — 읽기, EDIT 전달, 기록, `--verbose`

**Files:**
- Modify: `src/PellyCoder.Core/Fsm/RunRequest.cs` (`RunContext`), `src/PellyCoder.Core/Fsm/Phases/PreflightPhase.cs`, `src/PellyCoder.Core/Fsm/Phases/EditPhase.cs`, `src/PellyCoder.Core/Logging/RunLogger.cs` (`RunSummary`, `Summarize`), `src/PellyCoder.Cli/Terminal/SpectreUserInteraction.cs` (`ShowRunSummary`), `src/PellyCoder.Core/Reporting/FailureKinds.cs`
- Test: `tests/PellyCoder.Core.Tests/Fsm/RulesFlowTests.cs`, `tests/PellyCoder.Cli.Tests/SpectreUserInteractionTests.cs`, `tests/PellyCoder.Core.Tests/Reporting/FailureKindsTests.cs`

**Interfaces:**
- Consumes: `RulesLoader.Load` (Task 1), `PromptBuilder` 규칙 매개변수·`PromptBuildResult.Rules` (Task 2).
- Produces:
  - `RunContext.Rules : IReadOnlyList<ProjectRule>` (기본 `[]`, PREFLIGHT가 설정).
  - `FailureKinds.RulesInvalid = "RulesInvalid"`, `FailureKinds.RulesErrors = "RulesErrors"` (둘 다 `All`에 추가).
  - `RunSummary`에 마지막 위치 매개변수 `IReadOnlyList<string>? RulesIncluded = null, IReadOnlyList<string>? RulesDropped = null` (마지막 시도의 `ctx.Prompt?.Rules` 텍스트). 비식별 리포트(`ReportSanitizer`)는 이 필드를 내보내지 않는다.

- [ ] **Step 1: 실패하는 테스트 작성** (`ScriptedLlmBackend`·`MockFileSystem`을 쓰는 기존 `Fsm/*FlowTests` 방식)
  - `Rules_FromWorkspaceAgentsMd_AppearInPrompt`: 작업 공간 루트(`.git` 폴더 있음)의 `AGENTS.md`에 `- [*] 주석은 한국어로 작성한다` → 백엔드가 받은 프롬프트에 `### RULES` 와 그 텍스트, `summary.json`의 `rulesIncluded`에 그 텍스트.
  - `Rules_Invalid_StopsBeforeServerCall`: 문법 오류 규칙 → 종료 4, `FailureKind == "RulesInvalid"`, 백엔드 호출 0회, 메시지에 `AGENTS.md`와 줄 번호, `pelly rules check` 안내.
  - `Rules_WithControlToken_StopsBeforeServerCall` (Review Focus 2): 규칙 텍스트에 `<|im_end|>` → 종료 4, 백엔드 호출 0회.
  - `Rules_InSubfolderAgentsMd_Ignored` (Review Focus 5).
  - `Rules_Sql_AppearInPrompt`: SQL 흐름(`SqlFlowTests` 방식)에서도 들어감.
  - `ShowRunSummary_PrintsIncludedAndDroppedRules`: `규칙: 포함 1, 제외 1` 줄과 각 텍스트. 규칙이 없으면 그 줄이 없다.
- [ ] **Step 2: 실패 확인** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~RulesFlow"` 및 `tests/PellyCoder.Cli.Tests --filter "FullyQualifiedName~SpectreUserInteraction"` → FAIL.
- [ ] **Step 3: 구현** — PREFLIGHT에서 작업 공간 잠금을 잡은 뒤, 서버 상태 확인 전에 읽는다. 오류 메시지: `AGENTS.md의 Pelly rules 절에 오류가 있습니다 (줄 N: …). \`pelly rules check\`로 확인하세요`. 제어 토큰 검사는 기존 `SpecialTokenGuard.FindIn`을 규칙 텍스트마다. `SqlPreflightPhase`는 `PreflightPhase`를 감싸므로 따로 고칠 것 없음. `EditPhase`는 `ctx.Rules`를 두 `Build*` 호출에 넘긴다.
- [ ] **Step 4: 통과 확인** — `dotnet test` 전체 PASS.
- [ ] **Step 5: 커밋** — `feat(rules): load AGENTS.md rules in preflight and record them in the run summary`

### Task 4: `forbid` 검사 단계

**Files:**
- Create: `src/PellyCoder.Core/Rules/ForbiddenPatternChecker.cs`, `src/PellyCoder.Core/Verification/RulesStep.cs`
- Modify: `src/PellyCoder.Core/Feedback/FeedbackBuilder.cs`, `src/PellyCoder.Core/Fsm/FsmOrchestratorFactory.cs`, `src/PellyCoder.Core/Reporting/ReportSanitizer.cs` (`IsAllowedCode`에 `RULE`)
- Test: `tests/PellyCoder.Core.Tests/Rules/ForbiddenPatternCheckerTests.cs`, `tests/PellyCoder.Core.Tests/Fsm/RulesFlowTests.cs`, `tests/PellyCoder.Core.Tests/Reporting/ReportSanitizerTests.cs`

**Interfaces:**
- Consumes: `ctx.Rules` (Task 3), `ctx.Blocks : IReadOnlyList<PatchBlock(Search, Replace)>`.
- Produces:
  - `public sealed record RuleViolation(ProjectRule Rule, string AddedLine)`
  - `public static class ForbiddenPatternChecker`: `IReadOnlyList<string> AddedLines(IReadOnlyList<PatchBlock> blocks)` — 블록마다 Replace 줄에서 Search 줄을 다중집합으로 뺀 나머지(순서 유지); `IReadOnlyList<RuleViolation> Check(IReadOnlyList<ProjectRule> rules, IReadOnlyList<string> addedLines)` — `Forbid`가 있는 모든 규칙, 시간 초과는 `RegexMatchTimeoutException`을 그대로 던짐.
  - `FeedbackBuilder.ForRuleViolations(IReadOnlyList<RuleViolation> violations)` → `RULE VIOLATION: <규칙 텍스트>\n  added: <줄(앞뒤 공백 제거)>`를 위반마다, 예산 넘으면 뒤에서부터 뺌.
  - `RulesStep : IVerifierStep`, `Name => "Rules"` (실패 유형은 자동으로 `RulesErrors`). 위반이면 `StepResult.Fail(feedback, [new Diagnostic(<대상 파일>, 0, 0, "error", "RULE", <규칙 텍스트>)], [])`. 시간 초과면 `StepResult.Environment("AGENTS.md 줄 N의 forbid 정규식 검사가 시간 안에 끝나지 않았습니다 …", "RulesInvalid")`.
  - 배치: C#은 `SyntaxStep` 바로 뒤, SQL은 `SqlObjectStep` 앞(맨 앞).

- [ ] **Step 1: 실패하는 테스트 작성**
  - `AddedLines_ExcludesLinesAlreadyInSearch` (SEARCH에 있던 `.Result` 줄은 그대로 남아도 위반 아님), `AddedLines_CountsDuplicates`(같은 줄이 SEARCH 1번·REPLACE 2번이면 1줄 추가).
  - `Check_AllForbidRules_NotOnlySelected` (설계 결정 3), `Check_NoForbid_NoViolation`.
  - `Check_CatastrophicPattern_Throws` (Review Focus 3): `forbid: (a+)+$`, 추가 줄 `"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!"` → `RegexMatchTimeoutException`.
  - `RulesFlowTests.Forbid_Violation_RetriesWithFeedback`: 1차 응답이 `task.Result`를 추가, 2차는 `await task` → 종료 0, 2차 프롬프트에 `### FEEDBACK` + `RULE VIOLATION:`, `errorCodes`에 `RULE`.
  - `Forbid_ViolationEveryAttempt_Exit1`: 종료 1, `FailureKind == "RulesErrors"`, 파일 원복.
  - `Forbid_Timeout_Exit4`.
  - `ReportSanitizerTests`: `IsAllowedCode("RULE")` true, `"RULES"` false.
- [ ] **Step 2: 실패 확인** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~ForbiddenPattern|FullyQualifiedName~RulesFlow|FullyQualifiedName~ReportSanitizer"` → FAIL.
- [ ] **Step 3: 구현**
- [ ] **Step 4: 통과 확인** — `dotnet test` 전체 PASS.
- [ ] **Step 5: 커밋** — `feat(rules): enforce forbid patterns on added lines in VERIFY`

### Task 5: `pelly rules check`

**Files:**
- Create: `src/PellyCoder.Cli/Commands/RulesCheckCommand.cs`
- Modify: `src/PellyCoder.Cli/CliApp.cs` (`AddBranch("rules", …)`, `CommandSettings`, `FindOptionWithoutValue`)
- Test: `tests/PellyCoder.Cli.Tests/RulesCheckCommandTests.cs`, `tests/PellyCoder.Cli.Tests/ExitCodeMappingTests.cs`

**Interfaces:**
- Consumes: `RulesLoader`, `RuleSelector.Render`, 옵션은 `OptionsResolver`로(`DoctorCommand`와 같은 방식, `budget.rules`가 필요).
- Produces:
  - `RulesCheckSettings`: `--config <PATH>`, `--pause-on-exit`.
  - `CliApp.CommandSettings` 키를 `"rules init"`/`"rules check"`로 등록하고, `FindOptionWithoutValue`는 `args[0] == "rules"`이면 `args[0] + " " + args[1]`로 찾고 옵션은 인덱스 2부터 본다.
  - 작업 공간은 `ReportCommand`처럼 현재 폴더 기준.

- [ ] **Step 1: 실패하는 테스트 작성** (기존 `ReportCommandTests`의 `CommandAppTester` + MockFileSystem 방식)
  - `Check_NoFile_Exit0_SaysNoRules`: 출력에 `규칙이 없습니다`와 `pelly rules init` 안내.
  - `Check_Valid_Exit0_ShowsTable`: 줄·태그·규칙·토큰·forbid 열, `[*]` 규칙 토큰 합과 `budget.rules` 150 표시.
  - `Check_Errors_Exit4_ListsLines`.
  - `Check_AlwaysRulesOverBudget_Warns`: `[*]` 규칙만으로 150 토큰을 넘으면 경고 줄(`항상 넣는 규칙만으로 예산을 넘어 아래쪽 규칙이 빠집니다`), 종료 0.
  - `FindOptionWithoutValue_RulesCheck_ConfigWithoutValue` → `"--config"`.
- [ ] **Step 2: 실패 확인** — `dotnet test tests/PellyCoder.Cli.Tests --filter "FullyQualifiedName~RulesCheck|FullyQualifiedName~ExitCodeMapping"` → FAIL.
- [ ] **Step 3: 구현**
- [ ] **Step 4: 통과 확인** — 같은 명령 PASS, `dotnet run --project src/PellyCoder.Cli -- rules check` 이 저장소 루트에서 종료 0.
- [ ] **Step 5: 커밋** — `feat(cli): pelly rules check`

### Task 6: 규칙 초안 분석기

**Files:**
- Create: `src/PellyCoder.Core/Rules/RulesDrafter.cs`
- Test: `tests/PellyCoder.Core.Tests/Rules/RulesDrafterTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record DraftRule(string Line, string Evidence)` — `Line`은 Task 1 문법을 통과하는 규칙 줄 전체, `Evidence`는 한국어 한 줄(예: `async 메서드 42개 중 41개가 Async로 끝남`).
  - `public sealed record DraftResult(IReadOnlyList<DraftRule> Rules, int CsFiles, int SqlFiles)`
  - `public static class RulesDrafter`: `DraftResult Draft(IFileSystem fs, string root, CancellationToken ct = default)`. 파일은 `BoundedWalker.Files`로(최대 디렉터리 2000, `.cs` 500개, `.sql` 300개, 파일당 512 KiB 넘으면 건너뜀), 디코딩은 `TextDecoding`. 결과 순서: `[*]` 규칙 먼저, 그다음 아래 표 순서.

  탐지 규칙(정확한 문구와 기준 — 표본이 최소 수에 못 미치면 규칙을 만들지 않음):

  | id | 측정 (C#은 Roslyn 구문 트리) | 최소 표본 | 기준 | 규칙 줄 |
  | :--- | :--- | :--- | :--- | :--- |
  | async-suffix | `async` 한정자 메서드(`override`·`Main` 제외) 중 이름이 `Async`로 끝나는 비율 | 10 | ≥ 0.9 | `- [async] async 메서드 이름은 Async로 끝낸다` |
  | no-blocking | async 메서드 10개 이상인 코드에서 `.Result` 멤버 접근 + `.Wait()` 호출 + `.GetAwaiter().GetResult()` 수 | 10 | = 0 | `- [async] .Result나 .Wait()로 비동기 결과를 기다리지 않는다 \| forbid: \.Result\b\|\.Wait\(\)` |
  | configure-await | `await` 식 중 `.ConfigureAwait(false)`가 붙은 비율 | 20 | ≥ 0.8 | `- [await] await하는 Task에는 ConfigureAwait(false)를 붙인다` |
  | dapper | `using Dapper;`가 있는 파일 수 | 1 | ≥ 1 | `- [dapper] SQL 값은 Dapper 파라미터로 넘기고 문자열 연결·보간으로 만들지 않는다` |
  | korean-comments | 주석 트리비아(`//`, `/* */`, `///`) 중 한글(U+AC00–U+D7A3)이 있는 비율 | 20 | ≥ 0.6 | `- [*] 주석은 한국어로 작성한다` |
  | exception-language | `throw new X(<문자열 또는 보간 문자열>, …)`의 첫 문자열 인수 중 한글 비율 | 10 | ≥ 0.6 → 한국어 / ≤ 0.1 → 영어 | `- [throw] 예외 메시지는 한국어로 작성한다` / `- [throw] 예외 메시지는 영어로 작성한다` |
  | var-style | 지역 변수 선언 중 `var` 비율 | 50 | ≥ 0.8 / ≤ 0.2 | `- [*] 지역 변수는 가능하면 var로 선언한다` / `- [*] 지역 변수는 var 대신 명시적 타입으로 선언한다` |
  | sql-nocount | `.sql`의 `CREATE`/`ALTER PROCEDURE` 중 본문에 `SET NOCOUNT ON`이 있는 비율(대소문자 무시) | 3 | ≥ 0.8 | `- [procedure] 프로시저 본문 처음에 SET NOCOUNT ON을 둔다` |
  | sql-schema | `.sql`의 `FROM`/`JOIN` 뒤 객체 이름(`#`·`@`로 시작하는 것 제외) 중 스키마가 붙은 비율 | 20 | ≥ 0.9 | `- [procedure,function,view] 테이블·뷰를 참조할 때 스키마 이름(dbo. 등)을 붙인다` |

- [ ] **Step 1: 실패하는 테스트 작성** — MockFileSystem에 작은 코드베이스를 만든다. 탐지 규칙마다 기준을 넘는 경우와 표본 부족·기준 미달인 경우 2개씩(예: `AsyncSuffix_TenOfTen_Emits`, `AsyncSuffix_NineSamples_NoRule`, `NoBlocking_OneResultUse_NoRule`, `ExceptionLanguage_English_EmitsEnglishRule`, `SqlNocount_ThreeOfThree_Emits`). 추가로:
  - `Draft_AllLinesParse`: 모든 탐지가 걸리는 코드베이스에서 나온 줄을 `RulesDocument.Parse("## Pelly rules\n" + lines)`에 넣으면 오류 0.
  - `Draft_SkipsBinObj`: `bin/`·`obj/` 안 파일은 세지 않음.
  - `Draft_Corpus_IsDeterministic`: `eval/corpus`(테스트의 `CorpusLocation` 사용)를 두 번 분석해 결과가 같다.
- [ ] **Step 2: 실패 확인** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~RulesDrafter"` → FAIL.
- [ ] **Step 3: 구현** — 파싱 실패한 `.cs`는 건너뛴다. C# 언어 버전은 최신으로 파싱(초안용이라 정확한 버전 불필요).
- [ ] **Step 4: 통과 확인** — 같은 명령 PASS.
- [ ] **Step 5: 커밋** — `feat(rules): deterministic draft of project rules from the codebase`

### Task 7: `pelly rules init`

**Files:**
- Create: `src/PellyCoder.Cli/Commands/RulesInitCommand.cs`, `src/PellyCoder.Core/Rules/RulesSectionWriter.cs`
- Modify: `src/PellyCoder.Cli/CliApp.cs`
- Test: `tests/PellyCoder.Core.Tests/Rules/RulesSectionWriterTests.cs`, `tests/PellyCoder.Cli.Tests/RulesInitCommandTests.cs`

**Interfaces:**
- Consumes: `RulesDrafter.Draft` (Task 6), `RulesDocument.FindSection`/`ReplaceSection` (Task 1), `IUserInteraction.ConfirmAsync`.
- Produces:
  - `public static class RulesSectionWriter`: `string BuildSection(IReadOnlyList<DraftRule> rules, DateOnly today)` →
    ```
    ## Pelly rules
    <!-- pelly rules init 초안 (yyyy-MM-dd). 검토해서 고치거나 지우세요. 형식: - [태그,...] 규칙(100자 이내) [| forbid: 정규식] -->
    - [async] async 메서드 이름은 Async로 끝낸다
    <!-- 근거: async 메서드 42개 중 41개가 Async로 끝남 -->
    ```
    규칙이 0개면 주석 줄 아래에 `<!-- 일관된 패턴을 찾지 못했습니다. 예: - [*] 주석은 한국어로 작성한다 -->`.
  - `string Apply(string? existingFileText, string sectionLf)` → 파일이 없으면 `"# AGENTS.md\n\n" + section`, 있으면 `RulesDocument.ReplaceSection`. 원본의 BOM 유무와 줄바꿈(다수 쪽, `FileSnapshot`과 같은 규칙)을 유지해 쓴다.
  - `RulesInitSettings`: `--dry-run`, `--force`, `--yes`, `--pause-on-exit`.
  - 동작: 절이 이미 있고 `--force`가 없으면 종료 4(`이미 AGENTS.md에 Pelly rules 절이 있습니다. --force로 이 절만 다시 만들거나 \`pelly rules check\`로 검사하세요`). 새 절과 근거를 보여주고 `--dry-run`이면 종료 0. 아니면 `AGENTS.md에 쓸까요?`(기본 아니오)를 묻고, 비대화형인데 `--yes`가 없으면 종료 4. 쓴 뒤 `RulesLoader.Load`로 다시 읽어 오류 0을 확인하고 경로와 규칙 수를 출력.

- [ ] **Step 1: 실패하는 테스트 작성**
  - `Apply_NoFile_CreatesWithTitle`, `Apply_ExistingWithoutSection_AppendsAndKeepsRest`, `Apply_ReplacesOnlySection_PreservesCrlfAndBom` (Review Focus 1: 절 밖 바이트 동일).
  - `Init_DryRun_DoesNotWrite`, `Init_Yes_WritesAgentsMd` (MockFileSystem 작업 공간, 결과 파일을 `RulesLoader`로 읽으면 오류 0), `Init_ExistingSection_NoForce_Exit4`, `Init_Force_ReplacesSection`, `Init_NonInteractiveWithoutYes_Exit4`, `Init_UserDeclines_Exit2_NoWrite`.
- [ ] **Step 2: 실패 확인** — `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~RulesSectionWriter"`, `dotnet test tests/PellyCoder.Cli.Tests --filter "FullyQualifiedName~RulesInit"` → FAIL.
- [ ] **Step 3: 구현** — 거부는 `ExitCodes.UserCancelled`(2).
- [ ] **Step 4: 통과 확인** — 같은 명령 PASS. 수동: 스크래치 폴더에 `eval/corpus/Modern`을 복사해 `pelly rules init --dry-run` 실행, 초안이 출력되는지 확인.
- [ ] **Step 5: 커밋** — `feat(cli): pelly rules init drafts the AGENTS.md rules section`

### Task 8: 문서

**Files:**
- Modify: `README.md`(사용법에 `pelly rules init`·`pelly rules check`와 `AGENTS.md` 형식, `budget.rules`), `deploy/usage.md`(같은 내용 요약), `docs/PRD.md` §12.5(머리에 "구현: 2026-10-24, 설계 결정 1–6은 `docs/superpowers/plans/2026-10-24-pelly-v2-3-project-rules.md`" 한 단락, 변경 이력 표에 행 추가), 설계 문서 §설정 예시의 `"system": 600` → `450`, `"rules": 150` 추가.
- Test: 기존 `tests/PellyCoder.Integration.Tests/Package/FieldKitDocTests.cs`·`PackageTests.cs`

- [ ] **Step 1: 문서 수정** — README "실행 후 표시 옵션" 앞에 `### pelly rules — 프로젝트 규칙 (AGENTS.md)` 절: 문법, 선택 방식, 예산, forbid 강제, 하위 폴더 무시, 초안은 검토 필수.
- [ ] **Step 2: 확인** — `dotnet build -warnaserror && dotnet test` → 전체 PASS (문서 검사 테스트 포함).
- [ ] **Step 3: 커밋** — `docs: project rules (AGENTS.md) usage and PRD V2-3 status`
