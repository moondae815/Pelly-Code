# V2-1a 명세 파일과 순차 실행 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `changes/<id>.md` 명세를 읽어 작업을 위에서부터 하나씩 v1 FSM으로 실행하는 `pelly apply`와 `pelly spec new|check`를 만든다. 작업 그룹, 심볼 영향 탐색, E2E 벤치마크, `pelly report`의 명세 완료율 집계는 제외한다(V2-1b·V2-1c).

**Architecture:**
- `PellyCoder.Core/Specs/`에 순수 구성 요소를 둔다.
  - 파서
  - 명세 파일 갱신기
  - 검사기
  - apply 트랜잭션
  - 실행기
- 실행기는 작업 하나를 `ITaskExecutor`에 맡긴다. 실제 구현은 v1 FSM을 부르고, 테스트에서는 가짜로 바꾼다.
- FSM에는 두 가지만 더한다. 하나는 「apply가 이미 잡은 작업 공간·잠금을 받는 것」이고, 다른 하나는 「실행 결과(run-id, 처음·마지막 파일 스냅숏)를 돌려주는 것」이다. v1 단일 실행 동작은 그대로다.

**Tech Stack:** .NET 10, Spectre.Console.Cli, System.IO.Abstractions, xunit v3(기존 테스트 프로젝트들).

**Spec:** `docs/superpowers/specs/2026-10-24-v2-1-apply-design.md` (이 계획은 §4, §5의 1~4, §6의 그룹 외 전부, §7을 구현한다. §5의 5, §6 작업 그룹, §8 E2E 벤치마크는 V2-1b·V2-1c 계획.)

## Global Constraints

- **v1 동작 보존:** v1 `pelly cs`·`pelly sql`의 동작, 프롬프트, 종료 코드, 기존 테스트는 바뀌지 않는다. 전체 테스트가 통과하고, `dotnet build -warnaserror`가 경고 0이어야 한다.
- **종료 코드:** v1 그대로 쓴다. 0 성공, 1 검증 실패, 2 사용자 취소·거부, 3 대상 특정 실패, 4 환경·입력 오류, 5 안전장치 거부.
  - 명세 오류는 4다.
  - 명세 파일이 실행 중 바깥에서 바뀌면 4다.
  - `--continue`로 끝까지 갔는데 실패가 있으면 1이다.
- **작업 줄 문법:** `- [ ] <ID> <cs|sql> <파일>[#심볼] <요청> [(AC 참조)] [[G<n>]]`.
  - 필드 사이 공백은 하나 이상이다.
  - 완료 표시는 `[x]`(소문자)뿐이다.
  - 실행 기록 주석은 `<!-- run: <run-id> -->`, 실패 주석은 `<!-- failed: <run-id> <실패 종류> -->`이다.
- **작업 수 상한:** 20.
- **V2-1a는 작업 그룹(`[G<n>]`)을 실행하지 않는다.** `spec check`가 오류 `GroupUnsupported`(「작업 그룹은 아직 지원하지 않습니다(V2-1b)」)로 막는다. 파서는 그룹을 읽는다.
- **apply 작업 공간 루트:** 명세 파일에 `WorkspaceLocator.Resolve`를 적용한 결과다. 모든 작업 경로는 이 루트 기준 상대 경로다.
- **잠금:** apply 동안 잠금을 한 번만 잡는다.
- **기록 위치:**
  - 백업: `.pelly/backup/<apply-id>/<상대 경로>`
  - apply 요약: `.pelly/runs/<apply-id>/apply.json`
  - apply-id: `apply-<yyyyMMdd-HHmmss>-<hex4>`
  - `summary.json`을 쓰지 않아 `RunHistory`와 보존 정리가 건드리지 않는다.
- **보관:** `changes/archive/<yyyy-MM-dd>-<id>.md`. 같은 이름이 있으면 `-2`, `-3`을 붙인다.
- **문구:** 사용자 문구는 한국어, 코드 주석은 영어(저장소 관례).
- **커밋 꼬리:** `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.

## Review Focus

1. **실행 중에 사람이 편집기로 명세 파일을 저장하는 경우:** 체크 표시를 쓰지 않고 멈춰야 한다(종료 4). 덮어쓰면 사람이 고친 내용이 사라진다. → Task 2 `Mark_refuses_when_spec_changed_on_disk`, Task 6 `Apply_stops_when_spec_file_changes_midway`.
2. **CRLF·BOM·CP949로 저장된 명세:** 갱신 뒤에도 인코딩·줄 끝·BOM이 그대로여야 한다. → Task 2 `Mark_preserves_crlf_bom_and_cp949`.
3. **Windows 경로(`\`)나 `#`·괄호가 든 요청문:** AC 참조는 줄 끝의 `(AC…)`만이다. → Task 1 `Parses_backslash_paths_and_parentheses_inside_request`.
4. **같은 파일을 대상으로 하는 작업 둘:** 둘째 작업은 첫째의 결과 위에서 실행된다. `--review-at-end`에서 거부하면 apply 시작 전 원본으로 돌아가야 한다(첫 백업이 이긴다). → Task 6 `Review_at_end_reject_restores_originals_for_shared_file`.
5. **비대화형인데 `--yes`가 없는 경우:** 파일을 하나도 바꾸기 전에 거부해야 한다. v1은 작업마다 바꾼 뒤 되돌린다. → Task 6 `Non_interactive_without_yes_refuses_before_any_task`.

