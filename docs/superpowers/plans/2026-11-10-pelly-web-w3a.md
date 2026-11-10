# W3a — 명세 js·css 작업, JS 함수 서명 변경과 부르는 곳 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 명세에 `js`·`css` 작업을 더하고, 명세 안에서 JS 함수의 이름·매개변수를 바꿀 수 있게 하며, 그 함수를 부르는 곳을 찾아 `spec check --impact`와 apply 뒤에 경고한다.

**Architecture:**
- 명세 파서·검사·실행기에 두 종류를 더하고, W1·W2 Web 파이프라인을 그대로 부른다(A2의 asp 갈래와 같은 모양).
- W2 사이트 색인(`WebSiteIndex`)에 JS 호출과 `<script src>` 관계를 더한다(캐시 판 3 → 4).
- 그 위에 영향 탐색(`JsImpactFinder`)과 적용 뒤 서명 비교(`JsSignatureChanges`)를 둔다.
- 서명 변경 허용은 `RunRequest.AllowSignatureChange` 하나로 켠다.

**Tech Stack:** .NET 10, C#, xUnit v3, System.IO.Abstractions(MockFileSystem), Acornima(이미 있음).

**Spec:** `docs/superpowers/specs/2026-11-10-pelly-web-w3-design.md` — 이 계획은 §3·§4·§6과 §8의 W3a 부분이다. W3b(§5 폼 필드)는 범위 밖이다.

## Global Constraints

- 사용자에게 보이는 문구는 한국어이고, 설계 문서의 문구를 그대로 쓴다(아래 각 작업에 옮겨 둠).
- 게이트: `dotnet build -warnaserror` 종료 0, `dotnet test` 실패 0. 건너뜀은 SQL 서버·Ollama·Windows 전용 테스트뿐이다.
- `dotnet test`는 `-nologo` 없이 돌린다(`-nologo`이면 테스트 0개).
- 모든 빌드·테스트는 워크트리 `/Users/payletter/git-root/Pelly-Code-w3`에서 돌린다. 메인 체크아웃에서는 돌리지 않는다.
- 비밀번호·내부 주소·사용자 이름·로컬 임시 경로(`/private/tmp/claude-501/…`)를 커밋하지 않는다.
- 커밋 메시지 끝: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- 단독 `pelly js`·`pelly css`·`pelly asp`의 기존 결과는 바뀌지 않는다. 새 행·새 옵션을 줄 때만 달라진다(설계 §8 「단독 실행」).
- 경고는 종료 코드를 바꾸지 않는다(설계 §4.5).
- 색인 상한 2000, 함수 하나당 경고 10개와 「외 N곳」(설계 §4.4).

## Review Focus

1. 같은 `.asp` 파일을 asp 작업과 js 작업이 함께 고치는 명세. §3.2 규칙이 종류를 가리지 않고 적용되어야 한다. 앞 작업이 줄을 밀어도 뒤의 `#이름` 작업은 다시 찾아야 한다. → Task 2 `LinesAfterEdit_AcrossKinds`, Task 3 `Apply_AspThenJsSamePage`.
2. 최상위 함수 `function fnSearch(f)`를 `fnFind(f)`로 바꾸는 경우. 재시도 피드백 뒤 두 번째 시도에서도 대상을 찾아야 한다(`RefindFunction`). → Task 4 `Slice_Retry_FindsRenamedTarget`.
3. 문자열·주석 속의 `fnSearch(`, 그리고 `function fnSearch(` 선언 자체. 호출로 세면 안 된다. → Task 5 `Calls_IgnoreStringsCommentsAndDeclarations`.
4. 색인 캐시 판 3으로 저장된 기존 캐시 파일. 판이 다르면 버리고 다시 만들어야 한다. 옛 캐시의 빈 호출 목록을 믿으면 영향 탐색이 조용히 비게 된다. → Task 5 `Cache_Version3_IsRebuilt`.
5. apply 중 그룹이 되돌려진 js 작업. 되돌린 작업의 서명 변경은 적용 뒤 확인에서 빠져야 한다(`_restored`). → Task 7 `AfterApply_RestoredTask_NoWarning`.

---

## File Structure

- `src/PellyCoder.Core/Specs/ChangeSpec.cs`, `ChangeSpecParser.cs` — 종류·대상 문법(Task 1).
- `src/PellyCoder.Core/Specs/SpecChecker.cs` — js·css 검사, §3.2 넓히기, 영향 탐색 연결(Task 2, 6).
- `src/PellyCoder.Core/Web/WebTargetLookup.cs` (새) — spec check와 LOCATE가 함께 쓰는 「파일·페이지에서 함수·규칙 찾기」(Task 2).
- `src/PellyCoder.Core/Specs/FsmTaskExecutor.cs`, `ITaskExecutor.cs` — 실행 갈래(Task 3, 8).
- `src/PellyCoder.Core/Fsm/RunRequest.cs` — `AllowSignatureChange`, `ChangedJsFunctions`(Task 4, 8).
- `src/PellyCoder.Core/Web/JsTargetMatch.cs` (새) — 「같은 자리」 규칙(Task 4).
- `src/PellyCoder.Core/Web/JsChecks.cs`, `Fsm/Phases/WebSlicePhase.cs`, `Fsm/Phases/WebApplyPhase.cs`, `Llm/PromptResources.cs` — 서명 변경 허용(Task 4).
- `src/PellyCoder.Core/Web/JsCallFinder.cs` (새), `Web/WebSiteIndex.cs` — 호출 색인(Task 5).
- `src/PellyCoder.Core/Impact/JsImpactFinder.cs` (새), `Impact/ImpactCheck.cs`, `Impact/SqlImpactList.cs` 옆 `Impact/JsImpactList.cs` (새) — 영향 탐색(Task 6).
- `src/PellyCoder.Core/Web/JsSignatureChanges.cs` (새), `Specs/SpecRunner.cs`, `Specs/SpecRunnerFactory.cs`, `Reporting/*` — 적용 뒤 확인(Task 7).
- `src/PellyCoder.Core/Verification/AspJsSignatureStep.cs` (새) — asp 단독 경고 행(Task 9).
- `src/PellyCoder.Cli/Commands/JsCommand.cs`, `RunArguments.cs`, 자동완성 — `--signature`(Task 4).
- 문서·코퍼스·벤치마크(Task 10).

---

### Task 1: 명세 문법 — `js`·`css` 종류와 대상

**Files:**
- Modify: `src/PellyCoder.Core/Specs/ChangeSpec.cs:7`, `src/PellyCoder.Core/Specs/ChangeSpecParser.cs:12-23,106,191-218`
- Test: `tests/PellyCoder.Core.Tests/Specs/ChangeSpecParserTests.cs`

