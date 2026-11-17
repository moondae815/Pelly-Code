# PellyCodeMcpServer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** OpenCode가 부를 수 있는 stdio MCP 서버 `PellyCodeMcpServer.exe`를 만든다. 도구는 둘이다. `pelly_impact`는 코드에서 부르는 곳을 찾고, `pelly_verify`는 HEAD 대비 새로 생긴 오류·경고를 알린다.

**Architecture:**
- 새 프로젝트 `src/PellyCode.McpServer`가 `PellyCode.Core`를 프로세스 안에서 직접 부른다.
- 프로세스는 OpenCode가 띄운 동안 살아 있다. 그동안 Roslyn `Solution`을 캐시하고, HEAD 기준 빌드·테스트를 작업 공간 밖 사본에서 미리 만들어 둔다.
- 「수정 전」은 「지금 트리에서 git이 바뀌었다고 보는 파일만 HEAD 내용으로 되돌린 상태」다.

**Tech Stack:**
- .NET 10, C#
- `ModelContextProtocol` 2.4.0, `Microsoft.Extensions.Hosting` 10.0.14
- xUnit v3(`xunit.v3.mtp-v2`), System.IO.Abstractions
- git CLI

**Spec:** `docs/superpowers/specs/2026-11-17-pelly-mcp-server-design.md` (§11은 사람 결정). 계획과 설계가 다르면 설계가 이긴다.

## Global Constraints

- **이름:** 프로젝트 `src/PellyCode.McpServer`, `<AssemblyName>PellyCodeMcpServer</AssemblyName>`, MCP 서버 이름 `pellycode-mcp-server`. 도구 이름은 정확히 `pelly_impact`, `pelly_verify`이고 둘뿐이다.
- **참조:** Core만 참조한다. Cli는 참조하지 않는다. 서버는 LLM·Ollama를 부르지 않는다.
- **표준 출력:** MCP 메시지 말고는 한 바이트도 쓰지 않는다. 로그는 stderr와, `--log-file <경로>`를 주면 그 파일에만 쓴다. 예외는 `--check`이고, 이때는 MCP를 띄우지 않는다.
- **사용자 git 상태:** 바꾸지 않는다. 쓰는 git 명령은 `status`, `show`, `rev-parse`, `ls-files`(읽기)뿐이다. `stash`, `worktree`, `checkout`, `add`, `reset`은 쓰지 않는다. 모든 git 호출에 `-c core.quotepath=false`를 붙인다.
- **작업 공간 루트:** `--root <폴더>` 인자, 없으면 현재 폴더. git 저장소가 아니면 두 도구 모두 `git 저장소가 아닙니다: <루트>`를 돌려준다.
- **설정:** `PellyOptionsLoader.Load`로 읽는다. 순서는 사용자 설정(`PellyPaths.UserConfigPath()`), `<루트>/pelly.json`, 환경 변수(`PELLY__*`, `PELLY_SQL_CONNECTION`)로 Cli `OptionsResolver`와 같다. 운영 연결(`sql.production`)은 쓰지 않는다.
- **HEAD 기준 사본 위치:** `%LocalAppData%/PellyCode/mcp-base/<루트 전체 경로(소문자)의 SHA-256 앞 16자>/`. macOS·Linux에서는 `Environment.SpecialFolder.LocalApplicationData`. 작업 공간 안에는 두지 않는다.
- **사본에서 뺄 폴더 이름(어느 깊이든, 대소문자 무시):** `bin`, `obj`, `.git`, `.pelly`, `.vs`, `.svn`, `.hg`, `$tf`, `node_modules`, `TestResults`. `packages`는 빼지 않는다(레거시 빌드에 필요).
- **출력 언어와 형식:** 사용자에게 보이는 출력은 한국어이고, 설계 §6·§7의 형식을 따른다.
  - `pelly_impact` 결과는 50곳까지 보이고 넘으면 「외 N곳」.
  - `pelly_verify` 결과는 묶음마다 20줄까지 보이고 넘으면 「외 N건」. include하는 페이지는 50개까지.
- **오류와 경고 분류(설계 §7):**
  - 오류: 새 빌드 오류, 새로 실패한 테스트, 새 SQL 컴파일 오류, 새 VBScript 구문 오류, `AspNameCheck`의 새 미정의 이름.
  - 경고: 그 밖의 검사 모두.
  - 첫 줄은 「결과: 새 오류 N건, 새 경고 M건」이다. 둘 다 0이면 「결과: 새 문제 없음」.
- **외부 통신 없음:** `NetworkPolicyTests`가 `src/` 전체를 훑으므로 새 코드도 그 규칙을 지킨다. 프로세스 실행은 `IProcessRunner`로만 한다.
- **게이트:** `dotnet build PellyCode.sln -warnaserror` 종료 0, `dotnet test PellyCode.sln` 실패 0. 건너뜀은 SQL 서버·Ollama·Windows 조건뿐이다(지금 98).
- **작업 위치:** 빌드·테스트는 워크트리 `~/git-root/Pelly-Code-mcp`에서만 돌린다.
- **커밋:** 커밋 끝은 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. 비밀번호·내부 주소·로컬 임시 경로(`/private/tmp/claude-…`)는 커밋하지 않는다. SQL 비밀번호(`~/.pelly-sql.env`)는 에이전트가 읽지 않는다. SQL이 필요한 실행은 사람이 `!`로 돌린다.
- **문서 동기화:** 기능이나 옵션이 바뀌면 `deploy/field-checklist.md`도 고친다(작업 규칙).

