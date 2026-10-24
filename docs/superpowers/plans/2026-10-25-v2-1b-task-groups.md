# V2-1b 작업 그룹 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 명세의 `[G<n>]` 작업 그룹을 실행한다. 그룹 안 작업은 대상 멤버의 시그니처·이름을 바꿀 수 있다. 빌드 검증은 그룹 끝에 영향 범위 전체로 한 번 한다. 새 오류는 해당 작업에 되돌려 다시 실행하고, 실패하면 그룹 전체를 되돌린다.

**Architecture:**
- FSM에는 「그룹 모드」(`RunRequest.Group`)만 더한다. 그룹 모드에서 FSM은 다음과 같이 동작한다.
  - 기준 빌드와 빌드 검증을 하지 않는다.
  - 모양 검사가 대상 멤버의 시그니처·이름 변경을 허용한다.
  - 바뀐 이름으로 대상을 계속 추적한다.
  - 그룹용 시스템 프롬프트를 쓴다.
- 그룹 단위 일은 apply 수준(`Specs/`)에서 한다. 영향 범위 계산, 기준·검증 빌드, 오류 귀속, 재실행, 그룹 되돌리기가 여기에 속한다.
- v1 단일 실행과 V2-1a 비그룹 apply의 동작은 바뀌지 않는다.

**Tech Stack:** .NET 10, Roslyn(Microsoft.CodeAnalysis.CSharp 5.9.0), System.IO.Abstractions, xunit v3.

**Spec:** `docs/superpowers/specs/2026-10-24-v2-1-apply-design.md` (§4 그룹 규칙, §6 「작업 그룹」 전부, §6 승인·실패·이어서 하기의 그룹 관련 줄). 영향 탐색(§5의 5)과 E2E 벤치마크(§8)는 V2-1c.

## Global Constraints

- **v1·V2-1a 보존:** v1 `pelly cs`·`pelly sql`과 V2-1a 비그룹 apply의 동작·프롬프트·종료 코드·기존 테스트는 바뀌지 않는다. `RunRequest.Group`이 null이면 모든 단계가 지금과 같다. `dotnet build -warnaserror` 경고 0, 전체 테스트 실패 0이어야 한다.
- **종료 코드:** v1과 같다(0 성공, 1 검증 실패, 2 사용자 취소·거부, 3 대상 특정 실패, 4 환경·입력 오류, 5 안전장치 거부).
  - 그룹 빌드로 검증할 수 없으면 4다. 이 경우 아무 파일도 바꾸기 전에 끝낸다.
  - 그룹 빌드 실패(재시도 소진, 또는 명세에 빠진 영향 대상)는 1이다.
  - 그룹 승인 거부는 2다.
- **그룹 허용 변경**(설계 §6 Claude 결정):
  - 허용: 대상 멤버 자신의 반환 형식·매개변수·제한자·특성 변경, 그리고 대상 멤버의 이름 변경
  - 금지: 다른 멤버의 변경·삭제, `using` 삭제, public 멤버 추가. private 메서드 추가는 v1처럼 허용한다.
- **그룹 프롬프트:** v1 규칙 5만 바꾼다. 바뀐 문장은 「Change this member's signature only as far as the request asks; you may rename it. Do not change any other member. You may add a private helper method right after the target member.」이다.
  - 그룹용 시스템 프롬프트(few-shot 포함)는 기본 `budget.system`(450 토큰) 안에 들어야 한다.
- **영향 범위:** 그룹 작업 파일이 속한 프로젝트(가장 가까운 `.csproj`)와, 작업 공간 안에서 그 프로젝트를 직접·간접으로 `ProjectReference`하는 프로젝트들이다.
  - 프로젝트마다 형식에 맞는 빌드 도구(`BuildToolResolver`)를 쓴다.
  - 그룹 기준 빌드는 그룹 첫 작업 전에, 그룹 검증 빌드는 마지막 작업 뒤에 한다. 새 오류 판정은 `BaselineDiff.New`다.
- **검증 불가(종료 4, 편집 전):** 다음 중 하나면 검증할 수 없다.
  - 영향 범위에 레거시 프로젝트가 있는데 Windows가 아니다.
  - 빌드 도구가 없다.
  - 기준 빌드에 컴파일러가 아닌 오류(`!BuildMessages.IsCompilerError`)가 있다.
  - 빌드 시간이 초과됐다.
  - 기준 빌드를 시작하지 못했다.
