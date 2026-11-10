# W3b — 폼 필드 이름 짝 검사 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 편집 때문에 「서버 페이지가 읽는 폼 필드 이름」과 「그 페이지로 보내는 폼·ajax의 필드 이름」이 새로 어긋나면, `pelly asp`·`pelly js`의 경고 행과 apply 끝에서 알린다.

**Architecture:**
- 파일마다 「읽음·보냄·JS action 대입」 사실(`FileFormFacts`)을 뽑아 W2 사이트 색인 캐시에 담는다(판 4 → 5).
- 색인을 읽을 때 주소를 풀어 `FormSite`(대상 페이지 → 읽는 키, 보내는 쪽 묶음)를 만든다.
- 어긋남은 편집 전 세계와 편집 후 세계를 각각 계산해 **새로 생긴 것만** 낸다. 편집 전 세계는 색인에서 편집된 파일의 사실만 원본 텍스트로 바꿔 끼워 만든다.

**Tech Stack:** .NET 10, C#, xUnit v3, System.IO.Abstractions(MockFileSystem).

**Spec:** `docs/superpowers/specs/2026-11-10-pelly-web-w3-design.md` §5, 그리고 §6·§8의 W3b 부분. W3a(§3·§4)는 main에 있다(`f9e7659`).

## Global Constraints

- 판단 기준은 「확실히 아는 짝만 경고한다. 모르면 「열림」이나 「주소 모름」으로 두고 경고하지 않는다」(설계 §5.1).
- 경고만 하고 막지 않는다. 검증 단계는 늘 통과하고, 종료 코드는 바꾸지 않는다(§5.3).
- 편집 전부터 있던 어긋남은 다시 경고하지 않는다(§2 실패 조건).
- 표시 이름은 「폼 필드」다. `pelly asp` 행 키는 `FormFields`, `pelly js` 행 키는 `Fields`다(§5.3).
- 색인을 읽지 못하면 「사이트 색인을 읽지 못함」으로 건너뛴다(`AspElementsStep.IndexUnreadable`과 같은 문구).
- 사이트 색인 Lazy는 `AspRunState.SiteIndex`·`WebRunState.SiteIndex`를 함께 쓴다(새로 읽지 않음).
- 게이트: `dotnet build -warnaserror` 종료 0, `dotnet test` 실패 0(`-nologo` 없이). 건너뜀은 SQL 서버·Ollama·Windows뿐이다.
- 빌드·테스트는 워크트리 `/Users/payletter/git-root/Pelly-Code-w3b`에서만 돌린다.
- 새 코퍼스 파일은 CRLF로 쓴다. 코퍼스 목록 테스트 두 곳(`WebIntegrationTests.Corpus_pages_have_expected_client_blocks`, `AspIntegrationTests.Corpus_AllFiles_CompileUnderCscript`의 페이지 수 16)을 함께 고친다. W3a에서 Windows CI가 이것으로 실패했다.
- 커밋 끝: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. 비밀번호·내부 주소·로컬 임시 경로는 커밋하지 않는다.

## Review Focus

1. 공용 `.inc` 도우미가 `Request(name)`처럼 키를 변수로 읽으면, 그 `.inc`를 include하는 페이지는 모두 「모두 읽음」이 되어 경고가 사라진다. 이것은 의도한 보수 쪽 동작이다. 대신 그 사실이 테스트로 고정돼야 한다. → Task 3 `Reader_IncludeWithVariableKey_AllRead`.
2. `<form>` 태그가 VBScript 문자열 안에 있는 경우(`Response.Write "<form action=""save.asp"">"`): `""` 따옴표로도 폼을 찾아야 한다. → Task 1 `Form_InVbString`.
3. 같은 페이지에 폼이 둘이고 같은 저장 페이지로 보내는 경우: 한 폼만 키를 빼면, 다른 폼이 그 키를 보내더라도 묶음 기준으로 경고하지 않는다. → Task 3 `TwoFormsSameTarget_OneDropsKey_NoWarning`.
4. action이 없고 JS 대입도 없는 폼은 자기 자신에게 보낸다. 이때 그 페이지가 읽는 키와 짝지어져야 한다. → Task 3 `NoAction_PostsToSelf`.
5. 편집된 파일이 `.inc`이면, 읽는 키는 그 `.inc`를 include하는 페이지의 것으로 세고, 폼 주소는 include하는 페이지 폴더 기준으로 푼다. → Task 3 `IncForm_ResolvedFromIncluderFolder`.