## Review Focus

1. **경로에 공백·한글이 있을 때.** 또 git이 Windows에서 `/`로 돌려준 경로일 때. 모든 도구가 그 파일을 찾아야 한다. → Task 3 `Changes_PathWithSpaceAndHangul`.
2. **이름이 바뀐 파일(`R`).** 수정 전은 옛 경로의 HEAD 내용, 수정 후는 새 경로의 지금 내용이다. 같은 파일의 문제를 「새 문제」로 두 번 세지 않아야 한다. → Task 3 `Changes_Rename_KeepsOldPath`, Task 10 `Verify_RenamedSql_NoNewError`.
3. **CP949 ASP·SQL 파일.** HEAD 바이트는 지금 파일과 같은 코덱(`TextFileCodec`/`WebTextCodec`)으로 풀어야 한다. 아니면 한글이 깨져 가짜 「새 이름」 경고가 난다. → Task 10 `Verify_Cp949Asp_UnchangedNames_NoWarning`.
4. **삭제된 `.cs` 파일.** 영향 프로젝트는 옛 경로로 정한다. 빌드는 하고, 없는 파일의 텍스트 검사는 건너뛴다. → Task 9 `Verify_DeletedCs_BuildsOwningProject`.
5. **기준 작업이 도는 중에 verify가 두 번 불릴 때.** 기준 작업은 한 번만 돌고, 두 호출 모두 그 결과를 쓴다. → Task 8 `Baseline_ConcurrentRequests_OneBuild`.

---

## File Structure

```
src/PellyCode.McpServer/
  PellyCode.McpServer.csproj      Exe, AssemblyName PellyCodeMcpServer, Core 참조, win-x64 단일 파일 그룹(Cli와 같은 속성)
  Program.cs                      호스트·stdio·--root·--log-file·--check
  ServerContext.cs                루트, PellyOptions, Workspace, 도구 잠금
  Git/GitRepo.cs                  HEAD·변경 파일·HEAD 내용
  Git/GitChange.cs                변경 레코드
  Impact/ImpactService.cs         확장자별 대상 정하기와 찾기
  Impact/CachingSemanticLoader.cs Solution 캐시
  Impact/ImpactFormatter.cs       결과 텍스트
  Baseline/MirrorPaths.cs         사본 위치
  Baseline/BaseMirror.cs          사본 맞추기(전체 → 증분), HEAD로 되돌리기
  Baseline/BaselineService.cs     미리 만들기, 상태, 캐시, 동시 요청 합치기
  Verify/VerifyService.cs         변경 → 검사 고르기 → 결과
  Verify/CsVerifier.cs            빌드·테스트 비교
  Verify/SqlVerifier.cs           NOEXEC 비교
  Verify/WebVerifier.cs           ASP·JS·CSS 비교, include하는 페이지, JS 서명
  Verify/VerifyReport.cs          오류·경고 묶음과 텍스트
  Tools/PellyTools.cs             [McpServerToolType] 두 도구
  HealthCheck.cs                  --check
tests/PellyCode.McpServer.Tests/  단위·통합(임시 git 저장소)·MCP 프로토콜 시험
deploy/mcp-server.md              등록·점검·AGENTS.md 권장 줄
eval/PellyCode.Eval/ExternalAgent.cs, eval/opencode/*   효과 측정 연결(Task 13)
```

---

### Task 1: 프로젝트 뼈대, stdio 서버, MCP 프로토콜 시험

**Files:**
- Create: `src/PellyCode.McpServer/PellyCode.McpServer.csproj`, `Program.cs`, `ServerContext.cs`, `Tools/PellyTools.cs`(두 도구가 「아직 구현되지 않음」을 돌려주는 자리)
- Create: `tests/PellyCode.McpServer.Tests/PellyCode.McpServer.Tests.csproj`, `ProtocolTests.cs`
- Modify: `PellyCode.sln`, `Directory.Packages.props`(`ModelContextProtocol` 2.4.0, `Microsoft.Extensions.Hosting` 10.0.14)

**Interfaces:**
- Produces:
  - `sealed class ServerContext(string root, PellyOptions options, Workspace ws)`. 멤버는 `Root`, `Options`, `Workspace`, `SemaphoreSlim ToolLock`(용량 1).
  - `static ServerContext? ServerContext.TryCreate(IFileSystem fs, string root, out string? error)`. error 문구는 「git 저장소가 아닙니다: <루트>」 또는 설정 오류 목록이다.
  - `PellyTools`의 `pelly_impact(string path, int? line = null, string? name = null)`, `pelly_verify()`. 둘 다 `Task<string>`을 돌려준다.