---

### Task 1: 명세 파서

**Files:**
- Create: `src/PellyCoder.Core/Specs/ChangeSpec.cs`, `src/PellyCoder.Core/Specs/ChangeSpecParser.cs`
- Test: `tests/PellyCoder.Core.Tests/Specs/ChangeSpecParserTests.cs`

**Interfaces:**
- Produces:
  - `enum SpecStatus { Draft, InProgress, Done }`
  - `enum TaskKind { CSharp, Sql }`
  - `enum SpecIssueSeverity { Error, Warning }`
  - `record SpecIssue(int Line, SpecIssueSeverity Severity, string Code, string Message)` — Line은 1부터
  - `record AcceptanceCriterion(string Id, string Text, string? Test)`
  - `record SpecTask(string Id, TaskKind Kind, string Path, string? Symbol, string Request, IReadOnlyList<string> AcRefs, string? Group, bool Done, string? RunId, string? Failure, int Line)`
  - `record ChangeSpec(string? Id, SpecStatus Status, string? Title, IReadOnlyList<AcceptanceCriterion> Criteria, IReadOnlyList<SpecTask> Tasks, IReadOnlyList<SpecIssue> Issues)`
  - `static ChangeSpec ChangeSpecParser.Parse(string textLf)`

파싱 규칙:
- **머리말:** 첫 줄 `---`부터 다음 `---`까지다. `id:`와 `status:`(draft | in-progress | done)를 읽는다. 없거나 모르는 값이면 오류 `FrontMatter`다.
- **제목:** 첫 `# ` 줄이다.
- **AC:** `## 수용 기준` 아래의 `- AC<n>: <문장>` 줄이다. 바로 다음 줄이 들여쓴 `test: <이름>`이면 그 AC에 붙인다.
- **작업:** `## 작업` 아래에서 읽는다. 작업 줄 정규식(필드 사이 `\s+`)은 다음과 같다. 요청은 게으르게 잡고, 뒤의 `(AC…)`·`[G…]`·주석은 선택이다.
  ```
  ^- \[( |x)\]\s+(T\d+)\s+(cs|sql)\s+(\S+?)(?:#(\S+))?\s+(.+?)(?:\s+\((AC\d+(?:\s*,\s*AC\d+)*)\))?(?:\s+\[(G\d+)\][.,;:]?)?(?:\s*<!--\s*(run|failed):\s*([^>]*?)\s*-->)?\s*$
  ```
- **작업 줄 모양인데 문법에 안 맞는 줄은 오류 `InvalidTaskLine`이다.** 「작업 줄 모양」은 `^\s*(?:[-*+]|\d+[.)])\s*\[.?\]` 또는 `\bT\d+\s+(?:cs|sql)\s`에 맞는 줄이다. 들여쓰기, `*`, 번호 목록, `[X]`가 여기에 걸린다.
- **그 밖의 검사:**
  - 중복 ID는 `DuplicateId`다.
  - 작업 20개 초과는 `TooManyTasks`다.
  - 같은 그룹이 연속하지 않으면 `GroupNotContiguous`다.
  - 그룹 안의 `sql` 작업은 `SqlInGroup`이다.
  - 없는 AC를 참조하면 `UnknownAc`다.
  - 작업이 0개면 `NoTasks`다.
  - 모두 Error다.
- `failed` 주석의 값은 `<run-id> <실패 종류>`다. RunId와 Failure로 나눠 담는다.