---

## File Structure

- `src/PellyCoder.Core/Web/FormFacts.cs` (새): 파일 하나의 사실 뽑기(Task 1).
- `src/PellyCoder.Core/Web/WebSiteIndex.cs`: 캐시 판 5, `FormFacts` 담기(Task 2).
- `src/PellyCoder.Core/Web/FormSite.cs` (새): 주소 풀기와 어긋남 계산(Task 3).
- `src/PellyCoder.Core/Verification/AspFormFieldsStep.cs`·`JsFormFieldsStep.cs` (새), `Fsm/FsmOrchestratorFactory.cs`, `Fsm/Phases/ReviewPhase.cs`(Task 4).
- `src/PellyCoder.Core/Fsm/RunRequest.cs`(`RunResult.FormFieldWarnings`), `Fsm/FsmOrchestrator.cs`, `Specs/SpecRunner.cs`, `Specs/ApplySummary.cs`, `Reporting/*`, `PellyCoder.Cli/Commands/ReportCommand.cs`(Task 5).
- 문서·코퍼스·벤치마크(Task 6).

---

### Task 1: 파일 하나의 폼 사실 뽑기

**Files:**
- Create: `src/PellyCoder.Core/Web/FormFacts.cs`
- Test: `tests/PellyCoder.Core.Tests/Web/FormFactsTests.cs`

**Interfaces:**
- Produces:
  - `public enum FormReadSource { Form, Any, QueryString }`
  - `public sealed record FormRead(string Key, FormReadSource Source, int Line)`
  - `public enum FormSenderKind { Form, Ajax }`
  - `public sealed record FormSender(FormSenderKind Kind, string? Url, bool UrlUnknown, bool Post, IReadOnlyList<string>? Fields, int Line, string? FormName = null, string? FormId = null)`.
    - `Url`가 null이고 `UrlUnknown`이 false이면 action이 없는 폼이다(자기 자신 또는 JS 대입으로 정함).
    - `Fields`가 null이면 「열림」이다.
    - 필드 이름이 `x*`이면 접두다.
    - URL에 쿼리(`?a=1&b=2`)가 있으면 그 키도 필드로 넣는다. 접두사 `?`로 표시해 `"?a"`로 둔다(GET처럼 셈).
  - `public sealed record FormAction(string FormRef, string? Url, int Line)`. `FormRef`는 폼 이름이나 id이고, `Url`이 null이면 「주소 모름」이다.
  - `public sealed record FileFormFacts(IReadOnlyList<FormRead> Reads, bool AllRead, IReadOnlyList<FormSender> Senders, IReadOnlyList<FormAction> Actions)` with `static FileFormFacts Empty`.
  - `public static FileFormFacts FormFacts.OfPage(AspPage page, string textLf)`: 읽음(서버 코드), 폼(전체 텍스트, VB `""` 포함), ajax와 action 대입(클라이언트 JS 블록).
  - `public static FileFormFacts FormFacts.OfScript(string textLf)`: `.js` 파일의 ajax와 action 대입.