- [ ] **Step 1: 실패하는 시험을 쓴다.** `ProtocolTests.ListTools_ReturnsExactlyTwo`는 빌드된 서버를 SDK 클라이언트(`StdioClientTransport`)로 띄우고 도구 목록이 `["pelly_impact","pelly_verify"]`인지 본다. `ProtocolTests.Stdout_FirstBytesAreJsonRpc`는 서버를 띄우고 initialize 응답 전 stdout에 다른 바이트가 없는지 본다. 클라이언트 쓰는 법은 `~/git-root/mssql-mcp-server/tests/MssqlMcpServer.IntegrationTests`를 읽어 따른다(그 저장소는 고치지 않음). 시험 저장소는 임시 폴더에 `git init`한 빈 저장소다.
- [ ] **Step 2: 시험이 실패하는지 본다.** `dotnet test tests/PellyCode.McpServer.Tests`를 돌리면 프로젝트가 없어 빌드에 실패한다.
- [ ] **Step 3: 구현한다.** mssql-mcp-server `Program.cs`의 구조를 따른다: 콘솔 로그는 stderr, `ClearProviders`, `WithStdioServerTransport`, `WithTools<PellyTools>`. csproj의 win-x64 단일 파일 속성 그룹은 `src/PellyCode.Cli/PellyCode.Cli.csproj`와 같게 둔다(`SelfContained=false` 포함). `Microsoft.CodeAnalysis.Workspaces.MSBuild`의 BuildHost 콘텐츠가 출력에 나오는지도 Cli와 같게 맞춘다.
- [ ] **Step 4: 시험이 통과하는지, 게이트를 지키는지 본다.** `dotnet build PellyCode.sln -warnaserror`, `dotnet test tests/PellyCode.McpServer.Tests` → 통과.
- [ ] **Step 5: 커밋한다.** `feat(mcp): PellyCodeMcpServer skeleton with two placeholder tools`

### Task 2: OpenCode 도구 호출 제한 시간 재기 (스파이크, 제품 코드 없음)

**Files:**
- Modify: 설계 §7 「시간」에 잰 값을 적는다. `deploy/mcp-server.md`의 초안 메모에도 적는다(Task 12에서 다듬음).

- [ ] **Step 1:** 저장소 밖 임시 폴더(scratchpad)에 시험용 MCP 서버를 만든다. 같은 SDK로 `sleep(seconds)` 도구 하나만 두고, 10초마다 진행 알림을 보내는 판과 보내지 않는 판 둘을 만든다.
- [ ] **Step 2:** 개발 PC의 OpenCode 1.18.25에 그 서버를 등록한다(`OPENCODE_CONFIG`로 격리, 2026-11-17 비교와 같은 방식). `opencode run`으로 30·90·300·900초 호출을 시켜, 몇 초에 끊기는지, 진행 알림이 시간을 늘리는지, 등록의 `timeout` 키가 실행 시간에 듣는지 기록한다.
- [ ] **Step 3:** 결과로 `VerifyWaitLimit`을 정한다. 끊기기 전 한도에서 15초를 뺀 값, 상한 600초다. 이 값과 등록 예의 `timeout`을 설계 §7에 적는다. 측정이 불가능하면 `VerifyWaitLimit = 45초`로 두고 그 사실을 적는다.
- [ ] **Step 4: 커밋한다.** `docs(mcp): measured OpenCode MCP tool-call limits`

### Task 3: git 읽기

**Files:**
- Create: `src/PellyCode.McpServer/Git/GitRepo.cs`, `Git/GitChange.cs`
- Test: `tests/PellyCode.McpServer.Tests/GitRepoTests.cs`(진짜 git, 임시 저장소)

**Interfaces:**
- Produces:
  - `enum GitChangeKind { Modified, Added, Deleted, Renamed }`
  - `sealed record GitChange(string Path, GitChangeKind Kind, string? OldPath)`. 경로는 루트 기준이고 `/`로 구분한다. 추적되지 않는 새 파일은 `Added`다.
  - `sealed class GitRepo(IProcessRunner processes, string root)`. 메서드는 다음 셋이다.
    - `Task<string?> HeadAsync(CancellationToken ct)`: 커밋이 없으면 null.
    - `Task<IReadOnlyList<GitChange>> ChangesAsync(CancellationToken ct)`: `status --porcelain=v1 -z --untracked-files=all`. 무시된 파일은 빠진다.
    - `Task<byte[]?> ShowHeadAsync(string path, CancellationToken ct)`: `show HEAD:<path>`의 원본 바이트. HEAD에 없으면 null.

- [ ] **Step 1: 실패하는 시험을 쓴다.**
  - `Changes_ModifiedAddedDeletedUntracked`: 네 종류가 각각 하나씩 나온다.
  - `Changes_Rename_KeepsOldPath`: `git mv a.cs b.cs` → `GitChange("b.cs", Renamed, "a.cs")`.
  - `Changes_PathWithSpaceAndHangul`: `소스 폴더/주문 처리.asp`가 그대로 나온다.
  - `Changes_IgnoredFileExcluded`.
  - `ShowHead_ReturnsOriginalBytes_Cp949`: CP949 바이트가 그대로 돌아온다.
  - `ShowHead_NewFile_Null`.
  - `Head_NoCommits_Null`.
