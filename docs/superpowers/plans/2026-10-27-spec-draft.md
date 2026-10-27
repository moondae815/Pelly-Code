# `pelly spec draft` 구현 계획

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `pelly spec draft <id> "<요청>" --scope …`가 S6 조건 그대로 모델 작업 줄 초안을 받고, 기존 검사·영향 탐색을 거쳐 사람이 승인하면 `changes/<id>.md`로 쓴다.

**Architecture:** Core의 `Specs/Drafting/`에 색인(`DraftIndex`, S6 `CorpusIndex` 이식), 모델 호출(`DraftModel`, S6 arm B의 2단계 호출을 기존 스트림 클라이언트로), 조립(`SpecDrafter`: 줄 받기 → 문서 → `.pelly/drafts/` 임시 파일로 `SpecChecker.CheckAsync` → 그룹 출발 영향 대상을 작업 줄로 더함 → 재검사)을 둔다. Cli의 `SpecDraftCommand`가 인수·화면·승인·쓰기를 맡고, 모델 백엔드는 Composition의 새 팩토리로 받는다.

**Tech Stack:** .NET 10, Roslyn, ScriptDom, Spectre.Console.Cli, xUnit v3, Ollama(로컬 예비 측정).

**Spec:** `docs/superpowers/specs/2026-10-27-spec-draft-design.md`

## Global Constraints

- 작업 위치는 워크트리 `/Users/payletter/git-root/Pelly-Code-spec-draft`(브랜치 `spec-draft`). 셸 명령은 `cd /Users/payletter/git-root/Pelly-Code-spec-draft && …`. 메인 체크아웃에서 빌드·테스트하지 않는다.
- 게이트: `dotnet build -warnaserror` 종료 0, `dotnet test` 실패 0(건너뜀은 SQL 서버·로컬 모델·Windows 전용만).
- S6 조건 고정: 시스템 프롬프트는 `spikes/S6-planning/S6Planning/PlanPrompt.cs`의 `System` **문구 그대로**, 사용자 메시지 `"Index:\n" + 색인 + "\nRequest:\n" + 요청`, 생각 단계 sampling (0.6, 0.95, 20, 1.0, 0.0) `num_predict` 2048 stop `</think>`, 답 단계 (0.1, 0.9, 20, 1.05, 0.0) `num_predict` 1024 stop 턴 끝 토큰, `num_ctx` 8192, `keep_alive` "10m", 잘린 생각도 원문 그대로 이어 붙임.
- 첫 입력(시스템+색인+요청) ≤ 4096 토큰, 넘으면 모델을 부르지 않고 종료 4. 색인을 자르지 않는다.
- 승인 없이는 `changes/` 아래 아무것도 쓰거나 바꾸지 않는다. 초안 검사는 `.pelly/drafts/<id>.md`에서 하고 지운다.
- 문법 밖 줄은 고치지 않고 버리며 원문을 사람에게 보인다.
- 영향 탐색 출발점은 그룹 작업뿐. 더하는 대상은 C# 의미 기반·멤버 안·파일 안 이름 유일·출발 그룹 하나인 것만. SQL·이름 기준·문자열 이름 호출 대상은 경고만.
- `spikes/S6-planning`의 기존 파일(측정 기록)은 바꾸지 않는다. 예비 측정 실행기는 새 파일로 더한다.
- `HttpClient` 생성은 `src/PellyCoder.Cli/Composition.cs`에만(`NetworkPolicyTests`).
- 사용자 문구 한국어, 코드 식별자·주석 영어. 커밋 끝 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. push하지 않는다.

## Review Focus