- 규칙(설계 §5.1을 이 코드로 옮김):
  - **읽음:** 대상은 서버 코드 구간(`AspSegmentKind.Code`·`Output`·`ServerScript`)이다. VBScript 주석(문자열 밖 `'`, `Rem`)은 지운다.
    - 패턴: `\bRequest\s*(?:\.\s*(?<c>Form|QueryString)\s*)?\(\s*"(?<k>(?:[^"]|"")+)"\s*\)`, 대소문자 무시.
    - 「모두 읽음」: `Request.Form`을 괄호 없이 컬렉션으로 씀(`For Each … In Request.Form`, `Request.Form.Count`), 또는 `Request(`·`Request.Form(` 뒤가 문자열 리터럴이 아님.
  - **폼:** `<form …>` … `</form>`(대소문자 무시, 태그 안 값은 `""`·`"`·`'`·따옴표 없음 모두).
    - `action`·`method`(없으면 GET)·`name`·`id`.
    - action 값에 `<%`가 있으면 `UrlUnknown`.
    - 필드는 범위 안 `<input|select|textarea|button … name=…>`의 이름이다. 값에 `<%`가 있으면 그 앞부분 + `*`.
    - 범위 안에 다음이 있으면 `Fields = null`(열림): `name=` 바로 뒤가 VB 이어 붙이기(`name=""" &`·`name=" &`), `<!--#include`, 서버 코드 구간 안의 `Call 이름` 또는 그 페이지 `Procedures`에 있는 이름을 부르는 문장.
    - 페이지 JS에서 같은 줄에 폼 이름·id(`frm`, `#frm`, `"frm"`)와 `append(`·`appendChild(`·`.html(`·`.innerHTML`이 함께 있으면, 그 폼도 열림이다.
  - **ajax:** `$.ajax(` / `jQuery.ajax(`의 객체 리터럴에서 `url`·`type`|`method`·`data`를 읽는다. `$.post(url, data)`(POST), `$.get(url, data)`(GET)도 본다.
    - `data`가 객체 리터럴이고 키가 식별자·문자열이면 필드다. 그 밖(변수, `serialize()`, `new FormData`)이면 열림이다.
    - `data`가 없으면 필드 0개(닫힘)다.
    - `url`이 문자열 리터럴(+ 쿼리 이어 붙이기)이 아니면 `UrlUnknown`이다.
  - **action 대입:** `(?:document\s*\.\s*)?(?<f>[A-Za-z_$][\w$]*)\s*\.\s*action\s*=`, `document.forms\[["'](?<f>…)["']\]\.action\s*=`, `document.getElementById\(["'](?<f>…)["']\)\.action\s*=`, `\$\(\s*["']#(?<f>…)["']\s*\)\s*\.\s*attr\(\s*["']action["']\s*,`.
    - 값이 문자열 리터럴이면 그 주소다. 리터럴에 `?`가 있고 뒤에 `+ …`가 이어지면 쿼리를 떼고 쓴다. 리터럴에 `?`가 없고 `+ …`가 이어지거나, 변수이면 `Url = null`(주소 모름)이다.
  - 줄 번호는 페이지 줄이다(페이지 블록 뷰는 줄을 지킨다).

- [ ] **Step 1: 실패하는 테스트를 쓴다** (`FormFactsTests`)
  - `Reads_FormAnyQueryString`: `Request.Form("a")`·`Request("b")`·`Request.QueryString("c")` → 세 읽음, 출처와 줄. 주석 안 `' Request.Form("z")`는 없다.
  - `Reads_AllRead_ForEachAndVariableKey`: `For Each k In Request.Form` → `AllRead`. 다른 파일의 `Request(name)` → `AllRead`. `Request.QueryString(name)`만 있으면 `AllRead`가 아니다.
  - `Form_ActionMethodNameFields`: `<form name="frm" action="save.asp" method="post"><input name="custId"><select name="status"></select><textarea name="memo"></textarea></form>` → Url `save.asp`, Post, Fields `[custId, status, memo]`, FormName `frm`.
  - `Form_NoAction_UrlNullNotUnknown`, `Form_DefaultMethodIsGet`.
  - `Form_ServerValueName_Prefix`: `name="row<%= i %>"` → `row*`.
  - `Form_InVbString`: `<% Response.Write "<form action=""save.asp"" method=""post""><input name=""k"">" %>` … `</form>` → Fields `[k]`.
  - `Form_FieldsInIfBranches_Union`.
  - `Form_Open_ConcatName_Include_ProcedureCall`: 세 경우 각각 `Fields == null`.
  - `Form_Open_JsAddsFields`: 같은 페이지 블록에 `$("#frm").append(...)` → 열림.
  - `Form_ActionWithServerCode_UrlUnknown`.
  - `Form_UrlQueryKeys`: `action="save.asp?mode=del"` → Fields에 `?mode`가 있다.
  - `Ajax_ObjectData_Post`: `$.ajax({ url: "/admin/x.asp", type: "POST", data: { a: 1, "b": 2 } })` → Kind Ajax, Post, Fields `[a, b]`.
  - `Ajax_Serialize_Open`, `Ajax_VariableData_Open`, `Ajax_PostShorthand`, `Ajax_GetShorthand_NoData_ZeroFields`, `Ajax_VariableUrl_Unknown`.
  - `Actions_FourShapes`: `frm.action = "save.asp";`, `document.frm.action = "a.asp?m=" + m;`(→ `a.asp`), `$("#frm").attr("action", "b.asp")`, `document.getElementById("frm").action = url;`(→ Url null).
  - `OfScript_AjaxInJsFile`.