- [ ] **Step 1: 실패하는 테스트 작성** — `ChangeSpecParserTests`:
  - `Parses_prd_example`: PRD §12.3 예시 전문(두 칸 공백 포함)에서 다음을 확인한다.
    - `Id == "order-cancel"`, `Status == Draft`, `Criteria` 2개, AC1의 `Test == "OrderServiceTests.Cancel_WhenShipped_Throws"`
    - T1은 `(CSharp, "src/Orders/OrderService.cs", "Cancel", AcRefs ["AC1"])`
    - T2는 `(Sql, "db/sp/usp_CancelOrder.sql", null, ["AC2"])`
    - `Issues` 비어 있음
  - `Parses_backslash_paths_and_parentheses_inside_request`: 줄 `- [ ] T1 cs Src\\A.cs#B.M 값 (괄호) 처리 #주의 (AC1)`이면 다음과 같다.
    - Path는 `Src\\A.cs`, Symbol은 `B.M`
    - Request는 `값 (괄호) 처리 #주의`, AcRefs는 `["AC1"]`
  - `Reads_done_run_comment_and_failure_comment`:
    - `- [x] T1 cs A.cs#M 요청 <!-- run: 20261024-101010-ab12 -->`는 `Done`, `RunId == "20261024-101010-ab12"`다.
    - `- [ ] T2 cs A.cs#N 요청 <!-- failed: 20261024-101011-cd34 VerifyFailed -->`는 `Failure == "VerifyFailed"`다.
  - `Group_with_trailing_punctuation_is_read`: `... 요청 [G1].`이면 `Group == "G1"`이다.
  - `[Theory] Task_shaped_but_off_grammar_lines_are_errors`: 행은 `"  - [ ] T1 cs A.cs#M x"`, `"* [ ] T1 cs A.cs#M x"`, `"1. [ ] T1 cs A.cs#M x"`, `"- [X] T1 cs A.cs#M x"`, `"- [ ] T1 vb A.cs x"`다. 모두 `Issues`에 `InvalidTaskLine`(Error)이 있고 `Tasks`는 비어 있다.
  - `Duplicate_ids_too_many_tasks_group_order_sql_in_group_unknown_ac_are_errors`: 각각 하나씩 만든 명세 다섯 개다. 해당 Code가 있어야 한다.
  - `Missing_front_matter_is_an_error`
- [ ] **Step 2: 실패 확인** — `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*ChangeSpecParserTests"` → 컴파일 실패.
- [ ] **Step 3: 구현** — 위 규칙대로 구현한다. 줄 번호는 원문 기준이다.
- [ ] **Step 4: 통과 확인** — 같은 명령, 전부 통과.
- [ ] **Step 5: 커밋** — `feat(specs): change spec parser`

### Task 2: 명세 파일 갱신기

**Files:**
- Create: `src/PellyCoder.Core/Specs/SpecDocument.cs`
- Test: `tests/PellyCoder.Core.Tests/Specs/SpecDocumentTests.cs`

**Interfaces:**
- Consumes: Task 1의 `ChangeSpecParser`, 기존 `FileGuard`(`Read`, `WriteAsync`)와 `FileSnapshot`.
- Produces:
  - `sealed class SpecDocument`
    - `static SpecDocument Load(FileGuard guard, string path)`
    - `ChangeSpec Spec { get; }`, `FileSnapshot Snapshot { get; }`
  - 갱신 메서드. 모두 원문 줄 단위로 고친 뒤 `FileGuard.WriteAsync(Snapshot, newTextLf)`로 쓴다. 성공하면 `Snapshot`과 `Spec`을 새로 고친다.
    - `Task<FileWriteResult> MarkDoneAsync(string taskId, string runId, CancellationToken ct)`
    - `Task<FileWriteResult> MarkFailedAsync(string taskId, string runId, string failureKind, CancellationToken ct)`
    - `Task<FileWriteResult> ClearMarksAsync(IEnumerable<string> taskIds, CancellationToken ct)` — `[ ]`로 되돌리고 주석을 지운다
    - `Task<FileWriteResult> SetStatusAsync(SpecStatus status, CancellationToken ct)`

규칙:
- 줄의 기존 주석(run·failed)은 새 주석으로 바꾼다. 다른 글자는 그대로 둔다.
- `WriteAsync`가 `ExternallyChanged`면 아무것도 쓰지 않고 그 결과를 돌려준다(바깥 변경 감지는 FileGuard의 SHA 비교).

- [ ] **Step 1: 실패하는 테스트**
  - `Mark_done_sets_x_and_run_comment`: 결과 줄이 `- [x] T1 cs A.cs#M 요청 <!-- run: R1 -->`이다.
  - `Mark_failed_then_done_replaces_comment`
  - `Clear_marks_restores_unchecked_without_comments`
  - `Set_status_rewrites_front_matter_only`: 다른 줄은 바이트 단위로 같다.
  - `Mark_refuses_when_spec_changed_on_disk`: Load 뒤 디스크 파일을 바꾸면 `Status == ExternallyChanged`이고 디스크 내용은 바뀐 그대로다.
  - `Mark_preserves_crlf_bom_and_cp949`: 세 파일을 준비한다. ① CRLF + UTF-8 BOM ② CP949 한글(코드 페이지 등록은 기존 TextDecoding 관례). ③ LF 무 BOM. 갱신 뒤 바이트를 다시 읽으면 BOM 유무·줄 끝·인코딩이 같다.
- [ ] **Step 2: 실패 확인** → 컴파일 실패.
- [ ] **Step 3: 구현.**
- [ ] **Step 4: 통과 확인.**
- [ ] **Step 5: 커밋** — `feat(specs): spec document updates through FileGuard`

### Task 3: 명세 검사기 (`spec check` 1~4)