- [ ] **Step 2: 실패하는지 본다.**
- [ ] **Step 3: 구현한다.** `-z` 출력을 NUL로 자른다. 이름 바꿈 항목은 다음 토큰이 옛 경로다. 모든 호출에 `-c core.quotepath=false`를 붙이고, 제한 시간은 30초다.
- [ ] **Step 4: 통과하는지 본다.**
- [ ] **Step 5: 커밋한다.** `feat(mcp): read HEAD, changed files and HEAD contents through git`

### Task 4: Core — 영향 결과에 줄 번호

**Files:**
- Modify: `src/PellyCode.Core/Impact/ImpactModel.cs`(`ImpactTarget`), `Impact/SemanticImpactFinder.cs`, `Impact/NameImpactFinder.cs`
- Test: `tests/PellyCode.Core.Tests`의 기존 영향 찾기 시험 파일에 시험을 더한다.

**Interfaces:**
- Produces: `ImpactTarget`에 마지막 위치 매개변수 `int? Line = null`을 더한다. 기존 호출과 pelly CLI 출력은 바뀌지 않는다. 값은 참조 위치의 1부터 세는 줄 번호다.

- [ ] **Step 1: 실패하는 시험을 쓴다.**
  - `Semantic_Target_HasCallLine`: 코퍼스 형태의 작은 솔루션에서 호출 줄 번호가 정확한지 본다.
  - `Name_Target_HasCallLine`.
  - `CliImpactOutput_Unchanged`: 기존 스냅샷이나 문구 시험이 그대로 통과해야 한다.
- [ ] **Step 2: 실패하는지 본다.**
- [ ] **Step 3: 구현한다.** 의미 분석은 `Location.GetLineSpan().StartLinePosition.Line + 1`로, 이름 기준은 찾은 줄로 채운다. 재정의·구현 대상은 선언 줄을 쓴다.
- [ ] **Step 4: 통과하는지 본다.** 전체 `dotnet test`도 돌린다.
- [ ] **Step 5: 커밋한다.** `feat(core): impact targets carry the line of the reference`

### Task 5: `pelly_impact` — C#

**Files:**
- Create: `Impact/ImpactService.cs`, `Impact/CachingSemanticLoader.cs`, `Impact/ImpactFormatter.cs`
- Modify: `Tools/PellyTools.cs`
- Test: `tests/PellyCode.McpServer.Tests/ImpactCsTests.cs`. 진짜 MSBuild를 쓰고, `eval/corpus/Modern`을 임시 git 저장소로 복사해 쓴다.

**Interfaces:**
- Consumes:
  - `ServerContext`
  - `ImpactTarget.Line`(Task 4)
  - `MemberLocator.FindAtLine` / `FindByName`
  - `ImpactScope.Projects`
  - `SemanticImpactFinder(fs, loader)`
- Produces:
  - `sealed class ImpactService(IFileSystem fs, ServerContext ctx, ISemanticLoader loader)`와 `Task<string> FindAsync(string path, int? line, string? name, CancellationToken ct)`. 이 메서드가 도구 결과 텍스트를 돌려준다.
  - `sealed class CachingSemanticLoader(ISemanticLoader inner, IFileSystem fs) : ISemanticLoader`. 프로젝트 묶음을 키로 `Solution`을 캐시한다.
    - 다시 불리면 수정 시각이 바뀐 `.cs`만 `WithDocumentText`로 갱신한다.
    - 묶음 안 `.csproj`의 수정 시각이 바뀌면 다시 불러온다.
    - 돌려주는 `SemanticLoad`의 owner는 null이다.

- [ ] **Step 1: 실패하는 시험을 쓴다.**
  - `Cs_ByLine_ListsCallers`: `OrderService.cs`의 `CancelAsync` 안 줄을 주면, 첫 줄이 `대상: OrderService.CancelAsync (Modern/Shop.Api/Services/OrderService.cs:<줄>)`이고, 컨트롤러 호출 줄이 `- <경로>:<줄> <클래스>.<멤버> — 호출` 꼴로 들어 있다.
  - `Cs_ByName`
  - `Cs_NoLineNoName_Error`: 「line이나 name을 주세요」.
  - `Cs_NameFallback_NotesIt`: 로더 실패를 흉내 내면 첫 줄 다음에 「이름 기준으로 찾았습니다(빠진 곳이 있을 수 있음)」.
  - `Cs_Over50_Truncated`: 「외 N곳」.
  - `CachingLoader_SecondCall_NoReload`: 가짜 inner의 로드 횟수가 1이다.
  - `CachingLoader_EditedCs_SeesNewCall`: 호출을 더한 파일을 저장한 뒤 다시 부르면 새 호출이 보인다.
  - `Unsupported_Extension`: 「지원하지 않는 파일 형식」.
- [ ] **Step 2: 실패하는지 본다.**
- [ ] **Step 3: 구현한다.** 도구 메서드는 `ToolLock` 안에서 `ImpactService`를 부른다. 경로는 상대(루트 기준)와 절대를 모두 받는다.
- [ ] **Step 4: 통과하는지 본다.**
- [ ] **Step 5: 커밋한다.** `feat(mcp): pelly_impact for C# members with a cached Roslyn solution`