- [ ] **Step 2: 실패를 본다** — Run: `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~FormFactsTests"`. Expected: 컴파일 실패(형식 없음).
- [ ] **Step 3: 구현한다** — 정규식은 `JsTokenScan.Mask`로 주석·문자열 내용을 가린 JS에서 찾는다. 다만 리터럴 값은 원문에서 읽는다(`PageElements.Literal`과 같은 방식). 폼 찾기는 전체 페이지 텍스트에서 한다(요소 검사의 `Attribute` 정규식처럼 `""` 지원).
- [ ] **Step 4: 통과를 본다** — 같은 필터. Expected: PASS.
- [ ] **Step 5: 커밋** — `feat(web): read form fields, Request reads and ajax senders of one file`

---

### Task 2: 색인에 폼 사실 담기(캐시 판 5)

**Files:**
- Modify: `src/PellyCoder.Core/Web/WebSiteIndex.cs`
- Test: `tests/PellyCoder.Core.Tests/Web/WebSiteIndexTests.cs`

**Interfaces:**
- Consumes: Task 1.
- Produces: `WebSiteIndexResult.FormFacts`: `IReadOnlyDictionary<string, FileFormFacts>`. 키는 사이트 상대 경로이고, 사실이 비어 있는 파일은 넣지 않는다. 캐시 항목은 `CacheItem.Forms`(JSON 형식은 빌더가 정함)이고 `CacheVersion = 5`다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `Index_HasFormFacts_PageAndJs`.
  - `Cache_Version4_IsRebuilt`: 판 4 캐시를 두면 폼 사실이 채워지고 판 5로 쓴다.
  - `Cache_Reused_KeepsFormFacts`.
  - 기존 테스트의 `"version":4` 문자열은 5로 고친다. 판 3·4 다시 만들기 테스트는 그대로 둔다.
- [ ] **Step 2: 실패를 본다** — Run: `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~WebSiteIndex"`. Expected: FAIL.
- [ ] **Step 3: 구현한다** — `ExtractPage`는 `FormFacts.OfPage`를, `.js` 갈래는 `FormFacts.OfScript`를 쓴다. `Complete()`는 새 목록이 null이면 다시 만든다.
- [ ] **Step 4: 통과를 본다** — `FullyQualifiedName~Web`. Expected: PASS.
- [ ] **Step 5: 커밋** — `feat(web): keep form facts in the site index`

---

### Task 3: 주소 풀기와 새 어긋남 계산

**Files:**
- Create: `src/PellyCoder.Core/Web/FormSite.cs`
- Test: `tests/PellyCoder.Core.Tests/Web/FormSiteTests.cs`

**Interfaces:**
- Consumes: Task 1·2. `WebSiteIndexResult.Includes`, `IncludersOf`, `ScriptSources`(W3a), `JsImpactFinder.Files`(파일 목록), `PagePaths`.
- Produces:
  - `public sealed class FormSite`:
    - `public static FormSite Build(WebSiteIndexResult index, IReadOnlyDictionary<string, FileFormFacts>? overrides = null)`: `overrides`의 파일은 색인 대신 그 사실을 쓴다(값이 `Empty`면 그 파일에 폼이 없는 것으로 본다).
    - `public IReadOnlyList<(string Key, FormReadSource Source, string File, int Line)> ReadsOf(string page)`: 그 페이지와 include들의 읽음이다.
    - `public bool AllRead(string page)`.
    - `public IReadOnlyList<FormSenderGroup> SendersTo(string page)`.
  - `public sealed record FormSenderGroup(string File, string Target, int Line, FormSenderKind Kind, IReadOnlyList<string>? Fields, bool AnyPost, bool AnyGet)`: (보내는 파일, 대상 페이지)마다 하나다.
    - `Fields`는 그 파일 안에서 그 대상으로 보내는 닫힌 보내는 쪽의 필드 합집합이다. 하나라도 열림이면 null이다.
    - `Line`은 첫 보내는 쪽의 줄, `Kind`는 첫 보내는 쪽의 종류다.
  - `public enum FormMismatchKind { NotSent, NobodySends }`
  - `public sealed record FormMismatch(FormMismatchKind Kind, string Target, string Key, string ReadFile, int ReadLine, string? SenderFile, int SenderLine, FormSenderKind SenderKind, string? Similar, int Senders)` with `string Message`.
  - `public static IReadOnlyList<FormMismatch> FormSite.NewMismatches(FormSite before, FormSite after, IReadOnlyCollection<string> edited)`.