**Files:**
- Create: `src/PellyCoder.Core/Specs/SpecChecker.cs`, `src/PellyCoder.Core/Specs/RequestIdentifiers.cs`
- Test: `tests/PellyCoder.Core.Tests/Specs/SpecCheckerTests.cs`, `tests/PellyCoder.Core.Tests/Specs/RequestIdentifiersTests.cs`

**Interfaces:**
- Consumes: Task 1. 기존 `WorkspaceLocator`, `MemberLocator.FindByName(sourceLf, version, className, memberName)`, `LanguageVersionDetector.Detect(fs, csprojPath)`, `ProjectFileLocator`, `SqlBatchSplitter.Split(textLf, compatLevel)`, `SqlObjectName.Parse`, `TextFileCodec.Read`, `BoundedWalker`.
- Produces:
  - `record SpecCheckResult(ChangeSpec Spec, Workspace Workspace, IReadOnlyList<SpecIssue> Issues)`, `bool HasErrors`
  - `static SpecCheckResult SpecChecker.Check(IFileSystem fs, string specPath, int sqlCompatLevel)`
  - `static IReadOnlyList<string> RequestIdentifiers.Extract(string request)`

검사 순서와 Code:
- 파서 오류를 그대로 담는다.
- 그룹 표시가 하나라도 있으면 `GroupUnsupported`(Error)다.
- **대상 경로:**
  - 작업 공간 루트 밖이면 `OutsideWorkspace`다.
  - 파일이 없으면 `FileNotFound`다.
  - `cs`인데 `.cs`가 아니거나 `sql`인데 `.sql`이 아니면 `KindMismatch`다.
- **cs 심볼:**
  - `#` 없으면 `SymbolRequired`(Error)다. apply는 요청문으로 대상을 찾지 않는다(Claude 결정: 명세는 대상을 명시한다).
  - 심볼은 `Type.Member` 또는 `Member`다. `.`가 있으면 마지막 마디가 멤버이고, 그 앞 마디의 마지막이 클래스다.
  - `MemberLocator.FindByName` 결과가 0이면 `SymbolNotFound`, 2 이상이면 `SymbolAmbiguous`다.
- **sql 객체:**
  - `#`이 있으면 `SqlObjectName.Parse`로 이름을 읽어, 파일의 객체 배치에서 그 이름을 찾는다. 없으면 `SqlObjectNotFound`다.
  - `#`이 없으면 객체 배치가 정확히 하나여야 한다. 아니면 `SqlObjectAmbiguous`다.
- **AC `test:`:** 이름(`Class.Method` 또는 `Method`)이 작업 공간 `.cs` 파일 어디에도 메서드 선언으로 없으면 `TestNotFound`(Warning)다. 구문 검사이고, BoundedWalker 상한을 따른다.
- **지시 문구 안 이름(Warning `UnknownIdentifier`):**
  - `RequestIdentifiers.Extract`가 뽑은 각 낱말을 대조 집합에 대본다. 대조 집합은 다음과 같다.
    - cs 작업: 대상 파일의 가장 가까운 csproj 폴더 아래 모든 `.cs`의 식별자 토큰
    - sql 작업: 작업 공간의 모든 `.sql`의 식별자
  - 어디에도 없으면 경고한다. 메시지에 낱말을 적는다.

`RequestIdentifiers.Extract` 규칙:
- 백틱 안 문자열은 그대로 쓴다.
- 그 밖의 다음 낱말을 쓴다. 모두 ASCII다.
  - `PascalCase` 낱말: 대문자 시작, 소문자 포함, 두 글자 이상
  - `_` 시작 낱말
  - `schema.Name` 꼴
- 한국어 낱말과 순수 숫자는 제외한다.

- [ ] **Step 1: 실패하는 테스트**
  - `RequestIdentifiersTests`
    - `Extract` 입력이 ``"`OrderStatus.Shipped`이면 InvalidOperationException을 던지고 _context 대신 dbo.usp_X 호출, CancelTime 컬럼 추가"``이면, 결과에 `OrderStatus.Shipped`, `InvalidOperationException`, `_context`, `dbo.usp_X`, `CancelTime`이 있다.
    - 한글 낱말(`이면`, `컬럼`)과 숫자는 결과에 없다. `Ok`는 규칙상 PascalCase라 결과에 있다.
  - `SpecCheckerTests`(임시 폴더에 `.sln` 하나, SDK csproj, `.cs`, `.sql`):
    - `Clean_spec_has_no_issues`
    - `Missing_file_symbol_ambiguous_symbol_and_sql_object_are_errors`: 각 Code
    - `Cs_task_without_symbol_is_an_error`
    - `Group_is_unsupported_in_v2_1a`
    - `Path_outside_workspace_is_an_error`: `../x.cs`
    - `Unknown_identifier_in_request_is_a_warning`: sql 작업 요청에 `CancelTime`(파일에 없음)이 있으면 Warning `UnknownIdentifier`이고, `HasErrors == false`다.
    - `Known_identifiers_do_not_warn`: 대상 프로젝트 소스에 있는 `InvalidOperationException`·`OrderStatus`는 경고하지 않는다.