### Task 6: `pelly_impact` — SQL·JS

**Files:**
- Modify: `Impact/ImpactService.cs`, `Impact/ImpactFormatter.cs`
- Test: `tests/PellyCode.McpServer.Tests/ImpactWebSqlTests.cs`. `eval/corpus/Asp`, `eval/corpus/Sql`, `eval/corpus/Modern`을 한 임시 git 저장소에 넣고, `pelly.json`에 `asp`·`web` 절을 둔다.

**Interfaces:**
- Consumes:
  - `SqlBatchSplitter.Split(textLf, compatLevel)`
  - `AspImpactFinder.Find(seeds, [], index, _ => null)`
  - `SqlNameStringFinder.Find`
  - `JsImpactFinder.Find(seeds, [], index, access.CachedReader())`
  - `new WebSiteAccess(fs, options.Asp, options.Web, root)`

- [ ] **Step 1: 실패하는 시험을 쓴다.**
  - `Sql_ByLine_ListsAspAndCsCallers`: 코퍼스의 SP 하나를 대상으로, ASP 호출 줄과 C# 문자열 호출 줄이 나온다.
  - `Sql_NoLine_AllObjectsInFile`
  - `Js_ByName_ListsPageCallers`
  - `Js_InPageScript_ByLine`
  - `NoAspSection_SkipsWebPart`: 「설정에 asp 절이 없어 ASP·JS 부분은 건너뜀」.
  - `IndexTruncated_NotesIt`
- [ ] **Step 2: 실패하는지 본다.**
- [ ] **Step 3: 구현한다.** SQL 대상은 줄을 감싸는 객체 배치로 정하고, 이름이 있으면 이름으로, 둘 다 없으면 파일의 모든 객체로 한다. JS 대상은 줄을 감싸는 함수나 이름으로 정한다. `.asp` 안 `<script>`도 같은 방식이다.
- [ ] **Step 4: 통과하는지 본다.**
- [ ] **Step 5: 커밋한다.** `feat(mcp): pelly_impact for stored procedures and JS functions`

### Task 7: HEAD 기준 사본

**Files:**
- Create: `Baseline/MirrorPaths.cs`, `Baseline/BaseMirror.cs`
- Test: `tests/PellyCode.McpServer.Tests/BaseMirrorTests.cs`(임시 폴더, 진짜 git)

**Interfaces:**
- Consumes: `GitRepo`, `GitChange`
- Produces:
  - `static string MirrorPaths.For(string root)`
  - `sealed class BaseMirror(IFileSystem fs, GitRepo git)`와 `Task<MirrorResult> SyncAsync(string root, string mirrorDir, IReadOnlyList<GitChange> changes, CancellationToken ct)`
  - `sealed record MirrorResult(bool Ok, string? FailureReason, int CopiedFiles, int DeletedFiles)`
  - 결과: `mirrorDir`는 「지금 트리 − 제외 폴더」에서 바뀐 추적 파일을 HEAD 바이트로 되돌리고, 새 파일을 지우고, 이름 바꾼 파일은 옛 경로의 HEAD 내용만 남긴 상태가 된다.

- [ ] **Step 1: 실패하는 시험을 쓴다.**
  - `FirstSync_CopiesTreeWithoutExcludedFolders`: `bin`·`obj`·`.git`·`node_modules`는 없고 `packages`는 있다.
  - `Sync_RevertsModifiedToHead`
  - `Sync_RemovesAdded`
  - `Sync_Rename_OldPathWithHeadContent`
  - `SecondSync_CopiesOnlyChanged`: 크기나 수정 시각이 다른 파일만 복사했는지 `CopiedFiles`로 본다.
  - `Sync_DeletesFilesGoneFromTree`
  - `MirrorPaths_OutsideRoot_StableHash`
- [ ] **Step 2: 실패하는지 본다.**
- [ ] **Step 3: 구현한다.** 걷기는 제외 목록을 적용하고, 심볼릭 링크는 따라가지 않는다. 복사 실패(잠긴 파일 등)는 `Ok=false`와 이유로 돌려준다.
- [ ] **Step 4: 통과하는지 본다.**
- [ ] **Step 5: 커밋한다.** `feat(mcp): a HEAD mirror of the workspace outside the root, synced incrementally`

### Task 8: 기준 만들기와 미리 만들기

**Files:**
- Create: `Baseline/BaselineService.cs`
- Modify: `Program.cs`(호스트에 백그라운드 서비스 등록)
- Test: `tests/PellyCode.McpServer.Tests/BaselineServiceTests.cs`. 가짜 빌드·테스트 실행기와 `FakeTimeProvider`를 쓴다.

**Interfaces:**
- Consumes:
  - `BaseMirror`, `GitRepo`
  - `GroupVerifier.BuildAsync(Workspace, projects, ct)`
  - `TestRunner.RunAsync`, `TestProjectLocator.Find`, `SolutionProjects.Read`