- 주소 풀기(설계 §5.1):
  - 폼의 대상: `Url`이 있으면 그것, `UrlUnknown`이면 짝짓지 않는다. 없으면 그 폼의 이름·id에 대한 action 대입을 찾는다. 찾는 곳은 그 페이지의 사실, 그 페이지와 include들이 `<script src>`로 부르는 `.js`의 사실이고, 주소 하나마다 대상 하나다. 대입이 하나라도 「주소 모름」이면 그 폼은 짝짓지 않는다. 대입도 없으면 자기 자신이다.
  - 기준 폴더: 페이지면 그 폴더, `.inc`면 그것을 include하는 각 페이지의 폴더(없으면 자기 폴더), `.js`(ajax)면 그것을 부르는 각 페이지의 폴더다. 부르는 페이지는 `ScriptSources`에 그 `.js`가 있는 페이지와, 그 페이지가 `.inc`일 때 그 includer들이다.
  - `/`로 시작하면 사이트 루트 기준이다. 쿼리·`#`은 뗀다. 대소문자는 무시한다. `http:`·`https:`·`//`·`<%`는 짝짓지 않는다. 색인에 없는 파일도 짝짓지 않는다.
  - 대상이 `.inc`이면 짝짓지 않는다(브라우저는 `.inc`를 부르지 않음).
- 어긋남 계산(설계 §5.2, 한 세계에서):
  - **NotSent:** 묶음 G(파일 F → 대상 P)의 `Fields`가 null이 아니고, P가 `AllRead`가 아니며, P가 키 k를 Form 또는 Any로 읽는데 G가 k를 보내지 않는다(접두 `x*`는 앞부분으로 맞춤, `?k`는 Any 읽음에만 맞춤).
  - **NobodySends:** P로 보내는 묶음이 하나 이상 있고, 모두 `Fields`가 null이 아니며, P가 `AllRead`가 아니고, P가 k를 Form 또는 Any로 읽는데 어느 묶음도 k를 보내지 않는다.
  - Form 읽음은 POST 보내는 쪽의 필드에만 맞는다. Any 읽음은 아무 쪽의 필드와 `?k`에 맞는다.
  - `NewMismatches`: `after`에서 계산한 것 중 `before`에 같은 (Kind, Target, Key, SenderFile)이 없는 것만 낸다. 보는 범위는 `edited`에 닿는 것뿐이다. 즉 SenderFile ∈ edited, 또는 ReadFile(읽는 페이지나 그 include) ∈ edited, 또는 Target ∈ edited. 같은 (Target, Key)에 NotSent가 있으면 NobodySends는 내지 않는다.
  - 「비슷한 이름」(NotSent만): G의 필드 중 k와 대소문자만 다른 것, 아니면 G가 보내고 P가 읽지 않는 이름이 정확히 하나일 때 그 이름이다.
- 문구(설계 §5.2의 예를 따름):
  - NotSent: `admin/order/save.asp가 읽는 custNo(줄 14)를 admin/order/list.asp의 폼(줄 40)이 보내지 않음, 보내는 비슷한 이름: custId`. ajax는 「의 ajax(줄 n)」이고, 비슷한 이름이 없으면 뒤 부분을 뺀다.
  - NobodySends: `admin/order/save.asp가 읽는 custNo(줄 14)를 보내는 폼·ajax가 없음(보내는 곳 2곳)`.
  - 읽음이 include에 있으면 「(inc/x.inc 줄 3)」으로 파일을 밝힌다.