**Interfaces:**
- Produces:
  - `enum TaskKind { CSharp, Sql, Asp, Js, Css }`.
  - `KindKeyword(TaskKind.Js) == "js"`, `KindKeyword(TaskKind.Css) == "css"`.
  - js 작업의 `#이름`은 `SpecTask.Symbol`(예 `Common.fnX`)에 담는다.
  - css 작업의 `#선택자`도 `SpecTask.Symbol`에 그대로 담는다(예 `.search-box`, `#main`, `td.num`). Ruling: `Symbol` 재사용(설계 §9가 빌더에게 맡김).
  - `#L…`는 `SpecTask.Lines`에 담는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다** (`ChangeSpecParserTests`)
  - `Js_FunctionTarget`: `- [ ] T1 js js/order.js#fnSearch 페이지 번호를 더한다`이면 `Kind == Js`, `Path == "js/order.js"`, `Symbol == "fnSearch"`, `Lines == null`.
  - `Js_QualifiedTarget`: `#Common.fnX`이면 `Symbol == "Common.fnX"`.
  - `Js_LinesTarget_OnPage`: `T2 js admin/list.asp#L10-20 …`이면 `Lines == new TextSpanInfo(10, 20)`, `Symbol == null`, 문제 없음.
  - `Css_SelectorTargets`: `css/admin.css#.search-box`이면 `Symbol == ".search-box"`, `css/admin.css##main`이면 `Symbol == "#main"`.
  - `Css_IsNotCs`: `T4 css css/a.css#.x 요청`이면 `Kind == Css`(`CSharp`가 아님). `T4 cs src/A.cs#M 요청`이면 `Kind == CSharp`.
  - `Lines_StillRejectedForCsAndSql`: cs·sql의 `#L1-2`는 여전히 `LinesNotSupported`. 문구는 「작업 {id}: #L 줄 범위는 asp·js·css 작업에만 쓸 수 있습니다.」.
  - `GroupAndDoneMarks_WithJs`: `- [x] T1 js a.js#f 요청 [G1] <!-- run: r1 -->`이면 `Done`, `Group == "G1"`, `RunId == "r1"`.
  - `SyntaxError_MentionsNewKinds`: 문법이 틀린 줄의 문구에 `<cs|sql|asp|js|css>`가 들어 있다.
- [ ] **Step 2: 실행해서 실패를 본다**
  - Run: `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~ChangeSpecParserTests"`
  - Expected: 새 테스트가 FAIL한다(`js`가 작업 줄로 해석되지 않음).
- [ ] **Step 3: 구현한다**
  - 정규식 세 곳의 `cs|sql|asp`를 `cs|sql|asp|js|css`로 바꾼다.
  - `ToTask`의 종류 switch와 `KindKeyword`를 고친다.
  - `#L` 허용 조건을 `kind is Asp or Js or Css`로 바꾼다.
  - 작업 줄의 `(?:#(\S+))?`는 첫 `#`에서 자르므로 `##main` → `Symbol "#main"`이 된다. 테스트로 확인만 한다.
- [ ] **Step 4: 통과를 본다**
  - Run: 같은 필터.
  - Expected: PASS. 기존 `ChangeSpecParserTests`도 모두 통과한다. 문구를 바꾼 기존 테스트는 새 문구로 고친다.
- [ ] **Step 5: 커밋** — `feat(spec): js and css task kinds`

---

### Task 2: `spec check` — js·css 작업 검사와 §3.2 넓히기

**Files:**
- Create: `src/PellyCoder.Core/Web/WebTargetLookup.cs`
- Modify: `src/PellyCoder.Core/Specs/SpecChecker.cs:330-470`, `src/PellyCoder.Core/Fsm/Phases/JsLocatePhase.cs`, `CssLocatePhase.cs` (찾기 부분만 새 도우미로 옮김)
- Test: `tests/PellyCoder.Core.Tests/Specs/SpecCheckerWebTests.cs` (새), `tests/PellyCoder.Core.Tests/Web/WebTargetLookupTests.cs` (새)