- Produces:
  - `enum BaselineStatus { None, Building, Ready, Failed }`
  - `sealed record Baseline(string HeadSha, BaselineStatus Status, GroupBuild? Build, IReadOnlyDictionary<string, TestRun> Tests, IReadOnlyList<string> TestProjectsRun, string? FailureReason, DateTimeOffset At)`. `Tests`의 키는 테스트 프로젝트의 루트 기준 상대 경로다.
  - `sealed class BaselineService : BackgroundService`. 멤버는 다음과 같다.
    - `Task<Baseline> GetAsync(IReadOnlyList<string> projectsRelative, TimeSpan wait, IProgress<string>? progress, CancellationToken ct)`: 준비된 기준을 돌려준다. 한도 안에 못 끝나면 `Building` 상태를 돌려준다.
    - `Baseline? Last`: `--check`용.
  - 미리 만들기: 시작 때와 HEAD 변경 때 한다. HEAD 변경은 30초 주기와 도구 호출 때 `git rev-parse HEAD`로 확인한다. 대상은 루트 `.sln`/`.slnx`의 모든 프로젝트이고, 솔루션이 없으면 미리 만들지 않는다.
  - verify가 미리 만든 범위 밖의 프로젝트를 요청하면, 그 프로젝트만 사본에서 더 빌드해 합친다.

- [ ] **Step 1: 실패하는 시험을 쓴다.**
  - `Start_PrebuildsSolution`
  - `HeadChange_Rebuilds`
  - `Baseline_ConcurrentRequests_OneBuild`
  - `HeadCompileErrors_AreBaseline_NotFailure`: `Ready`이고 `Build.Errors`가 들어 있다.
  - `EnvironmentFailure_Failed_NoBackgroundRetry`: 복원 실패를 흉내 낸다. 30초 주기가 지나도 다시 빌드하지 않는다.
  - `EnvironmentFailure_RetriedOnceOnVerify`
  - `ShutdownMidBuild_NoCacheWritten`
  - `NoSolution_NoPrebuild`
  - `NoTestProjects_TestsEmpty`
- [ ] **Step 2: 실패하는지 본다.**
- [ ] **Step 3: 구현한다.** 디스크 캐시는 `<mirrorDir>/../<해시>.baseline.json`이고 키는 HEAD SHA다. 쓰기는 끝났을 때만 하고, 임시 파일에 쓴 뒤 이름을 바꾼다. 빌드는 사본을 `Workspace(mirrorDir, …)`로 넘겨 돌리므로, 오류 경로는 사본 루트 기준이 되어 지금 트리와 바로 비교할 수 있다.
- [ ] **Step 4: 통과하는지 본다.**
- [ ] **Step 5: 커밋한다.** `feat(mcp): prebuild the HEAD baseline in the background and share it between calls`

### Task 9: `pelly_verify` — C# 빌드·테스트와 보고 형식

**Files:**
- Create: `Verify/VerifyService.cs`, `Verify/CsVerifier.cs`, `Verify/VerifyReport.cs`
- Modify: `Tools/PellyTools.cs`
- Test: `tests/PellyCode.McpServer.Tests/VerifyCsTests.cs`(가짜 실행기), `VerifyCsIntegrationTests.cs`(`eval/corpus/Modern` 임시 저장소, 진짜 dotnet)

**Interfaces:**
- Consumes:
  - `BaselineService.GetAsync`
  - `ProjectFileLocator.FindNearestCsproj`, `ProjectReferenceGraph.AffectedProjects`
  - `GroupVerifier`, `GroupVerifier.NewErrors`
  - `TestRunner`, `TestRegressions.Find`, `TestProjectLocator.Find`
- Produces:
  - `sealed class VerifyReport`. 묶음 추가 메서드는 `Error(string section, string line)`, `Warning(string section, string line)`, `Pass(string section)`, `Skip(string section, string reason)`, `Note(string firstLineNote)`이고, `string Render()`로 텍스트를 만든다.
  - `sealed class VerifyService`와 `Task<string> VerifyAsync(IProgress<string>? progress, CancellationToken ct)`.
  - 처리 순서는 바뀐 파일 → C#·SQL·웹 검사기 → `Render()`이다.

- [ ] **Step 1: 실패하는 시험을 쓴다.**
  - `NoChanges`: 「HEAD 이후 바뀐 파일이 없습니다」.
  - `NewBuildError_Reported_OldOneNot`: HEAD에 있던 오류는 빠지고, 새 `CS1503`은 `- <경로>:<줄> CS1503 <메시지>`로 나온다.
  - `NewFailingTest_Reported`
  - `NoTestProjects_SkipLine`: 「[테스트] 건너뜀 — Shop.Api를 참조하는 테스트 프로젝트 없음. 동작 변경은 확인되지 않았습니다」. 오류 수 0.
  - `BaselineFailed_AllErrorsWithNote`: 첫 줄 다음에 「HEAD 기준을 만들지 못해 모든 오류를 보입니다: <이유>」.
  - `BaselineStillBuilding_ReturnsCallAgain`: 「기준 빌드가 아직 진행 중입니다. 잠시 뒤 다시 pelly_verify를 부르세요」.
  - `FirstLine_Counts`: 「결과: 새 오류 2건, 새 경고 0건」.
  - `Over20_Truncated`
  - `Verify_DeletedCs_BuildsOwningProject`
  - 통합 시험 `Integration_BreakBuild_ThenFix`: 깨뜨리면 새 오류 1건, 고치면 「새 문제 없음」.