- [ ] **Step 2: 실패 확인.**
- [ ] **Step 3: 구현.**
- [ ] **Step 4: 통과 확인.**
- [ ] **Step 5: 커밋** — `feat(specs): spec check — grammar, targets, AC tests, request identifiers`

### Task 4: FSM이 apply의 작업 공간·잠금을 받고 결과를 돌려준다

**Files:**
- Modify: `src/PellyCoder.Core/Fsm/RunRequest.cs` (RunRequest에 `ApplyHost? Host = null` 추가)
- Modify: `src/PellyCoder.Core/Fsm/Phases/PreflightPhase.cs`
- Modify: `src/PellyCoder.Core/Fsm/FsmOrchestrator.cs` (`RunDetailedAsync` 추가, `RunAsync`는 그것의 `ExitCode`)
- Modify: `src/PellyCoder.Cli/Runners.cs`·`Composition.cs` (`IRunner.RunDetailedAsync`)
- Test: `tests/PellyCoder.Core.Tests/Fsm/ApplyHostFlowTests.cs`

**Interfaces:**
- Produces:
  - `record ApplyHost(Workspace Workspace)` — 이것이 있으면 Preflight는 잠금을 잡지 않고(호출자가 이미 잡음) 이 작업 공간을 쓴다.
  - `record RunResult(int ExitCode, string RunId, string? FilePath, FileSnapshot? Original, FileSnapshot? Final, string? FailureKind)`
    - `Final`은 성공 시 마지막으로 쓴 스냅숏이다.
    - 실패·거부·롤백 뒤에는 롤백 결과 스냅숏이거나, 쓴 적이 없으면 `Original`이다.
  - `Task<RunResult> FsmOrchestrator.RunDetailedAsync(RunRequest request, CancellationToken ct)`
  - `Task<RunResult> IRunner.RunDetailedAsync(RunRequest request, PellyOptions options, CancellationToken ct)` — 기존 `RunAsync`는 이것을 부른다.

Preflight 규칙(Host가 있을 때):
- 대상 파일이 `Host.Workspace.Root` 밖이면 `Env`(「대상 파일이 apply 작업 공간 밖입니다」)다.
- 잠금은 `ctx.Lock = null`로 두어 FSM이 해제하지 않게 한다.
- 나머지(규칙 로드 등)는 같고, 규칙도 `Host.Workspace` 기준이다.

- [ ] **Step 1: 실패하는 테스트** — `ApplyHostFlowTests`. 기존 `FsmOrchestratorTests`의 Calc 픽스처와 Scripted 가짜를 쓴다.
  - `Run_with_host_does_not_take_the_lock`: 테스트가 먼저 `WorkspaceLock.TryAcquire`로 잠금을 잡은 상태에서 Host와 함께 성공 패치를 실행한다. 종료 0이고, 「다른 pelly 실행이 진행 중」 메시지가 없다.
  - `Run_without_host_still_refuses_when_locked`: Host 없이 잠금이 잡혀 있으면 종료 4다(v1 동작 보존).
  - `Run_with_host_rejects_file_outside_root`
  - `Detailed_result_carries_run_id_and_final_snapshot`: 성공하면 `Final.Text`에 패치가 들어 있다. 검증 실패(종료 1)면 `Final.Sha256 == Original.Sha256`이다.
- [ ] **Step 2: 실패 확인.**
- [ ] **Step 3: 구현.** `RunContext`에서 결과를 만든다. 롤백 뒤 스냅숏은 `RollbackAsync` 결과(`FileWriteResult.Snapshot`)에서 얻는다. 롤백이 바깥 변경으로 실패하면 `Final`은 null이다.
- [ ] **Step 4: 통과 확인** — `dotnet test tests/PellyCoder.Core.Tests` 전체(기존 FSM 테스트 포함) 통과.
- [ ] **Step 5: 커밋** — `feat(fsm): run inside an apply host and return a detailed result`

### Task 5: 작업 실행기와 apply 트랜잭션

**Files:**
- Create: `src/PellyCoder.Core/Specs/ITaskExecutor.cs`, `src/PellyCoder.Core/Specs/FsmTaskExecutor.cs`, `src/PellyCoder.Core/Specs/ApplyTransaction.cs`
- Test: `tests/PellyCoder.Core.Tests/Specs/FsmTaskExecutorTests.cs`, `tests/PellyCoder.Core.Tests/Specs/ApplyTransactionTests.cs`