- [ ] **Step 1: 실패하는 테스트를 쓴다** (`FormSiteTests`. MockFileSystem 사이트를 `WebSiteIndex.Load`로 읽은 뒤 `Build(index, overrides)`)
  - `FormRenamesField_NotSent_WithSimilarName`: list.asp 폼이 `custNo` → `custId`, save.asp가 `Request.Form("custNo")` → 문구 그대로.
  - `ReaderAddsKey_NobodySends`.
  - `ReaderAddsKey_SomeSenderSends_NoWarning`.
  - `ExistingMismatch_NotReported`.
  - `TwoFormsSameTarget_OneDropsKey_NoWarning`.
  - `OpenSender_NoWarning`(serialize).
  - `AllRead_NoWarning`.
  - `Reader_IncludeWithVariableKey_AllRead`: 저장 페이지가 `Request(name)`을 쓰는 `inc/func.inc`를 include하면 경고가 없다.
  - `NoSenders_NoWarning`.
  - `NoAction_PostsToSelf`.
  - `JsActionAssignment_Resolved`: `frm.action = "save.asp"`, `.js`에서 대입한 경우도.
  - `JsActionUnknown_NotPaired`.
  - `IncForm_ResolvedFromIncluderFolder`.
  - `JsAjax_ResolvedFromLoadingPageFolder`: 상대 주소를 페이지 폴더 기준으로 푼다.
  - `QueryKeyInUrl_SatisfiesAnyRead_NotFormRead`.
  - `GetForm_FormRead_NobodySends`: GET 폼의 필드는 `Request.Form` 읽음에 맞지 않는다.
  - `External_ServerCode_IncTarget_MissingFile_NotPaired`.
  - `Scope_UneditedPairsIgnored`.
- [ ] **Step 2: 실패를 본다** — Run: `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~FormSiteTests"`. Expected: 컴파일 실패.
- [ ] **Step 3: 구현한다.**
- [ ] **Step 4: 통과를 본다** — 같은 필터. Expected: PASS.
- [ ] **Step 5: 커밋** — `feat(web): pair form senders with the pages that read them and find new mismatches`

---

### Task 4: 검증 행 `FormFields`(asp)·`Fields`(js)

**Files:**
- Create: `src/PellyCoder.Core/Verification/AspFormFieldsStep.cs`, `src/PellyCoder.Core/Verification/JsFormFieldsStep.cs`
- Modify: `src/PellyCoder.Core/Fsm/FsmOrchestratorFactory.cs`(asp는 `AspJsSignatureStep` 뒤, js는 `JsElementsStep` 뒤), `src/PellyCoder.Core/Fsm/Phases/ReviewPhase.cs`(`AspCheckLabels`에 `("FormFields", "폼 필드")`, `WebCheckLabels`에 `("Fields", "폼 필드")`를 「요소」 뒤에), `src/PellyCoder.Core/Asp/AspRunState.cs`·`Web/WebRunState.cs`(`int FormFieldWarnings { get; set; }`)
- Test: `tests/PellyCoder.Core.Tests/Verification/AspFormFieldsStepTests.cs`, `JsFormFieldsStepTests.cs`, `tests/PellyCoder.Core.Tests/Fsm/WebOrchestratorTests.cs`(검토 줄)

**Interfaces:**
- Consumes: Task 3.
- Produces:
  - `AspFormFieldsStep(IFileSystem fs, PellyOptions options, ITextFileCodec codec)`, `Row = "FormFields"`.
  - `JsFormFieldsStep(IFileSystem fs, ITextFileCodec codec)`, `Row = "Fields"`.
  - 둘 다 늘 `StepResult.Pass`이고, 결과를 `state.FormFieldWarnings`에 쓴다.
- 동작:
  - 편집된 파일의 상대 경로 rel을 구한다. `before = FormSite.Build(index, {rel: 원본 사실})`, `after = FormSite.Build(index, {rel: 현재 사실})`이다. 현재 사실은 `ctx.Current.Text`로 다시 뽑는다. 색인이 이미 디스크의 새 파일을 읽었을 수 있으므로 양쪽 모두 바꿔 끼운다.
  - `NewMismatches(before, after, [rel])`.
  - 행 만들기: 원본·현재 사실이 모두 비어 있고(읽음·보냄·대입 없음), rel이 어떤 보내는 쪽의 대상도 아니면 행을 만들지 않는다.
  - 경고가 없으면 `Passed`, 있으면 `Warned`이고 상세는 문구들을 `; `로 잇는다.
  - 색인을 읽지 못하면 `Skipped` 「사이트 색인을 읽지 못함」.
  - `.js` 파일이면 `FormFacts.OfScript`, 페이지면 `OfPage`로 뽑는다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - asp(`AspElementsStepTests`의 `Edit` 틀):
    - `NoForms_NoRow`.
    - `RenameFieldInForm_Warned`.
    - `ReaderAddsUnsentKey_Warned`.
    - `UnrelatedEdit_Passed`.
    - `IndexUnreadable_Skipped`.
    - `SharesSiteIndexLazy`.
  - js:
    - `JsFile_AjaxKeyRenamed_Warned`.
    - `JsFile_NoAjax_NoRow`.
    - `Page_ActionAssignmentChanged_Warned`: 대입이 다른 페이지로 바뀌어 그 페이지가 읽는 키가 빠지는 경우다.
  - 검토 줄: `Js_FormFieldWarning_ShownInReviewLine`이 「폼 필드 경고」를 담는다(기존 줄 문구 형식을 따름).