1. **모델이 같은 번호를 두 번 쓰거나 번호를 건너뜀** — 기존 검사기가 중복 ID를 오류로 낸다. 더하는 영향 줄 번호는 기존 최대 번호 다음부터라 충돌이 없어야 한다. 테스트: Task 3 `ImpactLines_NumberAfterHighestId`.
2. **모델이 작업 줄 앞뒤에 설명 문장·코드 펜스를 섞음** — 작업 줄처럼 시작하지 않는 줄(설명)은 버린 줄로 세지 않고 조용히 무시, 작업 줄 모양(`- [ ]`로 시작)인데 문법 밖인 줄만 버린 줄로 보인다. 테스트: Task 3 `Prose_IsIgnored_BrokenTaskLine_IsReported`.
3. **`--scope`가 작업 공간 밖이나 없는 경로** — 모델 호출 전 종료 4와 경로를 밝힌 문구. 테스트: Task 1 `Scope_OutsideWorkspace_Rejected`, `Scope_Missing_Rejected`.
4. **초안 작업이 0개**(모델이 아무 줄도 안 냄, 또는 전부 버림) — 쓸 것이 없다는 안내, 승인 질문 없이 종료 1, 파일 없음. 테스트: Task 4 `NoTasks_Exit1_WritesNothing`.
5. **같은 id의 `.pelly/drafts/<id>.md`가 남아 있음**(이전 실행이 중단) — 덮어쓰고 진행, 끝나면 지움. 테스트: Task 3 `StaleDraftFile_IsReplacedAndRemoved`.

---

## 파일 구조

| 파일 | 책임 |
| :--- | :--- |
| `src/PellyCoder.Core/Specs/Drafting/DraftIndex.cs` | 범위 → 색인, `Render()` |
| `src/PellyCoder.Core/Specs/Drafting/DraftPrompt.cs` | S6 시스템 프롬프트, 메시지 조립, 토큰 수 |
| `src/PellyCoder.Core/Specs/Drafting/DraftModel.cs` | 2단계 호출 |
| `src/PellyCoder.Core/Specs/Drafting/SpecDrafter.cs` | 초안 받기·문서·검사·영향 줄 |
| `src/PellyCoder.Cli/Commands/SpecDraftCommand.cs` | 명령 |
| `src/PellyCoder.Cli/Composition.cs` | `LlmBackendFactory` 등록 |
| `tests/fixtures/spec-draft/` | S6에서 만든 색인 기준 파일 |
| `spikes/S6-planning/S6Planning/ProductArm.cs` (새) | 예비 측정: 제품 경로로 12사례 |

---

### Task 1: 색인 (`DraftIndex`)

**Files:** Create `src/PellyCoder.Core/Specs/Drafting/DraftIndex.cs`, `tests/PellyCoder.Core.Tests/Specs/Drafting/DraftIndexTests.cs`, `tests/fixtures/spec-draft/index-b1.txt`, `tests/fixtures/spec-draft/index-d1.txt`.

**Interfaces — Produces:**

```csharp
namespace PellyCoder.Core.Specs.Drafting;
public sealed record DraftIndexMember(string TypeName, string Name, string Signature);
public sealed record DraftIndexFile(string Path, string Kind, IReadOnlyList<DraftIndexMember> Members); // Kind "cs"|"sql", Path relative to workspace root with '/'
public sealed record DraftIndex(IReadOnlyList<DraftIndexFile> Files, IReadOnlyList<string> Skipped) // Skipped: workspace-relative .sql paths that did not parse
{
    public string Render(); // byte-identical to S6 CorpusIndex.Render for the same files
}
public sealed record DraftScopeError(string Message);
public static class DraftIndexBuilder
{
    // scopes: paths as the user typed them (file, folder or .csproj), resolved against cwd; all must be inside ws.Root and exist.
    public static (DraftIndex? Index, DraftScopeError? Error) Build(IFileSystem fs, Workspace ws, IReadOnlyList<string> scopes, int sqlCompatLevel);
}
```

Rules: a folder scope takes `.cs` and `.sql` under it, skipping the folders the existing `BoundedWalker` skips; a `.csproj` scope takes its folder; files ordered by relative path (ordinal), each file once. C# members and signatures exactly as S6 `CorpusIndexBuilder` (constructors excluded, same signature text). SQL as S6 but parsed with the parser for `sqlCompatLevel`; a file with parse errors goes to `Skipped` instead of throwing.

