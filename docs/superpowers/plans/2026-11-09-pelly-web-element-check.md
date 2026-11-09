# pelly 페이지 요소(id·name) 검사 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 편집 때문에 새로 생긴 「페이지에 없는 id·name을 가리키는 JS 참조」를 `pelly js`(페이지)·`pelly asp`의 경고 행으로 알린다.

**Architecture:** 순수 함수 모음 `Web/PageElements.cs`가 페이지 텍스트에서 참조(JS가 찾는 id·name)와 정의(HTML 속성·런타임 생성)를 모으고, 원본·고친 페이지의 끊긴 참조 수를 비교해 경고 문자열을 만든다. include·includer·`<script src>` 텍스트는 `PageScripts`에 새 공개 메서드를 두어 가져온다. 경고 단계 두 개(`JsElementsStep`, `AspElementsStep`)가 이를 불러 행을 기록한다.

**Tech Stack:** .NET 10, C#, xUnit, System.IO.Abstractions(MockFileSystem)

**Spec:** `docs/superpowers/specs/2026-11-09-pelly-web-element-check-design.md`

## Global Constraints

- 경고만 한다. 단계는 항상 `StepResult.Pass`를 돌려주고 재시도를 일으키지 않는다.
- 원본 대비 **새로** 끊긴 (종류, 값)만 경고한다. 단위는 페이지 전체(모든 `<script>` 블록)다.
- id·name 값 비교는 대소문자를 구분한다(Ordinal).
- 프롬프트·설정은 바꾸지 않는다.
- 게이트: `dotnet build -warnaserror` 종료 0, `dotnet test` 실패 0(건너뜀은 SQL 서버·Ollama·Windows 뿐).
- 테스트의 루트 경로는 Windows에서도 통하게 쓴다(W2에서 `/w` 루트가 Windows CI를 깬 적이 있다. 이미 있는 테스트 도우미의 루트 처리를 따른다).
- 커밋 끝 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. 다른 이름의 트레일러를 쓰지 않는다.
- 사용자에게 보이는 문구는 한국어.

## Review Focus

1. 공용 `.inc`를 고칠 때, 입력 칸이 부모 페이지(includer)에만 있으면 경고가 나오면 안 된다 — Task 2 테스트 `RelatedTexts_IncludesIncluderPages`, Task 4 테스트 `IncFile_FieldInIncluder_NoWarning`.
2. 원본에 이미 끊긴 참조가 있는 페이지에서 무관한 수정을 하면 경고가 나오면 안 된다 — Task 1 `Check_ExistingBrokenRef_NotReported`.
3. `opener.document.getElementById("x")`, `$("#x", opener.document)`처럼 다른 문서 참조는 세지 않는다 — Task 1 `References_OtherDocument_Ignored`.
4. VBScript `Response.Write "<input name=""custId"">"`로 만든 입력 칸은 「있음」 — Task 1 `Definitions_VbDoubledQuotes`.
5. `<% If %>`가 섞인 블록(구문 트리 없음) 안의 참조도 센다 — Task 1 `References_ServerStatementsBlock_Counted`.

---

### Task 1: `PageElements` — 참조·정의·판정

**Files:**
- Create: `src/PellyCoder.Core/Web/PageElements.cs`
- Test: `tests/PellyCoder.Core.Tests/Web/PageElementsTests.cs`

**Interfaces:**
- Consumes: `AspPage.Parse`, `AspPage.ClientBlocks`(`AspClientBlockKind.Script`), `PageBlockView.Build(AspPage, string, int)`·`PageBlockView.IsPlaceholder`, `JsTokenScan.Mask(string)`
- Produces:
  ```csharp
  public enum ElementKind { Id, Name }
  public sealed record ElementRef(ElementKind Kind, string Value, int Line);          // Line = 페이지 줄
  public sealed record ElementDef(ElementKind Kind, string Value, string? File, int Line); // File null = 대상 페이지 자신, 아니면 사이트 상대 경로
  public sealed record RelatedText(string File, string TextLf);                       // 사이트 상대 경로, LF 텍스트
  public static class PageElements
  {
      public static IReadOnlyList<ElementRef> References(AspPage page, string pageTextLf);
      public static IReadOnlyList<ElementDef> Definitions(string textLf, string? file);
      public static IReadOnlyList<string> NewBroken(AspPage original, string originalTextLf, AspPage patched, string patchedTextLf,
          IReadOnlyList<RelatedText> related);
  }
  ```