- [ ] **Step 2: 실패하는지 본다.**
- [ ] **Step 3: 구현한다.** 대기 한도는 Task 2의 `VerifyWaitLimit`이다. 기준 작업을 기다리는 동안 진행 알림을 보낸다. 도구 취소가 오면 지금 빌드를 취소한다(프로세스 트리 종료). 기준 작업은 취소하지 않는다.
- [ ] **Step 4: 통과하는지 본다.**
- [ ] **Step 5: 커밋한다.** `feat(mcp): pelly_verify reports new build errors and failing tests against HEAD`

### Task 10: `pelly_verify` — SQL·ASP·JS·CSS

**Files:**
- Create: `Verify/SqlVerifier.cs`, `Verify/WebVerifier.cs`
- Modify: `Verify/VerifyService.cs`, 필요한 Core 검사의 `InternalsVisibleTo`(`PellyCode.McpServer`)
- Test: `tests/PellyCode.McpServer.Tests/VerifySqlTests.cs`(SQL 서버 조건, 기존 `Env=SqlServer` 특성과 같은 건너뜀 규칙), `VerifyWebTests.cs`

**Interfaces:**
- Consumes:
  - `SqlBatchSplitter.Split`, `SqlServerExecutor.CompileWithNoExecAsync`, `SqlTargetPolicy.Check`
  - `AspNameCheck.NewUnknown`, `PageElements.NewBroken`, `FormSite.NewMismatches`
  - `JsNameCheck.NewUnknown`, `CssPropertyCheck.NewUnknown`, `JsSignatureChanges`, `JsImpactFinder`
  - cscript(Windows)
  - `TextFileCodec`/`WebTextCodec`
- Produces: 같은 `VerifyReport`에 `[SQL] <파일>`, `[ASP] <파일>`, `[JS] <파일>`, `[CSS] <파일>` 묶음을 더한다.

- [ ] **Step 1: 실패하는 시험을 쓴다.**
  - `Sql_NewCompileError_IsError`
  - `Sql_ErrorAlreadyInHead_NotReported`
  - `Sql_NoConnection_Skipped`: 「[건너뜀] SQL 컴파일 검사 — SQL 연결이 없습니다」.
  - `Sql_NotAllowListed_Skipped`
  - `Verify_RenamedSql_NoNewError`
  - `Asp_NewUnknownName_IsError`
  - `Js_NewUnknownName_IsWarning`
  - `IncChanged_IncluderPagesChecked`: include하는 페이지의 새 경고가 나온다.
  - `IncChanged_Over50Pages_Noted`
  - `JsSignatureChanged_CallerArgMismatch_Warning`
  - `Css_NewUnknownProperty_Warning`
  - `Verify_Cp949Asp_UnchangedNames_NoWarning`
  - `NonWindows_AspSyntaxSkipped`
- [ ] **Step 2: 실패하는지 본다.**
- [ ] **Step 3: 구현한다.** 「수정 전」 텍스트는 바뀐 파일의 HEAD 바이트를 지금 파일과 같은 코덱으로 푼 것이다. 바뀌지 않은 파일은 양쪽 모두 지금 트리에서 읽는다(설계 §7). SQL은 객체 이름으로 HEAD 배치와 짝지어, 새 오류만 낸다.
- [ ] **Step 4: 통과하는지 본다.**
- [ ] **Step 5: 커밋한다.** `feat(mcp): pelly_verify checks SQL, ASP, JS and CSS changes against HEAD`

### Task 11: `--check`와 로그

**Files:**
- Create: `HealthCheck.cs`
- Modify: `Program.cs`
- Test: `tests/PellyCode.McpServer.Tests/HealthCheckTests.cs`

**Interfaces:**
- Produces: `static Task<int> HealthCheck.RunAsync(ServerContext? ctx, string? contextError, BaselineService? baseline, IReadOnlyList<string> tools, TextWriter output, CancellationToken ct)`. 실패 행이 있으면 1, 없으면 0을 돌려준다.
  - 행: 루트·git, 설정, .NET SDK·MSBuild(`pelly doctor`와 같은 판정 함수), BuildHost 폴더, SQL 연결(설정 시), 도구 목록, 마지막 기준 상태(HEAD·상태·이유).

- [ ] **Step 1: 실패하는 시험을 쓴다.**
  - `NotGitRepo_Fails`
  - `MissingBuildHost_Fails`
  - `ListsTwoTools`
  - `ShowsLastBaselineFailureReason`
  - `LogFile_ReceivesStartupFailure`: 잘못된 `pelly.json`으로 시작 실패를 일으키면 로그 파일에 그 문구가 남는다.
- [ ] **Step 2: 실패하는지 본다.**
- [ ] **Step 3: 구현한다.** `--check`는 기준을 새로 만들지 않고 디스크 캐시만 읽는다.
- [ ] **Step 4: 통과하는지 본다.**
- [ ] **Step 5: 커밋한다.** `feat(mcp): --check self-test and the log file option`