- [ ] **Step 1: Generate the golden files once from the spike** — `cd /Users/payletter/git-root/Pelly-Code-spec-draft && dotnet run --project spikes/S6-planning/S6Planning -- index b1-block-cancel-shipped > tests/fixtures/spec-draft/index-b1.txt` and the same for `d1-modern-cancel-shipped` → `index-d1.txt`. Record in the test file comment which scope each case used (read `spikes/S6-planning/cases/<id>/gold.md` for its scope) and the command. If the spike's `index` output includes anything besides `Render()`, keep only the `Render()` part.
- [ ] **Step 2: Write failing tests**

```csharp
[Fact] Render_MatchesS6_B1()      // Build over the b1 scope under eval/corpus (same paths S6 used) → Render() == index-b1.txt bytes
[Fact] Render_MatchesS6_D1()
[Fact] Folder_TakesCsAndSql_SkipsBinObj()   // MockFileSystem: src/a.cs, src/bin/x.cs, src/obj/y.cs, db/p.sql → only src/a.cs (+ db when scoped)
[Fact] Csproj_TakesItsFolder()
[Fact] Paths_AreWorkspaceRelativeWithSlash()
[Fact] UnparsableSql_IsSkippedNotThrown()  // Skipped == ["db/bad.sql"], other files present
[Fact] Scope_OutsideWorkspace_Rejected()  // Error.Message contains the path and "작업 공간 밖"
[Fact] Scope_Missing_Rejected()           // Error.Message contains "찾을 수 없습니다"
[Fact] SameFileTwice_ListedOnce()         // scope folder + file inside it
```

- [ ] **Step 3: RED** — `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*DraftIndexTests"` → compile errors.
- [ ] **Step 4: Implement** by porting `CorpusIndex.Render` and `CorpusIndexBuilder` member/SQL extraction (cite the S6 file in a comment); do not port `Exists`, `TargetKey` or the `"Sql"` scope shortcut.
- [ ] **Step 5: GREEN**, full gate, commit `feat(spec): draft index ported from the S6 corpus index`.

---

### Task 2: 프롬프트와 2단계 호출

**Files:** Create `DraftPrompt.cs`, `DraftModel.cs`; Test `tests/PellyCoder.Core.Tests/Specs/Drafting/DraftModelTests.cs`.

**Interfaces — Consumes:** `DraftIndex.Render()`. **Produces:**

```csharp
public static class DraftPrompt
{
    public const string System = /* S6 PlanPrompt.System, verbatim */;
    public static readonly IReadOnlyList<string> ExampleNames; // S6 ExampleNames
    public static string User(string request, string renderedIndex);   // "Index:\n" + index + "\nRequest:\n" + request
    public const int InputBudget = 4096;
}
public sealed record DraftCall(string Stage1Prompt, string Think, bool ThinkTruncated, string Answer, int InputTokens, double Seconds);
public sealed class DraftModel(ILlmBackend backend, QwenChatTemplate template, TimeProvider time)
{
    public int CountInput(string request, string renderedIndex);  // tokens of template.Build(System, User(...)) + ThinkOpen
    public Task<DraftCall> RunAsync(string request, string renderedIndex, CancellationToken ct, Action<TimeSpan>? onWaiting = null);
}
```

`RunAsync`: stage 1 prompt = `template.Build(System, User) + template.ThinkOpen`, request (0.6, 0.95, 20, 1.0, 0.0), 2048, stop `[template.ThinkClose]`; `ThinkTruncated` = stage 1 hit `num_predict` (done reason or eval count as `QwenStreamClient` reports it); stage 2 prompt = stage1 + think text **as returned** + `template.ThinkClose` (S6 `Runner.Stage2Prompt`), request (0.1, 0.9, 20, 1.05, 0.0), 1024, stop `[template.EndOfTurn]`. Both with `NumCtx` 8192, `KeepAlive` "10m". Use `QwenStreamClient.ReadAsync` (internal, same assembly).