**참조 규칙 (설계 4.1).** 각 `Script` 블록(내용이 있는 것, `src` 블록 제외)의 `PageBlockView` 텍스트를 `JsTokenScan.Mask`로 가린 뒤, 마스크에서 아래 모양을 찾고 문자열 값은 **같은 위치의 보기 텍스트**에서 읽는다.
- `(?<![\w$.])document\s*\.\s*getElementById\s*\(\s*(["'])` … 닫는 같은 따옴표, 그 뒤 `\s*\)` → Id
- 같은 모양 `getElementsByName` → Name
- `(?<![\w$.])(\$|jQuery)\s*\(\s*(["'])` … 닫는 따옴표 뒤 `\s*\)` (쉼표가 오면 문맥 인수 → 제외), 값이 `<`로 시작하면 제외. 값에서 `#([\w-]+)` 각각 → Id, `\[\s*name\s*=\s*["']?([\w-]+)` 각각 → Name
- 값에 `PageBlockView.IsPlaceholder`가 참인 조각(`__aspN__`)이 들어 있으면 그 참조 전체를 버린다.
- `document` 앞이 `.`이면(`opener.document`) 제외 — 위 lookbehind가 처리한다.

**정의 규칙 (설계 4.2).** 텍스트 전체(가리지 않은 원문)에서:
- `(?<![\w-])(id|name)\s*=\s*(?:""|"|')?(?<v>[^"'\s>]+)` — 대소문자 무시로 속성 이름 매칭, `v`가 `<%`를 포함하면 `<%` 앞부분을 접두사로(빈 문자열 가능) 기록한다. 접두사 정의는 `ElementDef.Value`가 `"<prefix>*"`로 끝나는 별 표시로 나타낸다(예 `row*`, 빈 접두사는 `*`).
- `\.\s*(id|name)\s*=\s*(["'])(?<v>[\w-]+)\2` → 해당 종류
- `setAttribute\s*\(\s*["'](id|name)["']\s*,\s*["'](?<v>[\w-]+)["']` → 해당 종류

**판정 (설계 4.3).** 정의 집합 = 대상 페이지(원본 쪽은 원본 텍스트, 고친 쪽은 고친 텍스트) + `related` 전부. 참조가 「끊김」 = 같은 종류에 같은 값(Ordinal) 정의도, 맞는 접두사 정의도 없음. (종류, 값)별로 끊긴 참조 수를 원본·고친 쪽에서 세고, 고친 쪽이 많은 것만 경고. 경고 순서는 고친 페이지에서 그 (종류, 값)이 처음 나오는 줄 순.

**경고 문구 (고정).**
- 참조 표시: Id → `#값`, Name → `[name=값]`. 형식 `{표시}(줄 {n}) — {id|name} 없음`.
- 힌트를 `, `로 이어 붙임(있는 것만, 이 순서): 다른 종류에 같은 값이 있으면 `name="값"는 있음(줄 m)` 또는 `id="값"는 있음(줄 m)`; 같은 종류에 대소문자만 다른 값이 있으면 `대소문자만 다름: 그값(줄 m)`. 정의가 다른 파일이면 `(줄 m)` 대신 `({파일}:{m})`.
- 예: `#custId(줄 116) — id 없음, name="custId"는 있음(줄 98)`