### Task 12: 반입 묶음, CI, 문서

**Files:**
- Modify:
  - `.github/workflows/ci.yml`: 두 프로젝트를 같은 폴더에 publish한다. 100MB 상한을 `PellyCodeMcpServer.exe`에도 적용한다. 고정 파일 수를 다시 센다.
  - `deploy/build-package.ps1`: 한 폴더에서 실행 파일 둘을 읽는다. SBOM에 서버 프로젝트를 더한다.
  - `deploy/package-files.txt`
  - `tests/PellyCode.Integration.Tests/Package/PackageTests.cs`
  - `deploy/no-network.md`
  - `THIRD-PARTY-NOTICES.md`: MCP SDK, Hosting
  - `deploy/install.md`, `deploy/field-checklist.md`(MCP 확인 절), `README.md`
  - `docs/PRD.md`: v1.35, 구성 §2·§11
- Create: `deploy/mcp-server.md`

- [ ] **Step 1: 실패하는 시험을 쓴다.**
  - `PackageTests`: 기대 목록에 `PellyCodeMcpServer.exe`와 `docs/mcp-server.md`를 더한다.
  - `BuildHost_SameForBothExecutables`: CI에서 두 publish의 `BuildHost-*` 내용 해시가 같은지 본다. 같은 폴더 publish라면 덮어쓰기 충돌이 없는지 보는 것으로 대신한다.
  - `FieldChecklist_CommandLines_UseRealCommandsAndOptions`가 새 MCP 절의 `PellyCodeMcpServer.exe --check`를 다루는지도 본다. 이 시험은 `pelly` 명령만 보므로 필요하면 서버 명령도 받게 넓힌다.
- [ ] **Step 2: 실패하는지 본다.**
- [ ] **Step 3: 구현한다.** `deploy/mcp-server.md`에는 다음을 넣는다.
  - OpenCode 등록 예: `"mcp": { "pelly": { "type": "local", "command": [...], ... } }`, Task 2에서 정한 `timeout`, `codemode` 주의.
  - `--root`, `--log-file`, `--check`
  - 기준 사본 위치와 지우는 법
  - 테스트가 없을 때의 안내(`pelly test`로 미리 만들기)
  - AGENTS.md 권장 줄(설계 §8 문구 그대로)
  - mssql-mcp-server와 함께 쓰는 예
- [ ] **Step 4: 통과하는지 본다.** 전체 게이트를 돌린다. Windows 패키지 확인은 CI에서 한다.
- [ ] **Step 5: 커밋한다.** `build(mcp): ship PellyCodeMcpServer.exe in the pelly zip with its docs`

### Task 13: 효과 측정 연결과 측정

**Files:**
- Create:
  - `eval/PellyCode.Eval/ExternalAgent.cs`: 2026-11-17 스파이크를 새 이름으로 옮긴다. 환경 변수 `PELLY_EVAL_AGENT`.
  - `eval/opencode/opencode-agent.sh`: `${target}를` 수정판. 변수 뒤 한글은 반드시 `${}`로 감싼다.
  - `eval/opencode/README.md`
  - `docs/mcp-effect.md`: 측정 결과
- Modify: `eval/PellyCode.Eval/Program.cs`(`Orchestrate` 연결)
- Test: `tests/PellyCode.Eval.Tests/ExternalAgentTests.cs`. 가짜 스크립트로 인자 순서, `agent.log`, 시간 초과 124를 본다.

- [ ] **Step 1: 실패하는 시험을 쓴다.** `ExternalAgent_PassesArgsInOrder`, `ExternalAgent_TimeoutIs124`, `ExternalAgent_WritesAgentLog`.
- [ ] **Step 2: 실패하는지 본다.**
- [ ] **Step 3: 구현한다.** README에는 OpenCode 설정 두 변형을 적는다. 둘 다 Ollama `qwen3.6:35b-a3b-q4_K_M`, `--pure --auto`이다.
  - (a) MCP 없음
  - (b) `pelly` MCP 등록
  - (b)에서 AGENTS.md 권장 줄을 넣는 경우와 빼는 경우는 스위트 `overlay`로 나눈다.
- [ ] **Step 4: 통과하는지 본다.** 그다음 C# 30건을 (a)·(b-줄 있음)·(b-줄 없음)으로 1회씩 돌린다. SQL 20건은 사람이 `!`로 돌릴 스크립트를 scratchpad에 만들어 안내한다(비밀번호는 `~/.pelly-sql.env`, 에이전트는 읽지 않음).
  - 기록할 것: 성공 수, 깨진 채 끝남, `pelly_verify`·`pelly_impact` 호출 작업 수(`agent.log`에서 셈), 시간 중간값.
  - 결과를 `docs/mcp-effect.md`에 쓴다. 이 PC의 모델 digest(`b52ca43760e9`)가 현장 파일과 다르다는 점과 1회 측정이라는 한계를 적는다.
- [ ] **Step 5: 커밋한다.** `eval: run another coding agent (OpenCode) through the benchmark and record the MCP effect`