- [ ] **Step 1: Failing tests** (fake `ILlmBackend` recording `LlmRequest`s and returning scripted chunks):

```csharp
[Fact] System_IsS6PromptVerbatim()           // DraftPrompt.System == text read from spikes/S6-planning/S6Planning/PlanPrompt.cs between the """ markers
[Fact] TwoStages_UseS6Settings()             // 2 requests; sampling, NumPredict 2048/1024, Stop, NumCtx 8192, KeepAlive "10m" as above
[Fact] Stage2_ConcatenatesThinkVerbatim()    // think " a\nb  " (trailing spaces kept) → stage2 == stage1 + " a\nb  " + ThinkClose
[Fact] TruncatedThink_IsReportedAndKept()
[Fact] CountInput_MatchesTokenCounter()      // == QwenTokenCounter.Shared.Count(stage1 prompt)
```

- [ ] **Step 2–5:** RED (`--filter-class "*DraftModelTests"`), implement, GREEN + full gate, commit `feat(spec): S6 planning prompt and two-stage draft call`.

---

### Task 3: 조립 (`SpecDrafter`)

**Files:** Create `SpecDrafter.cs`; Test `tests/PellyCoder.Core.Tests/Specs/Drafting/SpecDrafterTests.cs`, `tests/PellyCoder.Integration.Tests/Specs/SpecDraftImpactTests.cs`.

**Interfaces — Consumes:** Task 1–2, `ChangeSpecParser`, `SpecChecker.CheckAsync(fs, specPath, compat, impact, impactAllCsTasks: false, ct, sql)`, `ImpactResult.Targets`, `ImpactMode`. **Produces:**

```csharp
public sealed record DraftRequest(string Id, string Request, IReadOnlyList<string> Scopes);
public sealed record AddedImpactTask(string TaskId, string Path, string Member, string FromTaskId);
public sealed record DraftOutcome(
    string Document,                       // full changes/<id>.md text (\n, no BOM)
    IReadOnlyList<string> DroppedLines,    // task-shaped lines that failed the grammar, verbatim
    IReadOnlyList<AddedImpactTask> Added,
    SpecCheckResult Check,                 // the final (re)check
    bool ImpactSkippedForErrors,
    DraftCall Call,
    DraftIndex Index);
public sealed record DraftFailure(int ExitCode, string Message); // 4 for scope/budget/env, 1 for "no tasks"
public sealed class SpecDrafter(IFileSystem fs, DraftModel model, IImpactFinder impact, SqlImpactSetup? sql, int sqlCompatLevel)
{
    public Task<(DraftOutcome? Outcome, DraftFailure? Failure)> DraftAsync(Workspace ws, DraftRequest request, CancellationToken ct, Action<TimeSpan>? onWaiting = null);
}
```

Document shape:

```
---
id: <id>
status: draft
---
# <first line of request, cut at 60 chars>

## 목표
<request verbatim>

## 수용 기준
- AC1: 이 변경이 끝났을 때 참이어야 하는 것

## 작업
<!-- spec check: 오류 N, 경고 M ... -->   (only when the final check has errors)
<task lines>
```

Impact line text: `- [ ] T<k> cs <path>#<member> T<from>의 변경에 맞춰 이 코드를 고친다 (영향 탐색으로 추가) [G<g>]`, inserted after the last task of group `g`, `k` from highest existing number + 1 in insertion order. Selection rules: Global Constraints + design §4.4. Checking: write `.pelly/drafts/<id>.md` (create dir), run `CheckAsync`, delete the file in `finally`.