**Interfaces:**
- Consumes: Task 1(`SpecTask`), Task 4(`RunResult`, `ApplyHost`, `IRunner`와 같은 모양의 실행 델리게이트).
- Produces:
  - `record TaskRunOptions(bool Yes, bool DryRun, int? MaxRetries, bool Verbose)`
  - `interface ITaskExecutor { Task<RunResult> ExecuteAsync(SpecTask task, Workspace ws, TaskRunOptions o, CancellationToken ct); }`
  - `sealed class FsmTaskExecutor(Func<RunRequest, CancellationToken, Task<RunResult>> run) : ITaskExecutor`
    - `SpecTask`를 `RunRequest`로 바꾼다. 파일은 `Path.Combine(ws.Root, task.Path)`이고, 구분자는 정규화한다.
    - cs는 `ClassName`/`MethodName`을 심볼 규칙(Task 3)대로 넣는다.
    - sql은 `Kind = EditKind.Sql`, `ObjectName = task.Symbol`이다.
    - `Host = new ApplyHost(ws)`다.
  - `sealed class ApplyTransaction(FileGuard guard, Workspace ws, string applyId)`
    - `void Track(FileSnapshot original)` — 파일마다 처음 한 번만 `guard.Backup(ws, applyId, original)`을 하고 원본을 기억한다. 첫 백업이 이긴다.
    - `void Update(FileSnapshot lastWritten)`
    - `Task<IReadOnlyList<string>> RollbackAsync(CancellationToken ct)` — 되돌리지 못한 파일 경로를 돌려준다(바깥 변경).
    - `IReadOnlyList<(string Path, string Before, string After)> Changes()` — 원본과 다른 파일만.

- [ ] **Step 1: 실패하는 테스트**
  - `FsmTaskExecutorTests`
    - `Cs_task_maps_type_and_member`: `#OrderService.Cancel`이면 `ClassName == "OrderService"`, `MethodName == "Cancel"`이다.
    - `Sql_task_maps_object_name_and_kind`
    - `Backslash_path_is_normalized_and_host_is_set`
  - `ApplyTransactionTests`(실제 파일 시스템, 임시 폴더)
    - `Two_updates_to_one_file_rollback_to_first_original`
    - `Rollback_skips_externally_changed_file_and_reports_it`
    - `Changes_lists_only_modified_files`
- [ ] **Step 2: 실패 확인.**
- [ ] **Step 3: 구현.**
- [ ] **Step 4: 통과 확인.**
- [ ] **Step 5: 커밋** — `feat(specs): task executor over the FSM and apply transaction`

### Task 6: apply 실행기

**Files:**
- Create: `src/PellyCoder.Core/Specs/SpecRunner.cs`, `src/PellyCoder.Core/Specs/SpecArchiver.cs`, `src/PellyCoder.Core/Specs/ApplySummary.cs`
- Test: `tests/PellyCoder.Core.Tests/Specs/SpecRunnerTests.cs`, `tests/PellyCoder.Core.Tests/Specs/SpecArchiverTests.cs`

**Interfaces:**
- Consumes: Task 1~5, 기존 `IUserInteraction`, `WorkspaceLock`, `FileGuard`, `TimeProvider`.
- Produces:
  - `record ApplyOptions(bool Continue, bool Resume, bool ReviewAtEnd, bool Yes, bool DryRun, int? MaxRetries, bool Verbose)`
  - `sealed class SpecRunner(IFileSystem fs, FileGuard guard, ITaskExecutor executor, IUserInteraction ui, TimeProvider time, int sqlCompatLevel)`
    - `Task<int> RunAsync(string specPath, ApplyOptions o, CancellationToken ct)`
  - `static string SpecArchiver.Archive(IFileSystem fs, string specPath, DateOnly date, string id)` — 옮긴 경로를 돌려준다.
  - `record ApplySummary(string ApplyId, string SpecId, IReadOnlyList<ApplyTaskResult> Tasks, int ExitCode, IReadOnlyList<SpecIssue> Warnings, bool Archived)`, `record ApplyTaskResult(string TaskId, string? RunId, int ExitCode, string? FailureKind, double Seconds)`

실행 순서(설계 §6, 그룹 제외):
1. **검사.** `SpecChecker.Check`에 오류가 있으면 이슈를 보여 주고 4를 돌려준다.
2. **비대화형 확인.** `!ui.IsInteractive && !o.Yes && !o.DryRun`이면 4다(「비대화형 실행에는 --yes가 필요합니다」).
3. **경고 확인.** 경고가 있으면 보여 준다. 대화형이고 `--yes`가 아니면 `ConfirmAsync("경고가 있습니다. 계속할까요?", false)`로 묻는다. 거부하면 2다.
4. **잠금.** `WorkspaceLock.TryAcquire`가 실패하면 4다.
5. **시작 지점.** `--resume`이면 첫 `[ ]` 작업부터 시작한다. 아니면 모든 작업이 `[ ]`여야 하고, 이미 `[x]`가 있으면 4다(「이미 진행된 명세입니다. --resume을 쓰세요」).
6. **드라이런.** `--dry-run`이면 첫 대상 작업 하나만 `DryRun = true`로 실행하고, 명세는 바꾸지 않은 채 그 종료 코드를 돌려준다.
7. **상태와 ID.** `status: in-progress`로 바꾸고, apply-id를 만든다.
8. **작업마다.**
   - `executor.ExecuteAsync`로 실행한다. `Yes`는 `o.Yes || o.ReviewAtEnd`다.
   - 결과의 `Original`과 `Final`로 트랜잭션을 `Track`·`Update`한다.
   - 종료 0이면 `MarkDoneAsync`다.
   - 그 밖이면 다음과 같다.
     - `--continue`가 아니면 `MarkFailedAsync` 뒤 멈춘다.
     - `--continue`이고 종료 4가 아니면 `MarkFailedAsync` 뒤 다음 작업으로 간다.
     - 종료 4면 멈춘다.
   - 명세 갱신이 `ExternallyChanged`면 즉시 4로 멈춘다. 이미 적용된 작업은 그대로 둔다. 메시지는 「명세 파일이 실행 중에 바뀌었습니다」다.
   - 취소(`OperationCanceledException` 또는 종료 2이면서 취소 토큰이 설정됨)면 멈추고 2다.