- **오류 귀속:** 새 오류의 파일·줄을 감싼 멤버가 그룹 작업의 (현재) 대상이면, 그 작업에 `FeedbackBuilder.ForBuild` 피드백을 주고 다시 실행한다.
  - 그렇지 않으면(대상 아닌 멤버, 멤버 밖, 그룹 밖 파일, 파일 없는 오류) 「명세에 빠진 영향 대상」이다. 그룹 전체를 되돌리고 종료 1로 끝낸다. 메시지에 `<상대 경로>#<멤버>` 목록을 적는다.
  - 재실행은 그룹 전체에 `retry.max`(실행 옵션 `--max-retries`가 있으면 그 값)번이다.
- **그룹 되돌리기:** 그룹이 바꾼 모든 파일을 그룹 시작 전 바이트로 되돌린다.
  - pelly 밖에서 바뀐 파일은 덮어쓰지 않고 목록으로 알린다(종료 4).
  - 그룹 백업 위치는 `.pelly/backup/<apply-id>/<그룹 ID>/<상대 경로>`다.
- **체크 표시:**
  - 그룹이 통과하고 승인된 뒤 그룹 작업을 한꺼번에 `[x]`로 바꾼다.
  - 그룹이 실패하면 그룹의 모든 작업에 `<!-- failed: <run-id> <실패 종류> -->`를 붙인다. 실패 종류는 `GroupUnverifiable`, `GroupBuildFailed`, `GroupMissingTarget`, `GroupRejected`, 또는 그룹 안 작업의 FSM 실패 종류다.
- **승인:**
  - 그룹은 그룹 빌드 통과 뒤 그룹 전체 diff(그룹 시작 전 → 지금)를 한 번에 승인받는다.
  - `--yes`면 묻지 않는다.
  - `--review-at-end`면 그룹 승인을 건너뛰고 끝의 전체 승인에 맡긴다.
- **`--continue`:** 그룹이 실패한 뒤, 그 뒤 작업 중 그룹 파일을 대상으로 하는 것이 있으면 멈춘다. 종료 4(환경)도 멈춘다.
- **`--resume`:** 그룹 작업은 그룹이 실패하면 `[x]`가 없으므로, 그룹 첫 작업부터 다시 한다.
- **`--dry-run`:** 첫 대상 작업이 그룹이면 그 작업 하나만 그룹 모드·드라이런으로 실행한다(빌드 없음).
- **문구·커밋:** 사용자 문구는 한국어, 코드 주석은 영어다. 커밋 꼬리는 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`다.

## Review Focus

1. **명세에 빠진 호출부가 깨지는 그룹:** 그룹 밖 파일에서 새 오류가 나면 재시도하지 않는다. 그룹 전체를 되돌리고 종료 1로 끝내며, `파일#멤버`를 알려야 한다. → Task 7 `Group_error_outside_group_targets_rolls_back_and_names_missing_target`.
2. **이름 변경 뒤 재시도:** FSM 안 재시도와 그룹 재실행 모두 바뀐 이름으로 대상을 다시 찾아야 한다. → Task 3 `Rename_then_verify_failure_retries_on_the_renamed_member`, Task 7 `Group_rerun_targets_the_renamed_member`.
3. **비 Windows에서 영향 범위에 레거시 프로젝트가 있는 경우:** 아무것도 바꾸기 전에 종료 4다. 구문 검사만으로 통과시키지 않는다. → Task 5 `Legacy_in_scope_off_windows_is_unverifiable`, Task 7 `Unverifiable_group_exits_4_before_any_edit`.
4. **그룹 실행 중 IDE에서 저장한 파일:** 그룹 되돌리기가 그 파일을 덮어쓰지 않는다. → Task 6 `Group_rollback_skips_file_changed_outside`.
5. **기준 빌드에 이미 있던 CS 오류:** 새 오류만 실패로 센다. 컴파일러가 아닌 기준 오류는 편집 전 종료 4다. → Task 5 `Only_new_errors_count`, `Non_compiler_baseline_error_is_unverifiable`.

---

### Task 1: 프로젝트 참조 그래프와 영향 범위