- [ ] **Step 1: Failing tests** (MockFileSystem workspace with a small C# file pair and a fake `IImpactFinder` returning scripted targets; fake backend from Task 2):

```csharp
[Fact] ScopeOverBudget_NoModelCall_Exit4()        // Failure.ExitCode 4, message lists the biggest files and "--scope를 좁히십시오", backend calls 0
[Fact] Prose_IsIgnored_BrokenTaskLine_IsReported() // answer: "Here is the plan:\n- [ ] T1 cs a.cs#M do x\n- [ ] T2 cs\n```" → 1 task, DroppedLines == ["- [ ] T2 cs"]
[Fact] Document_HasTemplateSections()
[Fact] NoGroup_NoImpactSearch()                   // fake impact finder never called
[Fact] GroupSeed_AddsSemanticCallers()            // T1 [G1]; targets: caller B.cs#Use (호출) → line added after T1 with [G1] and "(영향 탐색으로 추가)"
[Fact] ImpactLines_NumberAfterHighestId()         // tasks T1,T3 [G1] → added T4
[Fact] NameBasedResult_AddsNothing()              // Mode Name → Added empty, warning kept
[Fact] OutsideMember_Ambiguous_MultiGroup_Sql_AddNothing()  // one case each, Added empty
[Fact] CheckErrors_SkipImpact()                   // invented member → ImpactSkippedForErrors true, Added empty
[Fact] AddedLine_KeepsGroupWhenParsed()           // parse Document → added task has Group 1 and its Request has no "[G1]"
[Fact] StaleDraftFile_IsReplacedAndRemoved()      // .pelly/drafts/<id>.md pre-exists → gone after DraftAsync
[Fact] ChangesFolder_Untouched()                  // changes/ does not exist after DraftAsync
```

Integration (`SpecDraftImpactTests`, real semantic finder like the existing impact integration tests and fixture `tests/fixtures/impact/…`): `[Fact] RealSemanticFinder_AddsCallerOfChangedSignature()` with the same trait/skip rules as the existing impact integration tests.

- [ ] **Step 2–5:** RED, implement, GREEN + full gate, commit `feat(spec): SpecDrafter checks the model draft and adds impact tasks by code`.

---

### Task 4: `pelly spec draft` 명령

**Files:** Create `src/PellyCoder.Cli/Commands/SpecDraftCommand.cs`; Modify `src/PellyCoder.Cli/CliApp.cs` (`spec` branch `AddCommand<SpecDraftCommand>("draft")`, `CommandSettings["spec draft"]`), `src/PellyCoder.Cli/Composition.cs` (`public delegate ILlmBackend LlmBackendFactory(LlmOptions llm);` registered with the existing `HttpClientHolder`), `tests/PellyCoder.Cli.Tests/ExitCodeMappingTests.cs` (registry key list); Test `tests/PellyCoder.Cli.Tests/SpecDraftCommandTests.cs`.

**Settings:** `<ID>`, `[REQUEST]`, `--scope <PATH>` (repeatable, required), `--dry-run`, `--yes`, `--force`, `--config`, `--model`, `--endpoint`, `--pause-on-exit`. Validation: ID as `spec new`; no scope → 4 「`--scope`가 하나 이상 필요합니다」; request omitted → prompt if interactive else 4; control-token strings → 4 before any call; `changes/<id>.md` exists without `--force` → 4.

Flow: print index summary (`색인: 파일 N개, 멤버 M개, T 토큰 (예산 4096)`, skipped `.sql` as warnings) → waiting line while the model runs (`ShowWaiting`-style status) → draft tasks → dropped lines → added impact lines → check issues → if no tasks: 「초안에 작업이 없습니다」 exit 1 → `--dry-run`: stop, exit 0 → question `changes/<id>.md에 쓸까요?` (mention 「덮어씁니다」 with `--force`; default N; with errors print 「오류가 있는 명세는 `pelly apply`가 실행하지 않습니다. 쓴 뒤 고치십시오」 first) → yes: write UTF-8 no BOM, exit 0; no: exit 2. Non-interactive without `--yes`/`--dry-run` → 4. Run record: `.pelly/runs/<RunLogger.NewRunId>/draft.json` with request, scopes, index tokens, stage1 prompt, think, answer, dropped lines, added lines, issues, outcome, written path (connection strings never appear here).

- [ ] **Step 1: Failing tests** (CommandAppTester, fake `LlmBackendFactory`, MockFileSystem workspace, fake impact finder via DI as `SpecCheckCommand` tests do):

```csharp
[Fact] Approve_WritesChangesFile_Exit0()
[Fact] Reject_WritesNothing_Exit2()
[Fact] DryRun_WritesNothing_Exit0()
[Fact] Existing_WithoutForce_Exit4_NoModelCall()
[Fact] Existing_WithForceAndYes_Overwrites()
[Fact] NoScope_Exit4()
[Fact] NoTasks_Exit1_WritesNothing()
[Fact] ControlToken_Exit4_NoModelCall()
[Fact] NonInteractiveWithoutYes_Exit4()
[Fact] Errors_WarnBeforeQuestion_AndCommentInFile()
[Fact] RunRecord_Written()                        // .pelly/runs/*/draft.json has answer and dropped lines
[Fact] WrittenFile_ReadsBackInSpecCheck()         // spec check on the written file → same issue codes as the final check
```

- [ ] **Step 2–5:** RED, implement, GREEN + full gate, commit `feat(cli): pelly spec draft writes an approved model draft to changes/<id>.md`.

---

### Task 5: 문서와 PRD

**Files:** `docs/PRD.md` (§12.1 두 줄, §12.2 로드맵 V2-4 행, §12.7 첫 줄, 새 §12.8, FR-00 명령 한 줄, 변경 이력 v1.20 — 문구는 설계 §6 그대로), `README.md` (`pelly spec draft` 절), `deploy/usage.md` (`spec`·`apply` 절에 초안 하위 절: 예, `--scope` 규칙, 예산 초과 안내, 승인, 영향 줄 표시, 알려진 한계 — 생성자는 색인에 없음, AC는 쓰지 않음), `deploy/field-results.md` (현장 모델로 S6식 확인 한 칸: 실제 요청 3건으로 초안을 받아 「고치지 않고 쓸 만함 / 고쳐서 씀 / 못 씀」을 적는 표).

- [ ] **Step 1:** edit; **Step 2:** `dotnet test` (doc/package tests) 0 failures; **Step 3:** commit `docs: pelly spec draft, PRD V2-4 and the §12.7 change`.

---

### Task 6: 예비 측정 (로컬 모델)

**Files:** Create `spikes/S6-planning/S6Planning/ProductArm.cs`, register a new `run --arm P` path in `Program.cs` only by adding a case (existing arms untouched); results `spikes/S6-planning/raw/P/…`; report `docs/spec-draft-premeasure.md`.

Arm P runs each of the 12 approved cases × 5 through `DraftModel` + `SpecDrafter` (real semantic impact finder; SQL impact off) with the case's S6 scope, and stores the **final task list** (model lines + added impact lines) in the same raw record shape, so the existing `score` command scores P like A/B. Model must be `qwen3.6:35b` with the S6 digest prefix check (`Runner.CheckDigest`); if Ollama or the model is absent, skip and write that in the report.

- [ ] **Step 1:** implement arm P; smoke one case.
- [ ] **Step 2:** run `run --arm P --repeat 5` and `score`, `report`.
- [ ] **Step 3:** write `docs/spec-draft-premeasure.md`: conditions (digest, date, commit), category table P vs S6 A/B, category A recall with impact lines vs S6 B 0.33, dropped-line count, parser-leniency note (design §4.3), 「예비 — 사람 승인 전 수치, 현장 모델 아님」.
- [ ] **Step 4:** commit `docs: spec draft preliminary measurement on the S6 cases`.