9. **끝에 승인(`--review-at-end`).** 모든 작업이 끝난 뒤 `Changes()`를 `ShowDiff`로 보여 주고 `ConfirmAsync("모두 적용할까요?", false)`로 묻는다.
   - 거부하면 `RollbackAsync` 뒤 `ClearMarksAsync`(이번 apply에서 표시한 작업들)와 `SetStatusAsync(Draft)`를 하고 2를 돌려준다.
   - 되돌리지 못한 파일이 있으면 그 목록을 보여 주고 4다.
10. **마무리.** 모든 작업이 `[x]`면 `SetStatusAsync(Done)` 뒤 `SpecArchiver.Archive`를 한다.
11. **요약.** `apply.json`을 쓴다(best effort, 실패해도 결과를 바꾸지 않음). 종료 코드는 다음과 같다.
    - 모두 성공이면 0이다.
    - 멈췄으면 그 작업의 종료 코드다.
    - `--continue`로 끝까지 갔는데 실패가 있으면 1이다.
12. **잠금 해제.** `finally`에서 잠금을 해제한다.

- [ ] **Step 1: 실패하는 테스트** — `SpecRunnerTests`. 임시 작업 공간, 실제 `FileGuard`, 가짜 `ITaskExecutor`를 쓴다. 가짜 실행기는 스크립트대로 파일에 텍스트를 쓰고 `RunResult`를 돌려준다.
  - `All_tasks_succeed_marks_done_archives_and_exits_0`: 명세가 `changes/archive/2026-10-24-x.md`로 옮겨지고 `status: done`이다.
  - `Failure_stops_and_marks_failed`: T2가 종료 1이면 T3은 실행되지 않고, T2 줄에 `failed` 주석이 있으며 종료 1이다.
  - `Continue_runs_remaining_and_exits_1`
  - `Continue_stops_on_env_error`: T2가 종료 4면 T3은 실행되지 않는다.
  - `Resume_starts_at_first_unchecked`: T1 `[x]`면 T2부터다.
  - `Already_progressed_spec_without_resume_is_refused`: 종료 4다.
  - `Spec_errors_exit_4_without_running_tasks`
  - `Non_interactive_without_yes_refuses_before_any_task`: 실행기 호출 0회, 파일 변화 없음.
  - `Warnings_are_confirmed_interactively_and_rejection_exits_2`
  - `Dry_run_runs_first_task_only_and_leaves_spec_unchanged`
  - `Review_at_end_approve_keeps_changes`
  - `Review_at_end_reject_restores_originals_for_shared_file`: T1·T2가 같은 파일을 바꾼다. 거부하면 그 파일이 apply 전 바이트이고, 체크 표시가 사라지며 `status: draft`이고, 종료 2다.
  - `Apply_stops_when_spec_file_changes_midway`: 가짜 실행기가 T1 실행 중에 명세 파일을 바꾸면 종료 4이고, 명세의 바깥 변경이 보존된다.
  - `Lock_held_by_another_run_exits_4`
  - `SpecArchiverTests.Archive_appends_suffix_when_name_exists`: `-2`, `-3`을 붙인다.
- [ ] **Step 2: 실패 확인.**
- [ ] **Step 3: 구현.**
- [ ] **Step 4: 통과 확인.**
- [ ] **Step 5: 커밋** — `feat(specs): pelly apply runner — sequential tasks, resume, continue, review at end, archive`

### Task 7: CLI 명령