**Interfaces:**
- Consumes: Task 1의 `TaskKind.Js/Css`, `SpecTask.Symbol/Lines`.
- Produces:
  - `public enum WebTargetFind { Found, NotFound, Ambiguous }`.
  - `public static WebTargetFind WebTargetLookup.Function(string textLf, bool isPage, string name)`: 이름 맞추기는 지금 `JsLocatePhase`와 같다(`QualifiedName == name || Name == name`). 페이지는 `PageBlockView.IsJavaScriptBlock` 블록마다 본다. 해석되지 않는 조각은 `JsTokenScan.FunctionDeclarations`로 본다.
  - `public static WebTargetFind WebTargetLookup.Rule(string textLf, bool isPage, string selector)`: `CssSelector.Matches`를 쓰고, 페이지는 `<style>` 블록마다 본다.
  - `public static bool WebTargetLookup.LinesInOneBlock(AspPage page, TextSpanInfo lines, EditKind kind)`: js면 script 블록 하나, css면 style 블록 하나 안에 완전히 들어 있는가.
  - LOCATE 두 단계는 이 도우미가 쓰는 조각 만들기를 공유한다. 동작은 바뀌지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `WebTargetLookupTests`:
    - `Function_InJsFile_FoundAmbiguousNotFound`.
    - `Function_InPageBlock_Found`.
    - `Function_ServerStatementsBlock_UsesTokenScan`: `<% If %>`가 섞인 블록 안의 `function f(){}`도 찾는다.
    - `Rule_InPageStyle_Found`.
    - `LinesInOneBlock_TrueInside_FalseAcrossHtml`.
  - `SpecCheckerWebTests`: 설계 §3.3의 표 한 줄마다 테스트 하나를 쓴다. 코드·수준·문구 일부를 단언한다. MockFileSystem 사이트에 `js/order.js`, `css/admin.css`, `admin/list.asp`(script·style 블록), `lib/jquery-1.8.3.min.js`를 둔다.
    - `KindMismatch_JsOnCss`: 「js 작업의 파일이 .js, .asp, .inc, .htm, .html 중 하나가 아닙니다」. 확장자 목록은 `asp.extensions`를 반영한다.
    - `WebTargetRequired`: 대상이 없는 js·css 작업. 문구는 「작업 T1: js 작업에는 대상(#함수 또는 #L시작-끝)이 필요합니다」이고, css는 「#선택자 또는 #L시작-끝」이다.
    - `SymbolNotFound_Function`, `SymbolAmbiguous_Function`, `SymbolNotFound_Selector`.
    - `LinesOutOfFile_Js`.
    - `LinesOutsideBlock_PageHtml`: 문구 「작업 T1: 줄 범위가 <script> 블록 하나 안에 있지 않습니다. asp 작업으로 쓰십시오」(css는 `<style>`).
    - `WebVendorFile`: W1 `WebFileFilter` 판정이 Ok가 아니면 오류다. 문구에 판정 사유를 넣는다.
    - `WebEncoding`: 해석할 수 없는 바이트이면 오류다.
    - `LinesAfterEdit_AcrossKinds`: 같은 `admin/list.asp`에 T1 asp `#Search`, T2 js `#L10-12` → T2에 `LinesAfterEdit` 오류. 문구는 A2 `AspLinesAfterEdit`와 같다.
    - `LinesAfterEdit_AspStillUsesOldCode`: asp 뒤의 asp `#L`은 여전히 `AspLinesAfterEdit`.
    - `FirstTaskLines_Allowed`: 첫 작업이 `#L`이면 허용한다.
- [ ] **Step 2: 실행해서 실패를 본다**
  - Run: `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~SpecCheckerWebTests|FullyQualifiedName~WebTargetLookupTests"`
  - Expected: 컴파일 실패(새 형식 없음) 뒤 FAIL.
- [ ] **Step 3: 구현한다**
  - `CheckTask`에 `if (task.Kind is TaskKind.Js or TaskKind.Css) { CheckWebTask(…); return; }`를 더한다.
  - `CheckWebTask`는 `AspOptions`(확장자·인코딩·사이트 루트)와 `WebOptions`(vendorPaths)를 받는다. `SpecChecker.Check`에 `WebOptions? web = null` 매개변수를 더하고, CLI 호출부(`SpecCheckCommand`, apply 사전 검사)에서 설정값을 넘긴다.
  - `CheckAspLinesAfterEdit`는 모든 종류의 같은 파일 작업을 본다. 뒤 작업이 asp이면 코드 `AspLinesAfterEdit`, js·css이면 `LinesAfterEdit`를 쓴다. Ruling: 기존 코드·테스트를 깨지 않으려고 코드 이름을 둘로 둔다(설계 §9가 빌더에게 맡김).
  - 파일 비교는 지금처럼 전체 경로, 대소문자 무시다.
- [ ] **Step 4: 통과를 본다**
  - Run: 같은 필터 + `--filter "FullyQualifiedName~SpecChecker|FullyQualifiedName~JsLocate|FullyQualifiedName~CssLocate"`
  - Expected: PASS. 기존 LOCATE 테스트도 모두 통과한다.
- [ ] **Step 5: 커밋** — `feat(spec): check js and css tasks`

---

### Task 3: apply가 js·css 작업을 실행

**Files:**
- Modify: `src/PellyCoder.Core/Specs/FsmTaskExecutor.cs`, `src/PellyCoder.Core/Specs/SpecRunner.cs`(apply.json 작업 항목의 대상 필드), `src/PellyCoder.Core/Specs/ApplySummary.cs`
- Test: `tests/PellyCoder.Core.Tests/Specs/FsmTaskExecutorTests.cs`, `tests/PellyCoder.Core.Tests/Specs/SpecRunnerWebTests.cs` (새)

**Interfaces:**
- Consumes: Task 1.
- Produces:
  - js 작업 → `RunRequest(file, null, task.Symbol, null, task.Request, …, Kind: EditKind.Js, Host: host, Group: o.Group, Lines: task.Lines, AllowSignatureChange: true)`. `AllowSignatureChange`는 Task 4에서 생긴다. 이 작업에서는 아직 넣지 않고, Task 4 Step 3에서 넣는다.
  - css 작업 → `RunRequest(file, null, null, null, task.Request, …, Kind: EditKind.Css, Host: host, Group: o.Group, Lines: task.Lines, Selector: task.Symbol)`.
  - `apply.json` 작업 항목은 `kind: "js"|"css"`이고, 대상 필드는 `function`·`selector`·`lines` 중 하나다(`lines`는 `"10-20"`). 기존 asp 항목의 `proc`·`lines` 모양을 따른다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `FsmTaskExecutorTests`:
    - `Js_FunctionTarget_BuildsJsRequest`: `MethodName == "fnSearch"`, `Kind == Js`, `Lines == null`.
    - `Js_LinesTarget`.
    - `Css_SelectorTarget_BuildsCssRequest`: `Selector == ".search-box"`.
    - `Css_LinesTarget`.
  - `SpecRunnerWebTests`(가짜 실행기, 기존 `SpecRunnerAspTests`의 틀을 따름):
    - `Apply_JsAndCssTasks_RunInOrder_MarkDone`.
    - `Group_SqlAspJs_JsFails_AllRestoredToOriginalBytes`: CP949 바이트 비교.
    - `Group_JsOnly_NoBuild`: 빌드 실행기가 불리지 않는다.
    - `ApplyJson_JsTaskHasKindAndFunction`.
    - `Apply_AspThenJsSamePage`: T1 asp `#Search`가 줄을 3줄 늘린 뒤에도 T2 js `#goPage`가 실행 요청에 이름으로 전달된다.
- [ ] **Step 2: 실패를 본다**
  - Run: `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~FsmTaskExecutorTests|FullyQualifiedName~SpecRunnerWebTests"`
  - Expected: FAIL.
- [ ] **Step 3: 구현한다** — `FsmTaskExecutor`에 두 갈래를 더하고, 클래스 주석을 고친다. 그룹 쪽은 A2가 이미 「cs가 아닌 작업은 빌드 검증 없음」이므로 바꿀 것이 없다. 테스트가 그 사실을 고정한다.
- [ ] **Step 4: 통과를 본다** — 같은 필터와 `FullyQualifiedName~SpecRunner`. Expected: PASS.
- [ ] **Step 5: 커밋** — `feat(apply): run js and css spec tasks`

---

### Task 4: 서명 변경 허용 — 명세 js 작업과 `pelly js --signature`

**Files:**
- Create: `src/PellyCoder.Core/Web/JsTargetMatch.cs`
- Modify:
  - `src/PellyCoder.Core/Fsm/RunRequest.cs`
  - `src/PellyCoder.Core/Web/JsChecks.cs:55-90`
  - `src/PellyCoder.Core/Fsm/Phases/WebSlicePhase.cs:169-174`
  - `src/PellyCoder.Core/Fsm/Phases/WebApplyPhase.cs:161`
  - `src/PellyCoder.Core/Llm/PromptResources.cs`
  - `src/PellyCoder.Core/Web/WebRunState.cs`
  - `src/PellyCoder.Core/Specs/FsmTaskExecutor.cs`
  - `src/PellyCoder.Cli/Commands/JsCommand.cs`, `src/PellyCoder.Cli/Commands/RunArguments.cs:134-136`
  - PowerShell 자동완성 파일(`--signature`)
  - `src/PellyCoder.Core/Reporting/RunLogger.cs`(또는 `summary.json`을 쓰는 곳), `src/PellyCoder.Core/Fsm/Phases/ReviewPhase.cs`
- Test:
  - `tests/PellyCoder.Core.Tests/Web/JsTargetMatchTests.cs` (새)
  - `tests/PellyCoder.Core.Tests/Web/JsChecksTests.cs`
  - `tests/PellyCoder.Core.Tests/Fsm/WebOrchestratorTests.cs`
  - `tests/PellyCoder.Cli.Tests/…JsCommand…` (기존 js 명령 테스트 파일)

**Interfaces:**
- Produces:
  - `RunRequest`에 `bool AllowSignatureChange = false`를 더한다(마지막 선택 매개변수).
  - `public static JsFunction? JsTargetMatch.Find(IReadOnlyList<JsFunction> originals, IReadOnlyList<JsFunction> patched, JsFunction target, bool allowRename)`:
    1. 지금 규칙(같은 `QualifiedName`, 같은 이름 여럿이면 `Occurrence`의 순번)으로 찾으면 그것이다.
    2. 못 찾았고 `allowRename`이면, `patched` 중 `originals`에 없는 `QualifiedName`을 가지면서 `Owner`가 target과 같은 함수가 정확히 하나일 때 그것이다.
    3. 그 밖에는 null이다.
  - `public sealed record JsSignature(string Name, IReadOnlyList<string> Parameters) { public string Display => $"{Name}({string.Join(", ", Parameters)})"; }` (`JsTargetMatch.cs`에 둔다).
  - `WebRunState.SignatureChange`(`(JsSignature Before, JsSignature After)?`): 적용 판정에서 채운다.
  - `summary.json`의 `signatureChange: { before: "fnSearch(f)", after: "fnSearch(f, page)" }`. 바뀌지 않았으면 필드를 쓰지 않는다.
- 문구:
  - 막을 때 기존 두 문구 끝에 「 — 부르는 곳도 고쳐야 하면 명세(js 작업)로 하거나 --signature를 쓰십시오」를 덧붙인다.
  - 검토 화면 줄: `함수 서명 변경: fnSearch(f) → fnSearch(f, page)`.
  - 프롬프트(두 파일의 규칙 5 앞 문장)는 `AllowSignatureChange`일 때 실행 중에 바꿔 끼운다. 바꿀 원문은 `Do not rename the target function and do not change its parameters.`이고, 바꿔 넣을 문장은 `Rename the target function or change its parameters only when the request asks for it; other tasks update its callers.`이다. 뒤 문장 `Do not add new top-level declarations outside the target function.`은 그대로 둔다. 판 번호는 올리지 않는다(Ruling: 파일 판은 그대로이고 실행 중 한 문장만 바꿈 — 설계 §9).

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `JsTargetMatchTests`:
    - `SameName_Found`.
    - `SameNameTwice_PositionKept`.
    - `Renamed_TopLevel_FoundWhenAllowed`.
    - `Renamed_NotAllowed_Null`.
    - `Renamed_TwoNewNames_Null`.
    - `Renamed_OwnerMethod_SameOwnerOnly`: `Obj.a` → `Obj.b`는 찾고, `Other.b`는 찾지 않는다.
  - `JsChecksTests`:
    - `Shape_RenameAllowed_PassesAndRecordsChange`: `fnSearch(f)` → `fnFind(f)`. `TopLevelNames`에 새로 생긴 `fnFind`는 「새 최상위 선언」이 아니다. `SignatureChange`가 `fnSearch(f) → fnFind(f)`이다.
    - `Shape_ParamsAllowed`.
    - `Shape_RenameAllowed_NewOtherTopLevel_StillRefused`: 이름을 바꾸면서 `function helper(){}`를 더하면 「함수 밖에 새 최상위 선언을 만들 수 없습니다: helper」.
    - `Shape_NotAllowed_MessageHasHint`: 문구가 「…(이름을 바꾸지 마십시오) — 부르는 곳도 고쳐야 하면 명세(js 작업)로 하거나 --signature를 쓰십시오」로 끝난다.
  - `WebOrchestratorTests`(가짜 모델):
    - `Js_Signature_RenameApproved_SummaryHasSignatureChange`.
    - `Slice_Retry_FindsRenamedTarget`: 첫 시도가 이름을 바꾸고 문법 오류로 재시도할 때, 두 번째 슬라이스가 바뀐 함수를 대상으로 잡는다.
    - `Js_Signature_PromptSentenceReplaced`: 가짜 백엔드가 받은 시스템 프롬프트에 새 문장이 있고 옛 문장이 없다. 페이지 판도 같다.
    - `Js_NoSignature_PromptUnchanged`.
  - CLI: `Js_SignatureFlag_SetsAllowSignatureChange`, `Js_NoFlag_False`.
- [ ] **Step 2: 실패를 본다**
  - Run: `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~JsTargetMatch|FullyQualifiedName~JsChecksTests|FullyQualifiedName~WebOrchestratorTests"`, 그리고 CLI 테스트 필터.
  - Expected: FAIL.
- [ ] **Step 3: 구현한다**
  - `ShapeViolation`과 `RefindFunction`이 `JsTargetMatch.Find`를 쓰게 한다. `allowRename`은 `ctx.Request.AllowSignatureChange`이고, `WebRunState`에 옮겨 담는다.
  - 대상 함수의 새 이름은 `TopLevelNames` 비교에서 뺀다.
  - 매개변수 비교는 허용일 때 건너뛰고 `SignatureChange`를 기록한다.
  - `FsmTaskExecutor`의 js 갈래에 `AllowSignatureChange: true`를 넣는다.
  - `--signature`는 `JsSettings.Signature`(bool)로 받는다. css에는 없다.
  - README 옵션 표의 갱신은 Task 10에서 한다.
- [ ] **Step 4: 통과를 본다**
  - Run: 같은 필터 + `FullyQualifiedName~Web`.
  - Expected: PASS. 기존 W1·W2 테스트도 모두 통과한다.
- [ ] **Step 5: 커밋** — `feat(js): allow signature changes in spec tasks and with --signature`

---

### Task 5: JS 호출 색인과 `<script src>` 관계

**Files:**
- Create: `src/PellyCoder.Core/Web/JsCallFinder.cs`
- Modify: `src/PellyCoder.Core/Web/WebSiteIndex.cs` (캐시 판 `3` → `4`, `CacheItem`에 `Calls`·`ScriptSources`, 결과에 `JsCalls`·`ScriptSources`), `src/PellyCoder.Core/Specs/SpecChecker.cs:161` (`new WebOptions()` 대신 넘겨받은 `WebOptions`)
- Test: `tests/PellyCoder.Core.Tests/Web/JsCallFinderTests.cs` (새), `tests/PellyCoder.Core.Tests/Web/WebSiteIndexTests.cs`

**Interfaces:**
- Produces:
  - `public enum JsCallSource { Js, PageScript, Attribute }`.
  - `public sealed record JsCall(string Name, int Line, int? Arguments, bool Reference, JsCallSource Source, string? AttributeName = null)`. `AttributeName`은 `Attribute` 출처일 때의 속성 이름(`onclick`, `javascript:`)이다.
    - `Name`은 `(` 바로 앞의 점 이음 이름 전체다(`fnX`, `Common.fnX`).
    - `Reference`는 호출 없이 이름만 넘긴 경우다. 이때 `Arguments`는 null이다.
    - `Arguments`가 null이고 `Reference`가 false이면 「모름」이다.
  - `public static IReadOnlyList<JsCall> JsCallFinder.InScript(string textLf, JsCallSource source)`: `JsTokenScan.Mask` 뒤에 찾는다.
    - 선언(`function 이름(`)과 메서드 정의(`이름: function(`)는 뺀다. `new 이름(`은 호출로 센다. 생성자 이름이 바뀌어도 영향이 있기 때문이다(Ruling).
    - 앞에 `.`이 붙은 이름은 점 이음 전체로 남긴다(`a.fnX` → `"a.fnX"`). 맞추기는 Task 6이 한다.
    - `InScript`는 호출만 돌려준다(`Reference`는 늘 false). 참조를 씨앗 없이 다 뽑으면 모든 식별자가 되기 때문이다.
  - `public static IReadOnlyList<JsCall> JsCallFinder.References(string textLf, string name, JsCallSource source)`: 이름 하나의 참조(`Reference = true`)를 찾는다. 식별자 `name` 뒤에 `(`가 아니라 `,`·`)`·`;`·줄 끝이 오고, 앞이 `(`·`,`·`=`인 경우다(`setTimeout(fnX, 10)`, `b.onclick = fnX;`, `.click(fnX)`). 참조는 색인에 넣지 않는다(Ruling). Task 6·7이 씨앗 이름을 본문에 포함한 파일만 다시 읽어 이 함수로 찾는다.
    - 인수 개수는 짝을 맞춘 최상위 쉼표로 센다. 빈 괄호는 0이다. 짝이 맞지 않으면 null이다.
  - `public static IReadOnlyList<JsCall> JsCallFinder.InPage(AspPage page, string textLf)`:
    - 스크립트 블록(`PageBlockView.Build` 뒤 `InScript`, 줄은 페이지 줄)의 호출.
    - 속성 호출: 정규식 `(?<![\w-])on[a-z]+\s*=\s*(?:""|"|')(?<v>…)` 와 `href\s*=\s*(?:""|"|')\s*javascript:(?<v>…)`. 값 안을 `InScript`로 훑는다. VBScript `""` 따옴표를 지원한다.
  - `WebSiteIndexResult.JsCalls`: `IReadOnlyList<(string RelativePath, JsCall Call)>`, 경로는 사이트 기준이다.
  - `WebSiteIndexResult.ScriptSources`: `IReadOnlyDictionary<string, IReadOnlyList<string>>`, 페이지 상대 경로 → 그 페이지가 직접 `<script src>`로 부르는 `.js` 상대 경로. `PagePaths.ResolveScriptSrc`로 푼다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `JsCallFinderTests`:
    - `Calls_PlainQualifiedAndArgs`: `fnX(a, b(c, d), "x,y")`는 `Arguments == 3`이다. `Common.fnX()`는 `Name "Common.fnX"`, `Arguments == 0`이다.
    - `Calls_IgnoreStringsCommentsAndDeclarations`: `"fnX("`, `// fnX(1)`, `function fnX(a)`, `fnX: function(` 는 모두 0개다.
    - `Calls_Unbalanced_ArgumentsNull`.
    - `Calls_PlaceholderArgument_Counted`: `fnX('<%= a %>', <%= b %>)`가 담긴 페이지 블록은 2개다.
    - `Page_Attributes`: `onclick="fnX(1)"`, `href="javascript:fnX(2, 3)"`, `Response.Write "<a onclick=""fnX()"">"` → 세 호출, `Source == Attribute`, 줄 번호가 맞는다.
    - `Page_ScriptBlockLines_ArePageLines`.
    - `References_SetTimeoutAssignClick`: `setTimeout(fnX, 10)`, `b.onclick = fnX;`, `$(a).click(fnX)` → 세 참조. `fnXy`는 맞지 않는다.
  - `WebSiteIndexTests`:
    - `Index_HasJsCallsAndScriptSources`.
    - `Cache_Version3_IsRebuilt`: 판 3 캐시 파일(`Calls` 없음)을 미리 두면 호출이 채워진다.
    - `VendorFile_NotIndexed_WithConfiguredVendorPaths`: `SpecChecker`가 설정의 `web.vendorPaths`를 쓴다.
- [ ] **Step 2: 실패를 본다** — Run: `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~JsCallFinder|FullyQualifiedName~WebSiteIndex"`. Expected: FAIL.
- [ ] **Step 3: 구현한다** — `ExtractPage`와 `.js` 갈래에서 `JsCallFinder`를 부르고 캐시 항목에 담는다. `ScriptSources`는 `page.ClientBlocks`의 `Src`로 만든다. `CacheVersion = 4`.
- [ ] **Step 4: 통과를 본다** — 같은 필터 + `FullyQualifiedName~Web|FullyQualifiedName~Verification`. Expected: PASS(요소 검사·이름 검사 그대로).
- [ ] **Step 5: 커밋** — `feat(web): index JS calls and script sources`

---

### Task 6: `spec check --impact`의 JS 영향 탐색

**Files:**
- Create: `src/PellyCoder.Core/Impact/JsImpactFinder.cs`, `src/PellyCoder.Core/Impact/JsImpactList.cs`
- Modify: `src/PellyCoder.Core/Impact/ImpactCheck.cs`, `src/PellyCoder.Core/Specs/SpecChecker.cs` (`--impact`일 때 `FindJs`·`AddJsIssues`)
- Test: `tests/PellyCoder.Core.Tests/Impact/JsImpactFinderTests.cs` (새), `tests/PellyCoder.Core.Tests/Specs/SpecCheckerWebTests.cs`

**Interfaces:**
- Consumes: Task 5의 `JsCalls`, `ScriptSources`, `Includes`, `IncludersOf`, `JsCallFinder.References`.
- Produces:
  - `public sealed record JsImpactSeed(string TaskId, string RelativePath, string Function)` — 사이트 기준 경로.
  - `public sealed record JsImpactHit(JsImpactSeed Seed, string RelativePath, int Line, int? Arguments, bool Reference, JsCallSource Source, IReadOnlyList<string> CoveredBy, bool PageDoesNotLoadSeedFile)`.
  - `public static IReadOnlyList<JsImpactHit> JsImpactFinder.Find(IReadOnlyList<JsImpactSeed> seeds, IReadOnlyList<SpecTask> tasksSiteRelative, WebSiteIndexResult index, Func<string, string?> readTextLf)`.
  - 맞추기 규칙:
    - 씨앗 `fnX`(owner 없음)는 `Name == "fnX"`인 호출만 맞는다(`a.fnX`는 아님).
    - 씨앗 `Obj.m`은 `Name == "Obj.m"`만 맞는다.
    - 같은 이름을 그 페이지(와 include)의 블록이 직접 선언하고, 씨앗 파일이 그 페이지가 아니면 뺀다(설계 §4.3).
    - 페이지 호출인데 씨앗이 `.js`이고, 그 페이지와 include들의 `ScriptSources`에 씨앗 파일이 없으면 `PageDoesNotLoadSeedFile = true`로 둔다. 빼지 않는다.
  - 덮음 판정 `CoveredBy`:
    - 같은 파일의 js·asp 작업 대상 안에 있으면 그 작업이 덮는다. 함수 대상이면 그 함수 범위, `#L`이면 범위, asp 프로시저면 그 프로시저 범위다.
    - `Attribute`는 asp 작업만 덮을 수 있다.
  - `ImpactCheck.JsIssues(IReadOnlyList<JsImpactSeed> seeds, IReadOnlyList<JsImpactHit> hits, string? listPath)`:
    - 경고 코드는 `JsImpactNotInSpec`이고, 문구는 「작업 T1의 영향 대상이 명세에 없습니다: admin/order/view.asp:88 (fnSearch 호출, 인수 1개, onclick)」이다.
    - 출처 표시: `Js`이면 없음, `PageScript`이면 「script」, `Attribute`이면 `JsCall.AttributeName`(`onclick`, `javascript:`).
    - 인수 표시: 참조이면 「참조」, 모름이면 「인수 모름」.
    - `PageDoesNotLoadSeedFile`이면 「 (이 페이지는 js/order.js를 부르지 않음)」을 덧붙인다.
    - 함수 하나당 10개, 나머지는 「작업 T1: 외 N곳 — 전체 목록: <listPath>」.
    - `#L` js 작업이 범위 안에 함수 선언을 가지면 경고 `JsImpactLinesTarget`, 문구는 「작업 T2는 줄 범위 대상이라 부르는 곳을 찾지 않았습니다」.
    - 색인이 잘렸으면 경고 `JsImpactTruncated`, 문구는 「사이트 파일이 많아 일부 호출을 보지 못했을 수 있습니다」.
  - `JsImpactList.Write(IFileSystem fs, string path, string? specId, IReadOnlyList<JsImpactSeed> seeds, IReadOnlyList<JsImpactHit> hits)`:
    - 경로는 `.pelly/impact/<명세 id>-js.json`이다.
    - 항목은 `task, function, path, line, arguments, reference, source, coveredBy`이다.
  - apply 사전 탐색(`SpecRunner`의 영향 확인)에는 연결하지 않는다(설계 §4.4).

- [ ] **Step 1: 실패하는 테스트를 쓴다** (`JsImpactFinderTests`)
  - `Seed_FindsJsPageAndAttributeCalls`.
  - `Qualified_Seed_MatchesOnlyQualified`.
  - `PageDeclaresSameName_Excluded`.
  - `PageWithoutScriptSrc_Flagged`.
  - `IncludeScriptSrc_NotFlagged`: `.inc`가 `<script src>`를 넣는 경우다.
  - `Covered_ByFunctionTask_ByLinesTask_ByAspProc`.
  - `Attribute_CoveredOnlyByAsp`.
  - `References_Included`.
  - `SpecCheckerWebTests`:
    - `Impact_WarnsUncovered_Cap10_ListFile`: 12곳이면 경고 10줄과 「외 2곳」이다. 목록 파일의 항목은 12개다.
    - `Impact_LinesTarget_Note`.
    - `Impact_NotRunWithoutFlag`.
    - `Apply_PreImpact_HasNoJsWarnings`: apply 사전 확인 질문이 생기지 않는다.
- [ ] **Step 2: 실패를 본다** — Run: `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~JsImpact|FullyQualifiedName~SpecCheckerWebTests"`. Expected: FAIL.
- [ ] **Step 3: 구현한다** — `SpecChecker`의 `--impact` 흐름에 `FindAsp` 옆 `FindJs`를 더한다. 색인은 같은 `Load`를 한 번만 부르도록 함께 쓴다.
- [ ] **Step 4: 통과를 본다** — 같은 필터 + `FullyQualifiedName~Spec|FullyQualifiedName~Impact`. Expected: PASS.
- [ ] **Step 5: 커밋** — `feat(spec): JS caller impact in spec check --impact`

---

### Task 7: 적용 뒤 확인 — 서명이 바뀐 함수의 남은 호출

**Files:**
- Create: `src/PellyCoder.Core/Web/JsSignatureChanges.cs`
- Modify:
  - `src/PellyCoder.Core/Specs/SpecRunner.cs`: `WarnJsSignatureChanges()`, `WarnMismatches()` 넓히기, `Finish`에서 부르기.
  - `src/PellyCoder.Core/Specs/SpecRunnerFactory.cs`: 색인 다시 읽기 함수 주입.
  - `src/PellyCoder.Core/Specs/ApplySummary.cs`: `jsCallersNotUpdated`.
  - `src/PellyCoder.Core/Reporting/ApplyReport.cs`, `ReportSanitizer.cs`: 「JS 서명 변경 뒤 고치지 않은 호출 N곳」.
- Test: `tests/PellyCoder.Core.Tests/Web/JsSignatureChangesTests.cs` (새), `tests/PellyCoder.Core.Tests/Specs/SpecRunnerWebTests.cs`, `tests/PellyCoder.Core.Tests/Reporting/…`

**Interfaces:**
- Consumes: Task 4의 `JsSignature`, `JsTargetMatch.Find`. Task 5의 색인. Task 6의 맞추기 규칙(`JsImpactFinder`의 이름 맞추기·페이지 선언 제외를 `internal static`으로 공유).
- Produces:
  - `public sealed record JsSignatureChange(JsSignature Before, JsSignature After) { bool Renamed; bool CountChanged; bool OnlyAppended; }`.
    - `OnlyAppended`는 앞 매개변수가 그대로이고 끝에만 더해진 경우다.
  - `public static IReadOnlyList<JsSignatureChange> JsSignatureChanges.ForFunction(string beforeTextLf, string afterTextLf, bool isPage, string name)`: 대상 하나. 0개 또는 1개를 돌려준다.
  - `public static IReadOnlyList<JsSignatureChange> JsSignatureChanges.InLines(string beforeTextLf, string afterTextLf, bool isPage, TextSpanInfo lines)`: 범위가 걸친 조각의 함수 선언을 순서대로 짝지어 비교한다. 개수가 다르면 짝지은 만큼만 본다.
  - `SpecRunner` 생성자에 `Func<WebSiteIndexResult?>? loadSiteIndex = null`(마지막)을 더한다. `SpecRunnerFactory`가 `asp.siteRoot`·인코딩·`web.vendorPaths`로 `WebSiteIndex.Load`를 부르는 함수를 넘긴다.
  - `public sealed record JsCallerNotUpdated(string Function, string Path, int Line, string Change)` → `apply.json`의 `jsCallersNotUpdated`.
- 규칙(설계 §4.5):
  - 이번 apply에서 적용되고 되돌려지지 않은 js 작업(`#이름`이면 `ForFunction`, `#L`이면 `InLines`)과 asp 작업(`InLines`, 범위는 asp 작업의 프로시저 범위 또는 `#L`)을 비교한다.
  - 변경 전 텍스트는 `_firstRuns[id].Original`이고, 변경 후는 지금 파일이다.
  - `Renamed`: 끝난 뒤 색인에서 옛 이름의 호출과 참조(`References`)를 경고한다. 옛 이름 선언이 사이트에 남아 있으면 「(다른 선언이 있음: js/old.js:12)」를 덧붙인다.
  - `CountChanged`이고 이름이 같으면: 인수 개수가 새 매개변수 수와 다른 호출을 경고한다. 참조와 모름은 뺀다. 명세가 덮었는지는 보지 않는다. `OnlyAppended`이면 「 — 기존 호출은 새 인수가 undefined로 동작함」을 덧붙인다.
  - 개수가 같고 이름·순서만 바뀌면: 「fnSearch의 매개변수 순서·이름이 바뀌었습니다. 부르는 곳 N곳을 확인하십시오」와 첫 호출을 보인다. 기록은 10개까지다.
  - 경고 문구는 「fnSearch의 서명이 바뀌었습니다: fnSearch(f) → fnSearch(f, page). 고치지 않은 호출: admin/order/view.asp:88 외 2곳」이다. 색인이 잘렸으면 「 — 사이트 파일이 많아 일부 호출을 보지 못했을 수 있습니다」를 덧붙인다.
  - 어긋남 경고: 서명이 바뀐 js 작업(그룹 밖)이 적용된 뒤 같은 apply의 뒤 작업이 실패하면, 경고 코드 `JsCallerMismatch`, 문구 「T1(js)은 적용됐고 T2는 실패했습니다. 함수와 부르는 곳이 어긋나 있을 수 있습니다. 함께 되돌리려면 [G?]로 묶으십시오」(`NextFreeGroup`)를 낸다.
  - 종료 코드는 바꾸지 않는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `JsSignatureChangesTests`:
    - `Function_Renamed`.
    - `Function_ParamAppended_OnlyAppended`.
    - `Function_Reordered_SameCount`.
    - `Function_Unchanged_Empty`.
    - `Lines_PairsDeclarationsInOrder`.
    - `Page_BlockFunction`.
  - `SpecRunnerWebTests`(가짜 실행기가 파일을 실제로 바꿈, MockFileSystem 사이트):
    - `AfterApply_Rename_OldNameCallLeft_Warns`.
    - `AfterApply_Rename_AllCallersFixed_NoWarning`.
    - `AfterApply_Rename_OtherDeclarationNote`.
    - `AfterApply_ParamAdded_MismatchedCallsWarned_MatchingExcluded_AppendNote`.
    - `AfterApply_ParamAdded_CoveredButUnfixedCall_StillWarned`.
    - `AfterApply_Reordered_CheckNote`.
    - `AfterApply_AspTaskRenamedBlockFunction_Warns`.
    - `AfterApply_RestoredTask_NoWarning`.
    - `AfterApply_ResumeDoneTask_NotCompared`.
    - `AfterApply_ExitCodeUnchanged`.
    - `ApplyJson_JsCallersNotUpdated`.
    - `Mismatch_JsAppliedLaterFailed_Warns`.
    - `Mismatch_JsBodyOnly_NoWarning`.
  - 보고서: `Report_CountsJsCallersNotUpdated`.
- [ ] **Step 2: 실패를 본다** — Run: `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~JsSignatureChanges|FullyQualifiedName~SpecRunnerWebTests|FullyQualifiedName~Report"`. Expected: FAIL.
- [ ] **Step 3: 구현한다** — 위 규칙대로 하되, `WarnParameterChanges`와 같은 자리(`Finish`)에서 부른다.
- [ ] **Step 4: 통과를 본다** — 같은 필터 + `FullyQualifiedName~Spec`. Expected: PASS.
- [ ] **Step 5: 커밋** — `feat(apply): warn of JS callers left after a signature change`

---

### Task 8: 뒤 작업에 바뀐 서명 전달과 옛 이름 대상 따라가기

**Files:**
- Modify:
  - `src/PellyCoder.Core/Specs/ITaskExecutor.cs`: `TaskRunOptions.ChangedJsFunctions`.
  - `src/PellyCoder.Core/Specs/FsmTaskExecutor.cs`.
  - `src/PellyCoder.Core/Fsm/RunRequest.cs`: `ChangedJsFunctions`.
  - `src/PellyCoder.Core/Specs/SpecRunner.cs`: `WithChangedProcedures` 옆 `WithChangedJsFunctions`, 대상 이름 따라가기.
  - `src/PellyCoder.Core/Fsm/Phases/WebSlicePhase.cs`, `src/PellyCoder.Core/Fsm/Phases/AspSlicePhase.cs`: READ-ONLY 줄.
- Test: `tests/PellyCoder.Core.Tests/Specs/SpecRunnerWebTests.cs`, `tests/PellyCoder.Core.Tests/Fsm/WebSlicePhaseTests.cs`(또는 기존 슬라이스 테스트 파일), `tests/PellyCoder.Core.Tests/Fsm/AspSlicePhaseTests.cs`

**Interfaces:**
- Consumes: Task 7의 `JsSignatureChange`, `JsSignatureChanges.ForFunction/InLines`.
- Produces:
  - `public sealed record ChangedJsFunction(string TaskId, string RelativePath, JsSignature Before, JsSignature After)` (`RunRequest.cs`의 `ChangedProcedure` 옆).
  - `TaskRunOptions`에 `IReadOnlyList<ChangedJsFunction>? ChangedJsFunctions = null`, `RunRequest`에 같은 이름의 선택 매개변수(마지막)를 더한다.
  - READ-ONLY 줄 문구: `T1이 바꾼 함수: fnSearch(f, page) (원래 fnSearch(f))`.
    - js 작업은 「대상이 부르는 함수 선언」 부분 앞에 둔다.
    - asp 작업은 대상 범위 텍스트에 `Before.Name` 또는 `After.Name`이 낱말로 있을 때만 둔다.
  - 따라가기:
    - 이번 apply에서 적용된 js 작업이 같은 파일의 `fnSearch`를 `fnFind`로 바꿨고, 뒤 js 작업의 `Symbol`이 `fnSearch`이면, 실행 요청의 `MethodName`을 `fnFind`로 바꾼다.
    - 결과에 남기는 메시지(`owner.Ui.Info`, `apply.json` 작업 항목 `note`)는 「T5의 대상 fnSearch는 T1이 바꾼 fnFind로 찾음」이다.
    - 그룹 재실행의 `MemberOverride`와 겹치면 `MemberOverride`가 이긴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `Apply_LaterJsTask_GetsChangedSignature`: 가짜 실행기가 받은 `RunRequest.ChangedJsFunctions`에 T1 항목이 있다.
  - `Apply_LaterAspTask_GetsChangedSignature`.
  - `Apply_RenamedTarget_Followed_NoteRecorded`.
  - `Apply_RestoredJsTask_NotPassed`.
  - 슬라이스 테스트:
    - `WebSlice_ChangedFunctionLine_BeforeCalledDeclarations`.
    - `AspSlice_ChangedFunctionLine_OnlyWhenNameInRange`.
    - `Standalone_NoChangedLine`.
- [ ] **Step 2: 실패를 본다** — Run: `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~SpecRunnerWebTests|FullyQualifiedName~SlicePhase"`. Expected: FAIL.
- [ ] **Step 3: 구현한다** — A2의 `WithChangedProcedures`와 같은 자리에 `WithChangedJsFunctions`를 더한다. 바뀐 서명은 Task 7의 비교를 작업이 끝날 때마다 한 번 해서 목록에 쌓고, 적용 뒤 확인이 다시 쓴다(두 번 계산하지 않는다).
- [ ] **Step 4: 통과를 본다** — 같은 필터 + `FullyQualifiedName~Fsm|FullyQualifiedName~Spec`. Expected: PASS.
- [ ] **Step 5: 커밋** — `feat(apply): pass changed JS signatures to later tasks and follow renamed targets`

---

### Task 9: 단독 `pelly asp`의 `JsSignature` 경고 행

**Files:**
- Create: `src/PellyCoder.Core/Verification/AspJsSignatureStep.cs`
- Modify:
  - `src/PellyCoder.Core/Fsm/FsmOrchestratorFactory.cs`: asp 단계 목록의 `AspElementsStep` 뒤.
  - `src/PellyCoder.Core/Fsm/Phases/ReviewPhase.cs`: asp 판정 표 표시 이름 「JS 서명」.
- Test: `tests/PellyCoder.Core.Tests/Verification/AspJsSignatureStepTests.cs` (새)

**Interfaces:**
- Consumes: Task 7의 `JsSignatureChanges.InLines`, Task 5·6의 색인과 맞추기 규칙(호출 수 세기), `AspRunState.SiteIndex`(요소 검사와 같은 Lazy).
- Produces:
  - `Row = "JsSignature"`.
  - 판정:
    - 편집 범위가 걸친 script 블록이 없으면 행을 만들지 않는다.
    - 서명이 그대로이면 `Passed`다.
    - 바뀌면 `Warned`이고, 상세는 「함수 서명 변경: fnSearch(f) → fnFind(f), 부르는 곳 N곳」이다. 여럿이면 `; `로 잇는다.
    - 색인을 읽지 못하면 `Skipped`이고 상세는 「사이트 색인을 읽지 못함」이다.
  - 늘 `StepResult.Pass`다.

- [ ] **Step 1: 실패하는 테스트를 쓴다** — 기존 `AspElementsStepTests`의 `Edit` 도우미 틀로 쓴다.
  - `NoScriptBlock_NoRow`.
  - `BodyOnly_Passed`.
  - `Renamed_Warned_WithCallerCount`.
  - `IndexUnreadable_Skipped`.
  - `SharesSiteIndexLazy`.
- [ ] **Step 2: 실패를 본다** — Run: `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~AspJsSignatureStep"`. Expected: FAIL.
- [ ] **Step 3: 구현한다.**
- [ ] **Step 4: 통과를 본다** — `FullyQualifiedName~Verification|FullyQualifiedName~Asp`. Expected: PASS. 기존 asp e2e의 검토 줄 기대값이 바뀌면, 「JS 서명」이 행 없이 빠지는 경우(블록 없음)만 그대로이고 나머지는 새 행을 반영한다.
- [ ] **Step 5: 커밋** — `feat(asp): warn when an edit changes a page script function signature`

---

### Task 10: 문서·코퍼스·벤치마크·전체 게이트

**Files:**
- Modify:
  - `README.md`: 명세 절에 js·css 작업 예와 대상 문법, §3.2, 서명 변경·영향·적용 뒤 확인. 「JS 영향은 apply가 확인을 묻지 않음」 한 줄. `pelly js --signature`. `asp.siteRoot`가 js·css 작업에도 쓰인다는 것.
  - `deploy/usage.md`.
  - `docs/PRD.md`: 변경 이력 v1.28, §1.4 범위 표의 「이름·선택자 변경과 부르는 곳 함께 고치기(W3)」 칸을 「JS 함수 서명 변경은 명세(W3a), 선택자 변경은 하지 않음」으로. §12 V2-7 W3a 단락.
  - `docs/superpowers/specs/2026-11-04-pelly-js-css-design.md` 목표 8에 「W3a 구현됨」 표시.
  - `deploy/field-results.md` 17절: 설계 §10 현장 항목.
  - `eval/corpus/Asp/`: 같은 JS 함수를 부르는 `.js`·페이지 블록·`onclick` 세 곳.
  - `eval/benchmark/web-w3/` (새, `eval/benchmark/asp-a2/` 틀): `w3-001`(js 서명 변경 + 부르는 js·asp 작업 그룹), `w3-002`(매개변수 추가, 부르는 곳 하나를 명세에서 뺌 → 적용 뒤 경고 1), `w3-003`(css 작업 하나). 각각 `.md` 명세와 `.yaml` 기대값, `run.sh`. 첫 줄에서 빌드한다(기존 run.sh와 같은 블록).
  - `docs/web-premeasure.md`: 「W3a」 절(측정 결과를 적을 자리와 실행 방법).
- Test: 전체.

- [ ] **Step 1: 문서·코퍼스·벤치마크 파일을 쓴다.** README 예는 §3.2에 맞게 쓴다(같은 파일의 `#L`은 첫 작업에만).
- [ ] **Step 2: 벤치마크 기대값을 가짜 모델 없이 검증하는 테스트가 있으면 돌린다**
  - Run: `dotnet test tests/PellyCoder.Eval.Tests`
  - Expected: PASS. `web-w3` 사례 파일이 파서·`spec check`를 오류 없이 통과한다. Eval 테스트에 사례 파일 검사가 없으면, `SpecCheckerWebTests`에 `Benchmark_W3Specs_CheckClean`(사례 `.md`를 코퍼스 위에서 `spec check`)을 더한다.
- [ ] **Step 3: 전체 게이트**
  - Run: `dotnet build -warnaserror` → Expected: 종료 0, 경고 0.
  - Run: `dotnet test` → Expected: 실패 0. 건너뜀은 SQL 서버·Ollama·Windows뿐이다.
- [ ] **Step 4: 예비 측정** — Ollama가 응답하면 `eval/benchmark/web-w3/run.sh --repeat 1`을 돌려 `docs/web-premeasure.md`에 적는다. 응답하지 않으면 「측정 대기」로 적고, 사람에게 실행 명령을 남긴다. 기존 `eval/benchmark/web/run.sh`도 한 번 돌려, 새 행이 틀린 경고를 내지 않는지 본다.
- [ ] **Step 5: 커밋** — `docs: W3a in README, usage, PRD; corpus and web-w3 benchmark`