- [ ] **Step 1: 실패하는 테스트를 쓴다** — `PageElementsTests`에 아래 사례. 페이지 문자열은 테스트 안에 직접 쓴다.
  - `References_GetElementById_Jquery_GetElementsByName`: 세 모양이 각각 (Id,"a"), (Id,"b"), (Name,"c")로, 줄 번호가 페이지 줄.
  - `References_JquerySelectorParts`: `$("#f1 input[name=custId]")` → (Id,"f1"), (Name,"custId").
  - `References_OtherDocument_Ignored`: `opener.document.getElementById("x")`, `$("#y", opener.document)`, `$("<div id='z'>")` → 빈 목록.
  - `References_InComment_Ignored`: `// $("#x")`, `/* getElementById("y") */` → 빈 목록.
  - `References_Concat_Or_ServerValue_Ignored`: `$("#" + id)`, `$("#row<%= i %>")` → 빈 목록.
  - `References_ServerStatementsBlock_Counted`: `<% If x Then %>`가 든 블록 안 `$("#a")` → (Id,"a").
  - `Definitions_Attributes_And_Runtime`: `<input id="a" name='b'>`, `el.id = "c"`, `x.setAttribute("name", "d")` → 네 정의(종류·값·줄).
  - `Definitions_VbDoubledQuotes`: `Response.Write "<input name=""custId"">"` → (Name,"custId").
  - `Definitions_ServerValuePrefix`: `id="row<%= i %>"` → (Id,"row*"); `name="<%= n %>"` → (Name,"*").
  - `Check_NewJqueryIdWithOnlyName_Warns`: 원본 `f.custId.focus()`, 고친 쪽 `$("#custId").focus()`, HTML `<input name="custId">`(줄 2) → 결과 한 줄 `#custId(줄 {n}) — id 없음, name="custId"는 있음(줄 2)`.
  - `Check_ExistingBrokenRef_NotReported`: 원본·고친 쪽 모두 `$("#gone")`, 고친 쪽은 다른 줄만 바꿈 → 빈 목록.
  - `Check_IdRemovedFromHtml_Warns` / `Check_IdMoved_NoWarning`: HTML에서 `id="a"`를 지우면 `#a(줄 n) — id 없음`, 다른 줄로 옮기면 빈 목록.
  - `Check_CaseOnly_Hint`: 정의 `id="CustId"`, 참조 `#custId` → `대소문자만 다름: CustId(줄 m)` 포함.
  - `Check_PrefixDefinition_Matches`: 정의 `id="row<%= i %>"`, 새 참조 `getElementById("row3")` → 빈 목록.
  - `Check_RelatedFileDefinition_Matches_And_FileHint`: `related`의 `inc/head.inc`에 `<input id="q">` → 새 `#q` 참조는 빈 목록; `related`에 `name="p"`만 있고 새 `#p` → 힌트 `name="p"는 있음(inc/head.inc:{m})`.
- [ ] **Step 2: 실행해 실패 확인** — `dotnet test tests/PellyCoder.Core.Tests --filter FullyQualifiedName~PageElementsTests` → 컴파일 실패(`PageElements` 없음).
- [ ] **Step 3: `PageElements` 구현** — 위 Interfaces·규칙대로. 정규식은 `static readonly ... RegexOptions.Compiled`.
- [ ] **Step 4: 다시 실행해 통과 확인** — 같은 명령, 모두 통과.
- [ ] **Step 5: 커밋** — `feat(web): page element references, definitions and new broken refs`

### Task 2: `PageScripts.RelatedTexts` — include·includer·`<script src>` 텍스트

**Files:**
- Modify: `src/PellyCoder.Core/Web/PageScripts.cs` (Walker 재사용)
- Test: `tests/PellyCoder.Core.Tests/Web/PageScriptsTests.cs`

**Interfaces:**
- Consumes: `RelatedText`(Task 1)
- Produces:
  ```csharp
  public static IReadOnlyList<RelatedText> RelatedTexts(IFileSystem fs, string siteRoot, string pagePath, AspPage page,
      ITextFileCodec codec, WebSiteIndexResult index);
  ```
  대상 페이지의 include(재귀, 기존 `MaxIncludeDepth`·`MaxIncludeFiles`), 대상을 include하는 페이지(`index.IncludersOf`, 최대 `MaxIncluders`)와 그 include, 이 모든 파일이 `<script src>`로 부르는 사이트 안 `.js`의 텍스트. **대상 파일 자신은 넣지 않는다**(includer를 따라가다 다시 만나도 뺀다). 같은 파일은 한 번만. 읽지 못한 파일은 건너뛴다. 순서는 처음 만난 순.

- [ ] **Step 1: 실패하는 테스트** — `RelatedTexts_IncludesIncludeFiles_And_ScriptSrc`(페이지가 `inc/head.inc`를 include, head가 `<script src="/js/app.js">` → 결과 파일 `inc/head.inc`, `js/app.js`), `RelatedTexts_IncludesIncluderPages`(대상 `inc/form.inc`를 `a.asp`가 include → 결과에 `a.asp`, 대상 `inc/form.inc`는 없음), `RelatedTexts_MissingFile_Skipped`.
- [ ] **Step 2: 실패 확인** — `--filter FullyQualifiedName~PageScriptsTests` → 컴파일 실패.
- [ ] **Step 3: 구현** — Walker의 `Load`·`ReadText`·`PagePaths.ResolveInclude`·`PagePaths.ResolveScriptSrc`를 재사용. 기존 `Collect`의 동작은 바꾸지 않는다.
- [ ] **Step 4: 통과 확인** — 같은 명령, `PageScriptsTests` 전부 통과(기존 포함).
- [ ] **Step 5: 커밋** — `feat(web): related texts of a page for the element check`