- [ ] **Step 2: 실패를 본다** — Run: `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~FormFieldsStep|FullyQualifiedName~WebOrchestratorTests"`. Expected: FAIL.
- [ ] **Step 3: 구현하고 등록한다.** 기존 e2e의 검토 줄 기대값이 바뀌면 고친다(행이 생기는 사례만).
- [ ] **Step 4: 통과를 본다** — `FullyQualifiedName~Verification|FullyQualifiedName~Fsm|FullyQualifiedName~Asp`. Expected: PASS.
- [ ] **Step 5: 커밋** — `feat(web): warn when an edit breaks form field pairs`

---

### Task 5: apply 끝 다시 계산과 기록

**Files:**
- Modify:
  - `src/PellyCoder.Core/Fsm/RunRequest.cs`(`RunResult`에 `int FormFieldWarnings = 0`).
  - `src/PellyCoder.Core/Fsm/FsmOrchestrator.cs`: `ctx.Asp?.FormFieldWarnings ?? ctx.Web?.FormFieldWarnings ?? 0`.
  - `src/PellyCoder.Core/Specs/SpecRunner.cs`: `WarnFormFields()`, `Finish`에서 `WarnJsSignatureChanges` 뒤.
  - `src/PellyCoder.Core/Specs/ApplySummary.cs`: `FormFieldMismatches`.
  - `src/PellyCoder.Core/Reporting/ApplyReport.cs`·`ReportSanitizer.cs`, `src/PellyCoder.Cli/Commands/ReportCommand.cs`.
- Test: `tests/PellyCoder.Core.Tests/Specs/SpecRunnerWebTests.cs`, `tests/PellyCoder.Core.Tests/Reporting/ApplyReportTests.cs`

**Interfaces:**
- Consumes: Task 3, W3a `WebSiteAccess`(`Site.Index()`, `Site.Read`, `Site.SiteRelative`).
- Produces:
  - `public sealed record FormFieldMismatchRecord(string Kind, string Target, string Key, string? Sender, string Message)` → `apply.json`의 `formFieldMismatches`. `Kind`는 `notSent`·`nobodySends`다.
  - `ApplyReportData.FormFieldMismatches`, `ApplyCounts.FormFieldMismatches`.
  - 보고 줄은 「폼 필드 어긋남 N건」이다.
- 동작(설계 §5.3):
  - 이번 apply에서 적용되고 되돌려지지 않은 asp·js 작업의 파일 집합 E를 구한다. `--resume`으로 이미 완료된 작업은 뺀다.
  - `before = Build(index, E의 파일마다 첫 실행 원본(_firstRuns[id].Original)에서 뽑은 사실)`. 같은 파일을 여러 작업이 고쳤으면 가장 먼저 적용된 작업의 원본을 쓴다.
  - `after = Build(index)`. 색인은 끝난 뒤의 디스크다.
  - `NewMismatches(before, after, E)`의 각 문구를 `owner.Ui.Warn`하고 기록한다.
  - 작업들의 `FormFieldWarnings` 합 N(마지막 실행 기준)이 0보다 크고, N이 남은 수보다 크면 「작업 중 폼 필드 경고 N건 중 M건은 뒤 작업이 해결함」(M = N − 남은 수)을 한 줄 보인다.
  - 종료 코드는 바꾸지 않는다. 색인을 읽지 못하면 조용히 건너뛴다.