**Files:**
- Create: `src/PellyCoder.Cli/Commands/ApplyCommand.cs`, `src/PellyCoder.Cli/Commands/SpecNewCommand.cs`, `src/PellyCoder.Cli/Commands/SpecCheckCommand.cs`
- Modify: `src/PellyCoder.Cli/CliApp.cs` — `apply` 명령, `spec` 가지(`new`, `check`)를 등록한다. `CommandSettings`에 `"apply"`, `"spec new"`, `"spec check"`를 더한다. `FindOptionWithoutValue`가 `spec` 가지도 `rules`처럼 다루게 한다.
- Modify: `src/PellyCoder.Cli/Composition.cs` — `SpecRunner`를 연결한다. `FsmTaskExecutor`의 실행 델리게이트는 `IRunner.RunDetailedAsync`다.
- Test: `tests/PellyCoder.Cli.Tests/ApplyCommandTests.cs`, `tests/PellyCoder.Cli.Tests/SpecCommandTests.cs`

**Interfaces:**
- Consumes: Task 3, Task 6, 기존 `OptionsResolver.Resolve(anchorPath, config, think, model, endpoint, strictBaseline, warnAsError)`.
- Produces:
  - `pelly apply <SPEC> [--continue] [--resume] [--review-at-end] [--yes] [--dry-run] [--verbose] [--max-retries N] [--config] [--think] [--model] [--endpoint] [--pause-on-exit]`. 옵션 파일 기준점은 명세 파일이다.
  - `pelly spec new <ID>`: `changes/<id>.md`를 작업 공간 루트(현재 폴더 기준 `WorkspaceLocator`) 아래에 만든다. 템플릿은 PRD 예시 모양이고 작업 줄은 빈 예시 1줄을 주석으로 둔다. ID는 `^[a-z0-9][a-z0-9-]{0,63}$`이고, 아니면 4다. 이미 있으면 4다.
  - `pelly spec check <SPEC>`: 이슈를 줄 번호와 함께 출력한다. 오류가 있으면 4, 아니면 0이다.

- [ ] **Step 1: 실패하는 테스트**
  - `SpecCommandTests`
    - `New_creates_template_that_parses_without_errors_except_no_tasks`: 만든 파일을 파서에 넣으면 이슈가 `NoTasks` 하나뿐이다.
    - `New_rejects_bad_id_and_existing_file`
    - `Check_exits_4_on_errors_and_0_on_warnings_only`
    - `Option_without_value_is_rejected_for_spec_branch`
  - `ApplyCommandTests`
    - `Apply_passes_options_to_runner`: 가짜 실행기 주입으로 `--continue --review-at-end --yes`가 그대로 전달된다.
    - `Missing_spec_file_exits_4`
    - `Exit_codes_are_within_0_to_5`(기존 `ExitCodeMappingTests` 관례)
- [ ] **Step 2: 실패 확인.**
- [ ] **Step 3: 구현.**
- [ ] **Step 4: 통과 확인.** 다음 둘을 확인한다.
  - `dotnet build -warnaserror && dotnet test -- --filter-not-trait "Env=Windows" --filter-not-trait "Env=SqlServer" --filter-not-trait "Env=Ollama"`가 실패 0이다.
  - 수동 확인: 임시 작업 공간에서 `pelly spec new demo` → `pelly spec check changes/demo.md`(종료 4, NoTasks)다.
- [ ] **Step 5: 커밋** — `feat(cli): pelly apply and pelly spec new|check`

### Task 8: 실제 FSM으로 끝까지, 문서

**Files:**
- Test: `tests/PellyCoder.Core.Tests/Specs/ApplyEndToEndTests.cs`
- Modify: `README.md`(명령 표), `deploy/usage.md`(apply·spec 절), `docs/PRD.md`(§12.3 상태 줄과 변경 이력 v1.14: 「V2-1a 구현: 명세 파일·spec new/check·순차 apply. 그룹·영향 탐색은 V2-1b·c」)

**Interfaces:** Consumes Task 4~6과 기존 `FsmOrchestratorFactory.Create`·Scripted 가짜.

- [ ] **Step 1: 실패하는 테스트** — `ApplyEndToEndTests`. 실제 `FsmOrchestratorFactory.Create`, `ScriptedLlmBackend`, `ScriptedUserInteraction`, 빌드는 `ScriptedProcessRunner` 성공 응답을 쓴다.
  - `Two_tasks_on_one_file_apply_in_order_and_archive`
    - Calc 픽스처에 `Divide`와 `Multiply` 두 메서드를 두고 T1·T2를 실행한다.
    - 둘째 작업의 프롬프트(백엔드가 받은 요청)에 첫째 작업의 결과가 들어 있다.
    - 명세가 보관되고, 각 줄에 `run:` 주석이 있다.
  - `Second_task_verify_failure_rolls_back_only_that_task_and_marks_failed`
- [ ] **Step 2: 실패 확인.**
- [ ] **Step 3: 구현** — 필요하면 테스트 도우미만 더한다. 제품 코드가 바뀐다면 그것은 앞 작업의 결함이다. 그 작업으로 돌아가 테스트부터 고친다.
- [ ] **Step 4: 통과 확인** — 전체 테스트와 `dotnet build -warnaserror`.
- [ ] **Step 5: 문서 갱신 후 커밋** — `docs: V2-1a apply and spec commands`