### Task 3: `pelly js` — `JsElementsStep`

**Files:**
- Create: `src/PellyCoder.Core/Verification/JsElementsStep.cs`
- Modify: `src/PellyCoder.Core/Fsm/FsmOrchestratorFactory.cs:164` (`EditKind.Js` 단계 목록 끝에 추가), `src/PellyCoder.Core/Fsm/Phases/ReviewPhase.cs` `WebCheckLabels`(`("Api", "API")` 다음에 `("Elements", "요소")`)
- Test: `tests/PellyCoder.Core.Tests/Verification/JsElementsStepTests.cs`

**Interfaces:**
- Consumes: `PageElements.NewBroken`, `PageScripts.RelatedTexts`, `WebRunState.Page`(null이면 `.js` 파일), `WebRunState.SiteRoot`·`SiteIndex`, `ctx.Original.Text`·`ctx.Current.Text`
- Produces: `public sealed class JsElementsStep(IFileSystem fs, ITextFileCodec codec) : IVerifierStep`, `Name => "Elements"`.

Ruling(설계 4.4와의 차이): 설계는 행 이름을 `JsElements`라 했지만 `pelly js`의 다른 행 키가 `Names`·`Api`라 이 경로의 키는 `Elements`(표시 「요소」)로 한다. `pelly asp` 경로의 키는 `JsNames`·`JsApi`를 따라 `JsElements`(Task 4).

동작: `state.Page is null` → `Record("Elements", Skipped, "페이지가 아님")`. 아니면 원본·현재 텍스트를 `AspPage.Parse`, `RelatedTexts`는 원본 페이지로 한 번 구해 양쪽에 쓰고, 결과가 비면 `Passed`, 있으면 `Warned`(경고를 `, `로 이음). 텍스트는 LF로 다룬다(기존 단계들과 같은 방식). 항상 `StepResult.Pass`.

- [ ] **Step 1: 실패하는 테스트** — `JsFile_Skipped`(사유 「페이지가 아님」), `Page_NewIdWithOnlyName_Warned`(행 `Warned`, 상세에 `name="custId"는 있음`), `Page_NoNewBroken_Passed`, `Page_AlwaysPasses`(경고가 있어도 `StepResult.Pass`). 기존 `WebTestState` 도우미로 상태를 만든다.
- [ ] **Step 2: 실패 확인** — `--filter FullyQualifiedName~JsElementsStepTests` → 컴파일 실패.
- [ ] **Step 3: 구현과 연결** — 위대로. 팩토리의 `EditKind.Js` 배열 끝에 `new JsElementsStep(fs, codec)`.
- [ ] **Step 4: 통과 확인** — 같은 명령 통과, 이어서 `dotnet test tests/PellyCoder.Core.Tests --filter FullyQualifiedName~ReviewPhase` 통과.
- [ ] **Step 5: 커밋** — `feat(js): warn when a page edit points at an id or name the page lacks`

### Task 4: `pelly asp` — `AspElementsStep`

**Files:**
- Create: `src/PellyCoder.Core/Verification/AspElementsStep.cs`
- Modify: `src/PellyCoder.Core/Fsm/FsmOrchestratorFactory.cs:127` (`AspClientWarningsStep` 바로 뒤)
- Test: `tests/PellyCoder.Core.Tests/Verification/AspElementsStepTests.cs`

**Interfaces:**
- Consumes: Task 1·2, `AspRunState.OriginalPage`·`SiteRoot`·`Record`, `WebLocateCommon.SiteIndex(fs, ctx, options, siteRoot, codec)`
- Produces: `public sealed class AspElementsStep(IFileSystem fs, PellyOptions options, ITextFileCodec codec) : IVerifierStep`, `public const string Row = "JsElements"`.

동작: 단계 시작 때 `state.Checks.Remove(Row)`(재시도 대비, `AspClientWarningsStep`과 같다). 원본·고친 페이지 둘 다 `Script` 블록이 없으면 아무것도 기록하지 않는다. 있으면 `Passed`/`Warned`. 항상 `StepResult.Pass`.