- [ ] **Step 1: 실패하는 테스트를 쓴다**
  - `AfterApply_FormRenamed_ReaderNotUpdated_Warns`.
  - `AfterApply_FormAndReaderBothUpdated_NoWarning_ResolvedNote`: T1 asp가 save.asp의 키를 바꾸고(가짜 실행기의 결과에 FormFieldWarnings 1), T2가 폼을 고친다. 그러면 끝에 경고가 없고 「작업 중 폼 필드 경고 1건 중 1건은 뒤 작업이 해결함」이 나온다.
  - `AfterApply_RestoredTask_NotCounted`.
  - `ApplyJson_FormFieldMismatches`.
  - `Report_CountsFormFieldMismatches`.
- [ ] **Step 2: 실패를 본다** — Run: `dotnet test tests/PellyCoder.Core.Tests --filter "FullyQualifiedName~SpecRunnerWebTests|FullyQualifiedName~ApplyReportTests"`. Expected: FAIL.
- [ ] **Step 3: 구현한다.**
- [ ] **Step 4: 통과를 본다** — `FullyQualifiedName~Spec|FullyQualifiedName~Report`, 그리고 `tests/PellyCoder.Cli.Tests --filter FullyQualifiedName~Report`. Expected: PASS.
- [ ] **Step 5: 커밋** — `feat(apply): recheck form field pairs after the apply`

---

### Task 6: 문서·코퍼스·벤치마크·전체 게이트

**Files:**
- Modify:
  - `README.md`: `pelly asp` 판정 표 근처의 경고 행 `FormFields`, `pelly js`의 `Fields`, apply의 「폼 필드」 항목, `apply.json`·report.
  - `deploy/usage.md`.
  - `docs/PRD.md`: 변경 이력 v1.29, §12 W2·W3 단락의 「남은 것」에서 폼 필드를 뺌.
  - `docs/superpowers/specs/2026-11-04-pelly-js-css-design.md` 목표 8의 「W3b」 표시.
  - `deploy/field-results.md` §17 W13 메모.
- Corpus (CRLF, ASCII):
  - `eval/corpus/Asp/admin/order/memo.asp`: `<form name="frm" method="post">`와 `orderNo`·`memoText` 입력, 블록 안 `frm.action = "memo_save.asp"; frm.submit();`.
  - `eval/corpus/Asp/admin/order/memo_save.asp`: `Request.Form("orderNo")`·`Request.Form("memoText")`.
  - 목록 테스트 두 곳을 고친다(Global Constraints). `memo_save.asp`·`memo.asp`가 Windows `cscript` 검사를 통과하도록 `Option Explicit`·`Dim`을 맞춘다.
- Benchmark: `eval/benchmark/web-w3/` 실행기에 「검토 출력에 담김」 기대(`outputMustContain`)가 이미 있다. 사례 둘을 더한다.
  - `w3-004`: asp 작업 하나 `admin/order/memo.asp#L<입력란 줄>` 「memoText 입력란 이름을 memo로 바꾼다」. `outputMustContain: ["memo_save.asp가 읽는 memoText"]`.
  - `w3-005`: asp 작업 하나 `admin/order/memo_save.asp#L<읽는 줄>` 「Request.Form("writer")도 읽어 변수 writer에 담는다」. `outputMustContain: ["memo_save.asp가 읽는 writer"]`.
- Measure: `docs/web-premeasure.md`의 「W3b」 절.
- Test: 전체.

- [ ] **Step 1: 문서·코퍼스·사례를 쓴다.** 코퍼스 목록 테스트를 고친다.
- [ ] **Step 2: 전체 게이트**
  - Run: `dotnet build -warnaserror` → Expected: 경고 0.
  - Run: `dotnet test` → Expected: 실패 0.
- [ ] **Step 3: 예비 측정** — Ollama가 응답하면 `eval/benchmark/web-w3/run.sh --repeat 3`을 돌린다(w3-001~005). 틀린 경고를 보려고 `eval/benchmark/web/run.sh --repeat 1`과 `eval/benchmark/asp/run.sh --repeat 1`도 돌려 「폼 필드」 경고 수를 센다. 기대는 0이다. 결과를 `docs/web-premeasure.md`에 적는다.
- [ ] **Step 4: 커밋** — `docs: W3b form field check in README, usage, PRD; corpus and benchmark`