**Files:**
- Create: `src/PellyCoder.Core/Builds/ProjectReferenceGraph.cs`
- Modify: `src/PellyCoder.Core/Testing/TestProjectLocator.cs`(private `References`를 새 클래스로 대체, 동작 동일)
- Test: `tests/PellyCoder.Core.Tests/Builds/ProjectReferenceGraphTests.cs`

**Interfaces:**
- Produces:
  - `static IReadOnlyList<string> ProjectReferenceGraph.References(IFileSystem fs, string csprojPath)`
    - `ProjectReference`의 `Include`를 전체 경로로 돌려준다(`\` 정규화). XmlException이면 빈 목록이다.
    - 지금 `TestProjectLocator.References`의 동작을 그대로 옮긴다.
  - `static IReadOnlyList<string> ProjectReferenceGraph.AffectedProjects(IFileSystem fs, string workspaceRoot, IReadOnlyCollection<string> changedProjects)`
    - 바뀐 프로젝트와, 그것을 직접·간접 참조하는 작업 공간 안 프로젝트들을 돌려준다.
    - 후보는 `BoundedWalker.Files(fs, root, "*.csproj", 2000)`다.
    - 순서는 「바뀐 프로젝트를 먼저(입력 순서), 그다음 참조하는 프로젝트를 경로 서수 순」이고, 중복은 없다.
    - 경로 비교는 `SpecTaskPaths.Comparer`와 같은 규칙(Windows·macOS 대소문자 무시)을 쓴다. 그 비교자를 `Builds`에서 쓸 수 있게 `PathComparers.ForOs`로 옮겨 두 곳이 같은 것을 쓴다.

- [ ] **Step 1: 실패하는 테스트**
  - `References_reads_includes_with_backslashes`
  - `References_of_broken_xml_is_empty`
  - `Affected_includes_transitive_dependents_only`: 프로젝트는 Lib ← App ← Web, 그리고 Other(독립)다. `AffectedProjects([Lib])`의 결과는 `[Lib, App, Web]`이고, Other는 없다.
  - `Affected_handles_reference_cycles`: A ↔ B 순환이어도 끝나고 중복이 없다.
  - `TestProjectLocator` 기존 테스트 전부가 그대로 통과한다(리팩터 확인).
- [ ] **Step 2: 실패 확인** — `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*ProjectReferenceGraphTests"` → 컴파일 실패.
- [ ] **Step 3: 구현.**
- [ ] **Step 4: 통과 확인** — 위 명령 통과, `*TestProjectLocator*` 통과.
- [ ] **Step 5: 커밋** — `feat(builds): project reference graph and affected projects`

### Task 2: 그룹 모양 검사(시그니처·이름 변경 허용)

**Files:**
- Modify: `src/PellyCoder.Core/CSharp/MemberShapeGuard.cs`
- Test: `tests/PellyCoder.Core.Tests/CSharp/MemberShapeGuardTests.cs`(추가)

**Interfaces:**
- Produces:
  - `enum ShapeAllowance { MemberBodyOnly, TargetSignatureAndName }`
  - `ShapeCheck`에 `string? NewTargetDisplay`(끝 매개변수, 기본 null)를 더한다. 통과 시 바뀐 뒤 대상의 Display다.
  - `MemberShapeGuard.Check(string beforeLf, string afterLf, string targetDisplay, LanguageVersion version, ShapeAllowance allowance = ShapeAllowance.MemberBodyOnly)`

규칙:
- `MemberBodyOnly`는 지금과 바이트 단위로 같은 판정이다(기존 테스트 전부 통과). 통과 시 `NewTargetDisplay`도 채운다.
- `TargetSignatureAndName`:
  - 대상의 `SignatureOf`·`AttributesOf` 차이를 거부하지 않는다.
  - 대상 짝짓기는 같은 Key 우선이다. 없으면 「before에 없던 Key이고, 같은 Kind 낱말·같은 Scope이며, 포함 타입 안의 멤버 순서상 대상과 같은 자리」인 항목 하나를 새 대상으로 본다. 이름이 달라도 된다(이름 변경).
  - 짝이 없거나 둘 이상이면 「member X was removed or changed」로 거부한다.
  - 그 밖의 규칙(다른 멤버, using 삭제, private 메서드 외 추가)은 같다.

- [ ] **Step 1: 실패하는 테스트**
  - `Group_allows_return_type_change_and_reports_new_display`: `Task Cancel(int id)` → `Task<decimal> Cancel(int id)`면 Ok이고, `NewTargetDisplay`는 바뀐 Display다.
  - `Group_allows_rename_in_place`: `OrderDto GetOrder(int id);`(인터페이스) → `OrderDto FindOrder(int id);`면 Ok이고, `NewTargetDisplay`에 `FindOrder`가 있다.
  - `Group_still_rejects_other_member_change`
  - `Group_rejects_rename_plus_moved_member`: 대상 이름을 바꾸면서 다른 자리로 옮기면 거부한다(같은 자리 규칙).
  - `Group_still_rejects_public_addition`
  - `Default_allowance_still_rejects_signature_change`: v1과 같은 메시지다.
- [ ] **Step 2: 실패 확인.**
- [ ] **Step 3: 구현.**
- [ ] **Step 4: 통과 확인** — `*MemberShapeGuardTests*`, `*ShapeGuardFlowTests*` 전부.
- [ ] **Step 5: 커밋** — `feat(csharp): shape guard allowance for group signature and rename changes`

### Task 3: FSM 그룹 모드

**Files:**
- Modify: `src/PellyCoder.Core/Fsm/RunRequest.cs`
  - `record GroupRun(Feedback? InitialFeedback)`를 추가한다.
  - `RunRequest`에 끝 매개변수 `GroupRun? Group = null`을 더한다.
  - `RunResult`에 끝 매개변수 `string? TargetDisplay = null`을 더한다.
- Modify: `Phases/LocatePhase.cs`(그룹이면 기준 빌드·BuildTool을 만들지 않음), `Phases/ApplyPhase.cs`(그룹이면 `TargetSignatureAndName`으로 검사하고, 통과한 패치를 쓴 뒤 `ctx.Candidate`·`ctx.Target`을 `NewTargetDisplay`로 갱신), `Phases/SlicePhase.cs`(그룹이고 `ctx.Candidate`가 있으면 Request.MethodName 대신 `ctx.Candidate`의 이름·포함 타입으로 다시 찾음), `FsmOrchestrator.cs`(시작 시 `ctx.Feedback = Request.Group?.InitialFeedback`, RunResult에 `ctx.Candidate?.Display`)
- Test: `tests/PellyCoder.Core.Tests/Fsm/GroupModeFlowTests.cs`

**Interfaces:**
- Consumes: Task 2 `ShapeAllowance`, `ShapeCheck.NewTargetDisplay`.
- Produces: `GroupRun`, `RunRequest.Group`, `RunResult.TargetDisplay`. 그룹 모드에서는 빌드·테스트 단계가 통과로 처리된다(BuildTool이 null이므로 BuildStep이 Pass).

- [ ] **Step 1: 실패하는 테스트** — `BuildVerificationFlowTests`의 픽스처와 Scripted 가짜를 쓴다.
  - `Group_run_starts_no_baseline_and_no_build`: 프로세스 호출 0회, 종료 0.
  - `Group_run_accepts_signature_change`: 반환 형식을 바꾸는 패치가 종료 0이고, `RunResult.TargetDisplay`가 새 Display다.
  - `Rename_then_verify_failure_retries_on_the_renamed_member`:
    - 1회차 패치는 이름 변경과 구문 오류를 함께 담는다(SyntaxStep 실패).
    - 2회차는 바뀐 이름의 멤버를 SEARCH 대상으로 고친다.
    - 결과는 종료 0이다. 2회차 프롬프트의 EDITABLE target에 새 이름이 있다.
  - `Initial_feedback_reaches_first_prompt`: `GroupRun(new Feedback("BUILD FAILED (1 new errors)…", n))`면 첫 요청 프롬프트에 그 문구가 있다.
  - `Non_group_run_is_unchanged`: 같은 시그니처 변경 패치를 그룹 없이 실행하면 v1처럼 ShapeRejected 재시도 뒤 종료 1이다.
- [ ] **Step 2: 실패 확인.**
- [ ] **Step 3: 구현.**
- [ ] **Step 4: 통과 확인** — `dotnet test tests/PellyCoder.Core.Tests` 전체.
- [ ] **Step 5: 커밋** — `feat(fsm): group mode — no build, signature and rename allowance, follow the renamed target, initial feedback`

### Task 4: 그룹용 시스템 프롬프트

**Files:**
- Create: `src/PellyCoder.Core/Llm/Resources/prompts/system.cs.group.txt`. 머리 줄은 `# version: 2`이고, 내용은 `system.cs.txt`와 같되 규칙 5만 Global Constraints의 문장으로 바꾼다.
- Modify: `PromptResources.cs`(필드 `string SystemCsGroup = ""`, LoadDefault가 읽음), `PromptBuilder.cs`(`BuildCSharp(..., IReadOnlyList<ProjectRule>? rules = null, bool group = false)`가 `group`이면 `SystemCsGroup` 사용), `Phases/EditPhase.cs`(`ctx.Request.Group is not null`을 넘김), 임베디드 리소스 등록(csproj 관례 확인)
- Test: `tests/PellyCoder.Core.Tests/Llm/SystemPromptBudgetTests.cs`(`CSharpGroup_SystemAndFewShot_FitDefaultSystemBudget` 추가), `PromptBuilderTests.cs`(`Group_prompt_differs_only_in_rule_5`)

**Interfaces:** Consumes Task 3 `RunRequest.Group`. 프롬프트 버전 문자열은 `cs-v2` 그대로 둔다.

- [ ] **Step 1: 실패하는 테스트**
  - `CSharpGroup_SystemAndFewShot_FitDefaultSystemBudget`: 기존 C# 테스트와 같은 식이고, ≤ 450이다.
  - `Group_prompt_differs_only_in_rule_5`: `SystemCs`와 `SystemCsGroup`을 줄 단위로 비교했을 때, 다른 줄은 `5.`로 시작하는 한 줄뿐이다.
  - `Group_flag_selects_group_system_prompt`: 그룹 프롬프트에 「you may rename it」이 있다.
- [ ] **Step 2: 실패 확인.**
- [ ] **Step 3: 구현.**
- [ ] **Step 4: 통과 확인** — `*Prompt*` 테스트 전부, Core 전체.
- [ ] **Step 5: 커밋** — `feat(llm): group system prompt variant (rule 5)`

### Task 5: 그룹 빌드 검증과 오류 귀속

**Files:**
- Create: `src/PellyCoder.Core/Specs/GroupVerifier.cs`, `src/PellyCoder.Core/Specs/GroupErrorAttribution.cs`
- Test: `tests/PellyCoder.Core.Tests/Specs/GroupVerifierTests.cs`, `GroupErrorAttributionTests.cs`

**Interfaces:**
- Consumes: Task 1 `ProjectReferenceGraph.AffectedProjects`, 기존 `BuildVerifier.BuildAsync(tool, csproj, root, ct)`, `BuildToolResolver.ResolveAsync(style, ct)`, `ProjectStyleDetector.Detect`, `BaselineDiff.New`, `BuildMessages.IsCompilerError`, `MemberLocator`.
- Produces:
  - `record GroupBuild(bool Verifiable, string? Reason, string? FailureKind, IReadOnlyList<Diagnostic> Errors)`
  - `interface IGroupVerifier { Task<GroupBuild> BuildAsync(Workspace ws, IReadOnlyList<string> projects, CancellationToken ct); }`
  - `sealed class GroupVerifier(IFileSystem fs, BuildVerifier builds, BuildToolResolver tools, Func<bool> isWindows) : IGroupVerifier`
    - `Task<GroupBuild> BuildAsync(Workspace ws, IReadOnlyList<string> projects, CancellationToken ct)` — 프로젝트를 차례로 빌드해 오류를 합친다. 검증 불가 조건(Global Constraints)이면 `Verifiable = false`, `FailureKind = "GroupUnverifiable"`로 돌려준다. 레거시+비 Windows는 빌드 전에 판정한다.
    - `static IReadOnlyList<Diagnostic> NewErrors(GroupBuild baseline, GroupBuild current)` = `BaselineDiff.New`
  - `record GroupMember(string TaskId, string FullPath, string TargetDisplay)`
  - `record Attribution(IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> ByTask, IReadOnlyList<(string Location, Diagnostic Error)> Unattributed)`
  - `static Attribution GroupErrorAttribution.Attribute(IFileSystem fs, Workspace ws, IReadOnlyList<Diagnostic> newErrors, IReadOnlyList<GroupMember> members, LanguageVersion version)`
    - 오류 파일(작업 공간 상대 경로)을 전체 경로로 바꿔 그룹 멤버 파일과 비교한다(OS 비교자).
    - 그 파일의 현재 텍스트에서 오류 줄을 감싼 멤버를 `MemberLocator.FindAtLine`으로 찾는다. 그것이 그 파일의 어느 그룹 작업 `TargetDisplay`와 같으면 그 작업에 귀속한다.
    - 아니면 미귀속이다. Location은 `<상대 경로>#<멤버 이름>`이고, 멤버가 없으면 `<상대 경로>:<줄>`, 파일이 없으면 오류 코드다.

- [ ] **Step 1: 실패하는 테스트**
  - `GroupVerifierTests`(ScriptedProcessRunner의 핸들러로 `spec.Arguments`의 csproj별 결과를 준다)
    - `Builds_every_project_and_merges_errors`
    - `Only_new_errors_count`: 기준 빌드에 CS0103 오류가 1개 있으면, 검증 빌드의 같은 오류는 `NewErrors`에 없고 새로 생긴 CS0029만 있다.
    - `Non_compiler_baseline_error_is_unverifiable`: NETSDK1004면 Verifiable false다.
    - `Legacy_in_scope_off_windows_is_unverifiable`: `isWindows = () => false`, 레거시 csproj가 있으면 빌드 호출 0회다.
    - `Timeout_is_unverifiable`
  - `GroupErrorAttributionTests`
    - `Error_inside_a_group_target_is_attributed_to_its_task`
    - `Error_in_another_member_of_a_group_file_is_unattributed_with_member_location`
    - `Error_in_a_file_outside_the_group_is_unattributed`
    - `Error_without_file_is_unattributed`
- [ ] **Step 2: 실패 확인.**
- [ ] **Step 3: 구현.**
- [ ] **Step 4: 통과 확인.**
- [ ] **Step 5: 커밋** — `feat(specs): group build verification and error attribution`

### Task 6: 그룹 되돌리기 지점

**Files:**
- Modify: `src/PellyCoder.Core/Specs/ApplyTransaction.cs`
- Test: `tests/PellyCoder.Core.Tests/Specs/ApplyTransactionTests.cs`(추가)

**Interfaces:**
- Produces(기존 API는 유지):
  - `void BeginGroup(string groupId)`
    - 이후 `Track`/`Record`로 처음 보는 파일, 또는 그룹 안에서 처음 보는 기존 파일은 그 시점 바이트를 `.pelly/backup/<apply-id>/<groupId>/<rel>`에 백업한다(`FileGuard.Backup(ws, $"{applyId}/{groupId}", snapshot)`).
    - 그룹 시작 시점의 LastKnown을 기억한다.
  - `Task<IReadOnlyList<string>> RollbackGroupAsync(CancellationToken ct)`
    - 그룹이 바꾼 파일을 그룹 시작 전 바이트로 되돌리고, 되돌리지 못한 경로를 돌려준다.
    - 바깥 변경(ChangedOutside)·백업 없음·예외는 건너뛰고 보고한다(V2-1a `RollbackAsync`와 같은 규칙).
    - 성공한 파일의 LastKnown은 그룹 시작 시점 값으로 돌아간다. 그래서 그 뒤 전체 되돌리기도 올바르다.
  - `void EndGroup()`
  - `IReadOnlyList<(string Path, string Before, string After)> GroupChanges()` — 그룹 시작 전 → 지금.

- [ ] **Step 1: 실패하는 테스트**
  - `Group_rollback_restores_group_start_not_apply_start`: T1(비그룹)이 a를 바꾸고, 그룹이 a·b를 바꾼다. 그룹을 되돌리면 a는 T1 결과, b는 원본이다.
  - `Group_rollback_skips_file_changed_outside`: 그룹 안에서 바깥 편집이 감지된 파일은 그대로 두고 목록으로 돌려준다.
  - `Full_rollback_after_group_rollback_returns_apply_originals`
  - `Group_changes_compare_against_group_start`
- [ ] **Step 2: 실패 확인.**
- [ ] **Step 3: 구현.**
- [ ] **Step 4: 통과 확인.**
- [ ] **Step 5: 커밋** — `feat(specs): group checkpoint and rollback in the apply transaction`

### Task 7: apply 실행기의 그룹 실행

**Files:**
- Modify:
  - `src/PellyCoder.Core/Specs/SpecRunner.cs`
  - `ITaskExecutor.cs`: `TaskRunOptions`에 끝 매개변수 `GroupRun? Group = null, string? MemberOverride = null`을 더한다.
  - `FsmTaskExecutor.cs`: `MemberOverride`가 있으면 `MethodName`으로 쓰고, `Group`을 RunRequest에 넣는다.
  - `SpecChecker.cs`: `GroupUnsupported`를 없앤다.
  - `src/PellyCoder.Cli/Commands/ApplyCommand.cs`: SpecRunner에 `GroupVerifier`를 넘긴다.
- Test: `tests/PellyCoder.Core.Tests/Specs/SpecRunnerGroupTests.cs`, `SpecCheckerTests.cs`(GroupUnsupported 테스트를 「그룹이 있어도 오류 없음」으로 바꿈)

**Interfaces:**
- Consumes: Task 3 `GroupRun`, `RunResult.TargetDisplay`, Task 5 `GroupVerifier`·`GroupErrorAttribution`, Task 6 그룹 API, 기존 `FeedbackBuilder.ForBuild(newErrors, targetRelativePath, targetText)`.
- Produces: `SpecRunner` 생성자에 선택 매개변수 `IGroupVerifier? groups = null`을 더한다(그룹이 있는데 null이면 종료 4 「그룹 빌드 검증기를 사용할 수 없습니다」).

그룹 실행 순서(첫 그룹 작업을 만나면 연속한 같은 그룹 작업을 한꺼번에 처리):
1. **영향 범위.** 그룹 작업 파일들의 가장 가까운 csproj를 구하고 `AffectedProjects`를 계산한다. csproj가 없는 파일이 있으면 검증 불가(4)다.
2. **기준 빌드.** 검증 불가면 그룹 작업 전부에 `failed … GroupUnverifiable`을 남기고 종료 4로 멈춘다. 이때 파일은 하나도 바뀌지 않았다. 사유는 메시지로 보인다.
3. **그룹 시작.** `tx.BeginGroup(groupId)`.
4. **작업 실행.** 각 작업을 `TaskRunOptions(Yes: true, DryRun: false, MaxRetries, Verbose, Group: new GroupRun(null))`로 실행한다.
   - 실패하면 그룹을 되돌리고, 그룹 작업 전부에 그 FSM 실패 종류를 남긴 뒤 그 종료 코드로 멈춘다(--continue 규칙 적용).
   - 작업마다 `TargetDisplay`를 기억한다.
5. **검증 빌드.** 새 오류를 귀속한다.
   - 미귀속이 있으면 그룹을 되돌리고 `GroupMissingTarget`과 종료 1이다. 메시지는 「명세에 빠진 영향 대상: …」이다.
   - 귀속된 작업만 `FeedbackBuilder.ForBuild` 피드백(그 파일 기준 상대 경로·현재 텍스트)과 `MemberOverride = 기억한 TargetDisplay의 이름`으로 다시 실행하고, 5로 돌아간다.
   - 재실행 횟수는 그룹 전체에 `MaxRetries ?? retry.max`다. 소진하면 그룹을 되돌리고 `GroupBuildFailed`, 종료 1이다.
6. **승인.** 통과하면 `--yes`도 `--review-at-end`도 아닌 경우 `GroupChanges()`를 `ShowDiff`로 보이고 「이 그룹을 적용할까요?」를 묻는다. 거부하면 그룹을 되돌리고 `GroupRejected`, 종료 2다.
7. **체크 표시.** 그룹 작업 전부를 각자의 마지막 run-id로 `[x]` 표시하고, `tx.EndGroup()`.
8. **되돌리지 못한 파일.** 그룹 되돌리기에서 되돌리지 못한 파일(바깥 변경 등)이 있으면 종료 4다.
   - 그 파일의 그룹 작업은 `[x]`(그 작업의 run-id)로 둔다. 파일에 적용된 상태와 표시를 맞추는 것이다(설계 §2, V2-1a와 같은 규칙).
   - 나머지 그룹 작업에는 실패 주석을 단다.
   - 경고에 되돌리지 못한 파일, 해당 작업 ID, 「그룹 일부가 적용된 채 남아 빌드가 깨져 있을 수 있습니다」를 적는다.
   - 테스트는 `Group_rollback_with_outside_change_keeps_marks_for_unrestored_files_exit_4`다.

- [ ] **Step 1: 실패하는 테스트** — 가짜 `ITaskExecutor`(V2-1a `FakeExecutor` 방식, `TargetDisplay`를 돌려주도록 확장)와 가짜 `IGroupVerifier`(빌드 결과를 차례로 돌려줌)를 쓴다.
  - `Group_passes_marks_all_tasks_done_together`
  - `Group_rerun_targets_the_renamed_member`: 1차 검증에서 T2에 귀속된 오류가 나면, T2 재실행의 `MemberOverride`가 T2의 `TargetDisplay` 이름이고 `Group.InitialFeedback`에 「BUILD FAILED」가 있다.
  - `Group_error_outside_group_targets_rolls_back_and_names_missing_target`: 파일들이 그룹 시작 전 바이트이고, 종료 1이며, 경고에 `src/App/Controller.cs#Detail`이 있고, 그룹 작업에 `failed … GroupMissingTarget`이 있다.
  - `Group_retry_budget_exhausted_rolls_back_exit_1`
  - `Unverifiable_group_exits_4_before_any_edit`: 실행기 호출 0회, 파일 변화 없음.
  - `Group_task_failure_rolls_back_whole_group`
  - `Group_rejection_rolls_back_exit_2`
  - `Review_at_end_skips_group_question`
  - `Continue_after_group_failure_stops_when_later_task_targets_group_file`
  - `Resume_after_failed_group_restarts_at_group_start`
  - `Non_group_tasks_before_and_after_group_still_run`
  - `SpecCheckerTests.Groups_are_allowed`
- [ ] **Step 2: 실패 확인.**
- [ ] **Step 3: 구현.**
- [ ] **Step 4: 통과 확인** — Core 전체, Cli 전체.
- [ ] **Step 5: 커밋** — `feat(specs): run task groups — group build, attribution, re-run, rollback, review`

### Task 8: 실제 FSM으로 그룹 끝까지, 문서

**Files:**
- Test: `tests/PellyCoder.Core.Tests/Specs/GroupEndToEndTests.cs`
- Modify: `README.md`, `deploy/usage.md`(그룹 절), `docs/PRD.md`(§12.3 상태 줄, 변경 이력 v1.15 「V2-1b 구현: 작업 그룹(시그니처·이름 변경, 영향 범위 빌드, 오류 귀속·재실행, 그룹 되돌리기). 영향 탐색·벤치마크는 V2-1c」)

**Interfaces:** Consumes everything above. 실제 `FsmOrchestratorFactory.Create`, `GroupVerifier`(실제 클래스, ScriptedProcessRunner 핸들러), `ScriptedLlmBackend`.

픽스처: 임시 작업 공간(빈 `.sln`)에 다음을 둔다.
- `Lib/Lib.csproj`(SDK): `IOrderService.cs`(`Task CancelAsync(int id);`), `OrderService.cs`(구현)
- `App/App.csproj`(SDK, Lib 참조): `OrdersController.cs`(`await _svc.CancelAsync(id);`)

- [ ] **Step 1: 실패하는 테스트**
  - `Signature_change_group_applies_three_tasks_and_archives`:
    - T1은 인터페이스 반환 형식, T2는 구현, T3은 컨트롤러이고, 모두 `[G1]`이다.
    - 빌드 핸들러는 「기준 성공, 검증 성공」이다.
    - 결과는 종료 0이고, 세 파일 모두 바뀌었고, 명세가 보관된다.
    - 빌드는 Lib·App 두 프로젝트 × 2회(기준·검증)다.
  - `Missing_caller_rolls_back_group`:
    - T3이 없는 명세다.
    - 검증 빌드가 App 컨트롤러 줄에 CS0029를 낸다.
    - 결과는 종료 1이고, Lib 파일이 원본이며, 경고에 `App/OrdersController.cs#Cancel`(또는 실제 멤버 이름)이 있다.
- [ ] **Step 2: 실패 확인.**
- [ ] **Step 3: 구현** — 테스트 도우미만. 제품 코드가 바뀌어야 하면 앞 작업의 결함이니 그 작업으로 돌아가 테스트부터 고친다.
- [ ] **Step 4: 통과 확인** — `dotnet build -warnaserror`, Core·Cli 전체.
- [ ] **Step 5: 문서 갱신 후 커밋** — `docs: V2-1b task groups`