- [ ] **Step 1: 실패하는 테스트** (`AspClientWarningsStepTests`의 `Edited` 도우미 모양을 따른다)
  - `NoScriptBlocks_NoRow`
  - `HtmlOnlyEdit_RemovesUsedId_Warned` — `--lines`가 HTML 줄만 바꿔 `id="a"`를 지움, 블록의 `$("#a")`가 끊김.
  - `HtmlOnlyEdit_MovesId_NoWarning`
  - `ScriptEdit_NewBrokenRef_Warned`
  - `IncFile_FieldInIncluder_NoWarning` — 대상 `inc/form.inc`(블록 안 새 `$("#q")`), `a.asp`가 include하고 `<input id="q">`를 가짐 → `Passed`.
- [ ] **Step 2: 실패 확인** — `--filter FullyQualifiedName~AspElementsStepTests` → 컴파일 실패.
- [ ] **Step 3: 구현과 연결.**
- [ ] **Step 4: 통과 확인** — 같은 명령 통과, `--filter FullyQualifiedName~Asp` 전체 통과.
- [ ] **Step 5: 커밋** — `feat(asp): warn when an edit breaks a page element reference`

### Task 5: 끝에서 끝 확인과 문서

**Files:**
- Test: `tests/PellyCoder.Core.Tests/Fsm/WebOrchestratorTests.cs`(W2 페이지 끝에서 끝 테스트, 커밋 `8b11a42`)에 가짜 모델 테스트 하나를 더한다.
- Modify: `docs/PRD.md`(변경 이력 v1.27 행, §12.11 W2 단락 끝 한 문장), `README.md`·`deploy/usage.md`(js·asp 판정 표에 `요소`/`JsElements` 경고 행)

- [ ] **Step 1: 실패하는 테스트** — `PageEdit_JqueryIdWithOnlyName_WarnsElements`: 코퍼스 `eval/corpus/Asp/admin/order/list.asp` 114-121을 가짜 모델이 `$("#custId").focus();`를 넣은 본문으로 바꿈 → 종료 0, 기록된 판정(`summary.json` 또는 상태)의 `Elements` 행이 `warned`이고 상세에 `name="custId"는 있음`.
- [ ] **Step 2: 실패 확인 → Task 1–4가 끝났으므로 바로 통과할 수 있다.** 통과하면 테스트가 정말 이 경로를 타는지 `Elements` 행 기대를 `passed`로 잠시 바꿔 실패하는 것을 보고 되돌린다.
- [ ] **Step 3: 문서** — PRD v1.27(날짜 2026-11-09): 「`pelly js`(페이지)·`pelly asp`에 경고 행 `요소`(`JsElements`): 편집 때문에 새로 생긴, 페이지·include·includer·`<script src>`에 없는 id·name을 가리키는 JS 참조(`getElementById`·`getElementsByName`·`$("#id")`·`[name=x]`)를 알림. 막지 않음. 설계 `docs/superpowers/specs/2026-11-09-pelly-web-element-check-design.md`」. README·usage 표에 같은 뜻 한 줄.
- [ ] **Step 4: 게이트** — `dotnet build -warnaserror` 종료 0, `dotnet test` 실패 0.
- [ ] **Step 5: 커밋** — `docs: page element check in PRD, README and usage; e2e test`

### Task 6: 예비 재측정 (컨트롤러가 직접)

Ollama(`qwen3.6:35b-a3b-q4_K_M`, `localhost:11434`)가 필요하다. 개발 DB는 필요 없다.

- [ ] **Step 1:** `eval/benchmark/web/run.sh --repeat 3`(페이지 사례만 고르는 옵션이 있으면 그것)으로 page-001…008을 잰다.
- [ ] **Step 2:** 각 사례의 `Elements` 행을 모은다. page-004에서 모델이 `$("#custId")`를 쓴 실행은 `warned`여야 한다. 다른 사례에 새 경고가 나오면 원인을 적는다.
- [ ] **Step 3:** `docs/web-premeasure.md` W2 절에 「요소 검사 재측정」 소절과 관찰 1의 해결 여부. 로컬 임시 경로(계정 uid가 든 경로)는 쓰지 않는다.
- [ ] **Step 4: 커밋** — `docs: element check premeasure`
