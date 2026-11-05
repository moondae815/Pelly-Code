# `pelly js`·`pelly css` (클라이언트 JavaScript·CSS 수정) 구현 계획 — 1단계 W1

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** `pelly js <FILE> "<요청>"`이 `.js` 파일 하나에서 함수 하나 또는 줄 범위를, `pelly css <FILE> "<요청>"`이 `.css` 파일 하나에서 규칙 하나 또는 줄 범위를 모델로 고치고, 실행·외부 도구 없이 할 수 있는 판정(바이트·인코딩·문법·문법 수준·대상 모양·안전, 경고로 이름·API·속성·덮어쓰기)을 거쳐 사람이 승인하면 남긴다.

**Architecture:** Core에 `Web/` 폴더를 새로 두고 JS 해석은 Acornima(파일 전체를 ES 판별로 해석, 함수·자유 이름·호출 수집), CSS 해석은 손 토큰 해석기로 한다. 파일 코덱은 ASP 코덱의 바이트 이어 붙이기를 공용 기반 클래스(`SplicingTextCodec`)로 꺼내 ASP와 Web이 함께 쓴다(ASP 동작은 그대로). 실행 흐름은 기존 `FsmOrchestrator`에 Web 전용 단계(`JsLocatePhase`·`CssLocatePhase`·`WebSlicePhase`·`WebApplyPhase`·검증 단계 3개)를 `FsmOrchestratorFactory.CreateWeb`으로 조립한다. 사이트 루트·인코딩 기본값은 `asp.siteRoot`·`asp.encoding`을 그대로 읽는다(사람 결정).

**Tech Stack:** .NET 10, Acornima 1.9.0(BSD-3-Clause, net8.0 이상 의존성 없음), System.IO.Abstractions, Spectre.Console.Cli, xUnit v3, Ollama(로컬 예비 측정).

**Spec:** `docs/superpowers/specs/2026-11-04-pelly-js-css-design.md` (§4.1–§4.7. §4.8 W2·§4.9 W3는 이 계획 밖)

## Global Constraints

- 작업 위치는 워크트리 `/Users/payletter/git-root/Pelly-Code-web`(브랜치 `web`, main `4703c5f` 이후에서 만든다). 셸 명령은 `cd /Users/payletter/git-root/Pelly-Code-web && …`. 메인 체크아웃에서 빌드·테스트하지 않는다.
- 게이트: `dotnet build -warnaserror` 종료 0, `dotnet test` 실패 0(건너뜀은 SQL 서버·로컬 모델·Windows 전용만). 테스트 필터 형식: `dotnet test tests/PellyCoder.Core.Tests -- --filter-class "*<클래스>"`.
- **`cs`·`sql`·`asp`의 동작은 바꾸지 않는다.** 공용으로 꺼내는 코드(Task 1)는 기존 `AspTextCodecTests`·`FileGuardTests`가 고치지 않고 그대로 통과해야 한다.
- 사이트 루트 = `AspSiteRoot.Resolve(fs, options.Asp.SiteRoot, workspaceRoot)`. 순수 ASCII 파일 인코딩 = `options.Asp.Encoding`(기본 `cp949`). `web.encoding`·`web.siteRoot` 설정은 만들지 않는다(사람 결정 2026-11-04).
- 편집 영역 밖은 **원본 바이트 그대로**(줄 끝이 섞인 파일 포함). 인코딩 판정 순서(그대로): BOM → 파일 맨 앞 `@charset "<x>";`(`utf-8` → UTF-8; `euc-kr`·`ks_c_5601-1987`·`cp949`·`windows-949` → CP949; 그 밖 → 종료 4 「@charset "<x>"은 지원하지 않습니다」) → ASCII 아닌 바이트가 있으면 엄격한 UTF-8, 아니면 CP949 → 순수 ASCII면 `asp.encoding`. ASP의 `CodePage` 지시문은 Web에서 읽지 않는다. UTF-16 BOM → 종료 4 「UTF-16 파일은 지원하지 않습니다」.
- JS 해석기 `Acornima` **1.9.0** 고정(`Directory.Packages.props`). 해석은 `new Parser(new ParserOptions { EcmaVersion = v }).ParseScript(text)`(모듈이면 `ParseModule`), 실패는 `ParseErrorException`(`LineNumber` 1부터, `Column` 0부터 → 사용자에게는 +1, `Description`).
- 문법 수준 사다리(그대로): `ES5, ES2015, ES2016, …, ES2026`. ES3은 ES5로 묶는다. 표시 이름 `ES5`, `ES2015` …, `EcmaVersion.Latest`는 「최신」. `web.jsTarget` 값: `auto`(기본), `es5`, `es2015`~`es2026`, `latest`(대소문자 무시). 목표 수준 = `auto`면 원본 수준, 아니면 max(설정, 원본 수준).
- 모든 검사는 원본 기준선 대비 **새로 생긴 것만**, 위치가 아닌 내용(규칙+정규화 문장, 코드+문구, 이름)의 개수로 센다. 메시지에 줄 번호를 넣지 않는다(줄 번호는 별도 필드).
- JS 안전 규칙 ID(그대로): `JsEval`, `JsFunctionConstructor`, `JsStringTimer`, `JsDocumentWrite`, `JsExecScript`, `JsActiveX`. CSS 안전 규칙 ID: `CssExpression`, `CssBehavior`, `CssMozBinding`, `CssJavascriptUrl`.
- 함수 대상의 이름·매개변수, CSS 선택자 대상의 선택자 변경은 요청과 상관없이 거부(재시도 피드백).
- JS 이름·API, CSS 속성·덮어쓰기는 **경고만** 한다(실행을 막지 않는다).
- 새 실패 유형 문자열(그대로): `JsSyntaxErrors`, `JsLevelExceeded`, `JsSafetyRejected`, `JsTargetChanged`, `CssStructureErrors`, `CssSafetyRejected`, `CssTargetChanged`, `WebVendorFile`, `WebMinifiedFile`, `WebUnsupportedFile`, `WebEncoding`.
- `web.vendorPaths` 기본값(그대로): `["**/lib/**", "**/plugin*/**", "**/vendor/**", "**/jquery*.js", "**/jquery*.css", "**/bootstrap*"]` — 사이트 루트 기준 상대 경로, `/` 구분, 대소문자 무시.
- 프롬프트 버전 문자열 `js-v1`, `css-v1`. 예산은 `budget.*` 그대로. `[READ-ONLY: around]`는 앞뒤 10줄.
- 검토 고정 문구(그대로): 「JS·CSS는 브라우저에서 실행해 확인하지 않았습니다. 화면에서 직접 확인하십시오. 브라우저 캐시 때문에 바뀐 파일이 바로 반영되지 않을 수 있습니다.」 ASCII 파일에 ASCII 아닌 문자가 들어가면: 「이 파일은 ASCII였고 <CP949|UTF-8>로 저장했습니다」.
- 종료 코드는 기존과 같다(0 성공, 1 검증 실패, 2 거부·중단, 3 대상 못 찾음·모호·거절된 파일, 4 환경, 5 안전 가드).
- 검사 결과는 ASP와 같은 `AspCheck`·`AspCheckStatus` 자료형을 그대로 쓴다(이름 변경은 하지 않는다 — ASP 코드를 건드리지 않기 위해, Claude 결정).
- `HttpClient` 생성은 `src/PellyCoder.Cli/Composition.cs`에만. 코퍼스·문서에 사내 값을 쓰지 않는다(공개 저장소). 코퍼스에 실제 jQuery를 넣지 않는다.
- 사용자 문구 한국어, 코드 식별자·주석 영어. 커밋 메시지 접두 `feat(web)`·`test(web)`·`docs`, 끝에 `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. push하지 않는다.

## Review Focus

1. **jQuery의 이름 없는 함수 안에서 `--line`** (`$(function () { $("#btn").click(function () { … }) })`, 이름 있는 함수 없음) — 수백 줄짜리 `ready` 함수 전체를 대상으로 잡지 않고 `--lines`를 안내한다. 테스트: Task 6 `Line_InsideAnonymousOnly_AsksForLines`, `Line_NamedInsideReady_PicksNamed`.
2. **CP949·줄 끝 섞임·끝 줄바꿈 없음·순수 ASCII JS**에 한글 알림 문구 한 줄 추가 — 범위 밖 바이트 동일, 새 줄은 `asp.encoding`으로, 검토에 ASCII 알림. 테스트: Task 1 `Encode_MixedNewlines_OutsideBytesIdentical`, Task 13 `MixedEndingsAsciiJs_KoreanAdded_Cp949_NoticeShown`.
3. **대부분 ES5인데 어딘가 `let` 하나가 있는 원본** — 원본 수준이 ES2015가 되어 화살표 함수를 막지 않는다; `web.jsTarget: "es5"`를 줘도 원본이 더 높으면 원본 수준 + 알림. 테스트: Task 6 `Level_ConfiguredBelowOriginal_UsesOriginalWithNotice`, Task 2 `Detect_OneLet_Es2015`.
4. **원본이 해석되지 않는 JS**(깨진 파일, IE 전용 구문) — 멈추지 않고 괄호·문자열·주석 짝 비교로 낮추고 「건너뜀」을 보인다. 테스트: Task 7 `Fallback_NewUnbalancedBrace_Reported`, `Fallback_ExistingImbalance_NotCounted`, Task 13 `BrokenJs_FallbackChecks_SkippedShown`.
5. **IE 핵이 가득한 CSS**(`*zoom: 1`, `_height: 1%`, `filter: progid:DXImageTransform.Microsoft.gradient(startColorstr='#fff', endColorstr='#000')`, `color: red\9`) — 구조 문제·`;` 빠짐으로 잘못 보지 않는다. 테스트: Task 3 `IeHacks_NoProblems`, Task 13 `Css_IeHacksOriginal_EditPasses`.

---

## 설계와 다른 점 (이 계획에서 정함, 설계 문서도 같이 고친다 — Task 14)

- 실패 유형 `WebUnsupportedFile`(확장자가 맞지 않음)과 `WebEncoding`(ASP의 `AspEncoding` 대신)을 더한다.
- 이름 규칙의 라이브러리 판정(`jquery*`, `bootstrap*`)을 `web.vendorPaths` 기본값에 넣어 설정으로 뺄 수 있게 한다(설계 §4.1은 별도 목록).
- doctor 「JS·CSS」 행에 사이트 색인 파일 수를 넣지 않는다 — doctor 실행마다 색인을 만들게 되기 때문이다.
- 코퍼스는 `eval/corpus/Web/`이 아니라 기존 `eval/corpus/Asp/` 사이트 안(`js/`, `css/`, `lib/`)에 둔다 — 같은 사이트여야 W2(페이지 안 블록)에서 그대로 쓸 수 있다.

## 파일 구조

| 파일 | 책임 |
| :--- | :--- |
| `src/PellyCoder.Core/Files/SplicingTextCodec.cs` | 바이트 이어 붙이기 쓰기 공용 기반(ASP 코덱에서 꺼냄) |
| `src/PellyCoder.Core/Web/WebTextCodec.cs` | Web 인코딩 판정(`@charset`) |
| `src/PellyCoder.Core/Web/JsDocument.cs`, `JsLevels.cs`, `JsFeatures.cs` | Acornima 감싸기: 함수·최상위 이름·자유 이름·호출, 문법 수준 판정, 수준 넘은 문법 이름 |
| `src/PellyCoder.Core/Web/JsTokenScan.cs` | 해석 안 되는 원본용 짝 검사·`function 이름(` 찾기 |
| `src/PellyCoder.Core/Web/CssSheet.cs`, `CssSelector.cs` | CSS 토큰·규칙·선언·문제 |
| `src/PellyCoder.Core/Web/WebGlob.cs`, `WebFileFilter.cs` | 경로 패턴, 압축·라이브러리 파일 판정 |
| `src/PellyCoder.Core/Web/JqueryVersions.cs`, `WebBuiltins.cs`, `Web/Resources/*.txt` | jQuery 버전 찾기, 브라우저 전역·ES API·jQuery API·CSS 속성 표 |
| `src/PellyCoder.Core/Web/WebSiteIndex.cs` | 사이트 `.js` 최상위 이름 색인과 캐시 |
| `src/PellyCoder.Core/Web/WebRunState.cs` | 실행 상태·검사 결과 |
| `src/PellyCoder.Core/Web/JsChecks.cs`, `JsSafetyGuard.cs`, `CssChecks.cs`, `CssSafetyGuard.cs` | 막는 검사(문법·수준·모양·안전·구조) |
| `src/PellyCoder.Core/Web/JsNameCheck.cs`, `JsApiCheck.cs`, `CssPropertyCheck.cs` | 경고 검사 |
| `src/PellyCoder.Core/Fsm/Phases/JsLocatePhase.cs`, `CssLocatePhase.cs`, `WebSlicePhase.cs`, `WebApplyPhase.cs` | 단계 |
| `src/PellyCoder.Core/Verification/JsNamesStep.cs`, `JsApiStep.cs`, `CssPropertyStep.cs` | 검증 단계 |
| `src/PellyCoder.Core/Llm/Resources/prompts/system.js.txt`, `fewshot.js.txt`, `system.css.txt`, `fewshot.css.txt` | 프롬프트 |
| `src/PellyCoder.Cli/Commands/JsCommand.cs`, `CssCommand.cs` | CLI |
| 수정: `Asp/AspTextCodec.cs`, `Models/EditModels.cs`, `Config/PellyOptions.cs`·`PellyOptionsLoader.cs`, `Reporting/FailureKinds.cs`, `Fsm/RunRequest.cs`, `Fsm/FsmOrchestrator.cs`, `Fsm/FsmOrchestratorFactory.cs`, `Fsm/Phases/EditPhase.cs`·`ReviewPhase.cs`, `Llm/PromptBuilder.cs`·`PromptResources.cs`, `Rules/CodeMask.cs`, `Verification/RulesStep.cs`, `Feedback/FeedbackBuilder.cs`, `Logging/RunLogger.cs`, `Reporting/RunReport.cs`·`ReportSanitizer.cs`·렌더러, `Doctor/DoctorService.cs`, `Cli/CliApp.cs`·`Composition.cs`·`Commands/RunArguments.cs`·`Completion/CompletionTree.cs`, `Directory.Packages.props`, `PellyCoder.Core.csproj` | additive 확장 |

---

### Task 1: 공용 코덱 기반·Web 코덱·설정·Acornima

**Files:**
- Create: `src/PellyCoder.Core/Files/SplicingTextCodec.cs`, `src/PellyCoder.Core/Web/WebTextCodec.cs`
- Modify: `src/PellyCoder.Core/Asp/AspTextCodec.cs`(기반 클래스로 옮김), `src/PellyCoder.Core/Config/PellyOptions.cs`, `PellyOptionsLoader.cs`(`"web"` 구역), `src/PellyCoder.Core/Models/EditModels.cs`(`EditKind { CSharp, Sql, CSharpTest, Asp, Js, Css }`), `src/PellyCoder.Core/Reporting/FailureKinds.cs`(Global Constraints의 11개, 전체 목록 배열에도), `Directory.Packages.props`(`<PackageVersion Include="Acornima" Version="1.9.0" />`), `src/PellyCoder.Core/PellyCoder.Core.csproj`(`PackageReference Acornima`, `EmbeddedResource Include="Web/Resources/**"` — LogicalName `PellyCoder.Core.Web.Resources.%(Filename)%(Extension)`)
- Test: `tests/PellyCoder.Core.Tests/Web/WebTextCodecTests.cs`, `tests/PellyCoder.Core.Tests/Config/PellyOptionsLoaderTests.cs`(추가); 기존 `Asp/AspTextCodecTests.cs`는 고치지 않는다

**Interfaces:**
- Produces:
  - `public abstract class SplicingTextCodec : ITextFileCodec` — 지금 `AspTextCodec`의 `Decode`·`Encode`·`AfterWrite`와 줄 나누기·다수 줄 끝 계산을 그대로 옮긴다. 하위 클래스가 정하는 것: `protected abstract Encoding DetectWithoutBom(string path, byte[] bytes)`, `protected abstract string Utf16Message(string path)`, `protected abstract string StrictReadMessage(string path, Encoding encoding)`. 공용 정적 `protected static Encoding Utf8, Cp949`, `public static Encoding ForName(string name)`(`cp949`/`utf-8`, 그 밖 `ArgumentException`).
  - `AspTextCodec(string defaultEncoding) : SplicingTextCodec` — 공개 생성자·동작·메시지 그대로.
  - `public sealed class WebTextCodec(string defaultEncoding) : SplicingTextCodec` — Global Constraints의 판정 순서.
  - `public sealed class WebOptions { bool Configured; string JsTarget = "auto"; string? JqueryVersion; string[] Globals = []; string[] VendorPaths = [기본값]; }`, `PellyOptions.Web`. 로더 검증 문구: 「web.jsTarget은 auto, es5, es2015~es2026, latest 중 하나입니다」, 「web.jqueryVersion은 1.8.3 같은 형식입니다」(`^\d+\.\d+(\.\d+)?$`). `Configured`는 ASP처럼 구역 존재로 계산, 알려진 키 목록에서 뺀다.
  - 새 `EditKind` 값 때문에 경고가 나는 switch는 이 Task에서 `throw new NotSupportedException()` 분기로 막고, 실제 분기는 해당 Task(9·11)에서 채운다.
- [ ] **Step 1: 실패 테스트** — Codec: `Decode_Bom_Wins`, `Decode_CharsetUtf8_Utf8`, `Decode_CharsetEucKr_Cp949`, `Decode_CharsetOther_Throws`, `Decode_CodePageTextInJs_Ignored`(`var s = "CodePage=65001";` + CP949 한글 줄 → CP949), `Decode_PureAscii_UsesDefault`(`cp949` → 949, `utf-8` → UTF-8), `Decode_Utf16_Throws`(메시지에 「UTF-16 파일」), `Encode_MixedNewlines_OutsideBytesIdentical`, `Encode_NoFinalNewline_Kept`, `Encode_Unencodable_Throws`. Loader: `Web_Defaults`(`Configured == false`, `JsTarget == "auto"`, `VendorPaths` 6개), `Web_SectionPresent_Configured`, `Web_BadJsTarget_Error`, `Web_BadJqueryVersion_Error`, `Web_GlobalsBound`.
- [ ] **Step 2:** `--filter-class "*WebTextCodecTests"`, `"*PellyOptionsLoaderTests"` → 실패.
- [ ] **Step 3:** 구현. 먼저 `SplicingTextCodec`으로 옮기고 `--filter-class "*AspTextCodecTests"`·`"*FileGuardTests"`가 고치지 않고 통과하는지 본 뒤 `WebTextCodec`을 더한다.
- [ ] **Step 4:** 네 필터 통과 + `dotnet build -warnaserror` 종료 0.
- [ ] **Step 5:** 커밋 `feat(web): shared splicing codec, web codec, options and Acornima`

### Task 2: JS 해석(Acornima 감싸기)과 문법 수준

**Files:**
- Create: `src/PellyCoder.Core/Web/JsLevels.cs`, `JsDocument.cs`, `JsFeatures.cs`, `JsTokenScan.cs`
- Test: `tests/PellyCoder.Core.Tests/Web/JsDocumentTests.cs`, `JsLevelsTests.cs`, `JsFeaturesTests.cs`, `JsTokenScanTests.cs`

**Interfaces:**
- Produces:
  - `public static class JsLevels { IReadOnlyList<EcmaVersion> Ladder; string Name(EcmaVersion v); EcmaVersion? FromSetting(string value) /* auto → null */; int Compare(EcmaVersion a, EcmaVersion b); }` — `ES6`·`ES2015` 같은 별칭은 같게 본다, `Latest`는 가장 높다.
  - `public sealed record JsParseError(int Line, int Column, string Description)` — `Column` 1부터.
  - `public sealed record JsLevelResult(EcmaVersion? Level, bool Module, JsParseError? LatestError)`; `JsLevels.Detect(string textLf) -> JsLevelResult` — script로 사다리 순서대로 해석해 처음 성공한 판; script가 `Latest`에서도 실패하면 module로 `Latest` 해석, 성공하면 module로 `ES2015`부터 사다리; 모두 실패면 `Level = null`, `LatestError` = script `Latest` 오류.
  - `public sealed record JsFunction(string Name, string? Owner, IReadOnlyList<string> Parameters, int StartLine, int DeclLine, int EndLine, string Declaration) { string QualifiedName => Owner is null ? Name : $"{Owner}.{Name}"; }` — 모양(설계 §3): `function n(){}`(Owner null), `var|let|const n = function/화살표`(Owner null), 객체 리터럴 `var O = { n: function(){} }`(Owner `O`), `O.n = function`(Owner `O`), `O.prototype.n = function`(Owner `O.prototype`). 이름 없는 함수는 목록에 넣지 않는다. `StartLine`은 바로 위에 붙은 주석 줄 포함, `Declaration`은 선언 줄 원문(앞뒤 공백 제거).
  - `public enum JsArgKind { None, StringLiteral, StringConcat, Other }`; `public sealed record JsCall(string Callee, bool IsNew, int Line, string Text, JsArgKind FirstArg)` — `Callee`: 식별자 사슬이면 점 경로(`eval`, `window.setTimeout`, `document.write`, `Object.assign`), 개체가 식별자 사슬이 아니면 `.` + 멤버 이름(`$("#a").on(…)` → `.on`). `Text`는 호출식 원문을 공백 하나로 정규화.
  - `public sealed record JsNameUse(string Name, int Line, string? Function)` — 어느 범위에서도 선언되지 않은 식별자 참조. 범위 = 프로그램 + 함수마다(매개변수, `var`, 함수 선언, 본문의 `let`·`const`·`class`도 함수 범위로 본다, `catch` 매개변수, 이름 있는 함수 식의 자기 이름). 빼는 것: 계산되지 않은 멤버 속성(`a.b`의 `b`), 계산되지 않은 객체 속성 키, 레이블, 함수 안의 `arguments`.
  - `public sealed class JsDocument` — `static JsDocument Parse(string textLf, EcmaVersion level, bool module)`; 속성 `JsParseError? Error`(있으면 나머지는 빈 목록), `IReadOnlyList<JsFunction> Functions`, `IReadOnlySet<string> TopLevelNames`(최상위 함수 선언·`var`/`let`/`const`/`class`), `IReadOnlyList<JsNameUse> FreeNames`, `IReadOnlyList<JsCall> Calls`, `bool Module`, `bool CutsStatement(TextSpanInfo range)`(어떤 문장·객체 속성 노드가 범위 안에서 시작해 밖에서 끝나거나 밖에서 시작해 안에서 끝나면 true; 범위를 완전히 감싸는 노드는 자르지 않는다), `string LineText(int line)`.
  - `JsFeatures.FirstAbove(string textLf, bool module, EcmaVersion target) -> (string Feature, int Line)?` — `Latest`로 해석해 위치 순서로 처음 만나는, `target`보다 높은 문법. 표(그대로): `ArrowFunctionExpression` 「화살표 함수(=>)」 ES2015, `TemplateLiteral` 「템플릿 문자열(`)」 ES2015, `let`/`const` 선언 「let/const」 ES2015, `ClassDeclaration`/`ClassExpression` 「class」 ES2015, 구조 분해 패턴 「구조 분해」 ES2015, `AssignmentPattern` 매개변수 「매개변수 기본값」 ES2015, `SpreadElement`/`RestElement` 「...(펼치기)」 ES2015, `ForOfStatement` 「for…of」 ES2015, `**` 「거듭제곱(**)」 ES2016, async 함수·`await` 「async/await」 ES2017, `ChainExpression` 「?.」 ES2020, `??` 「??」 ES2020. 찾지 못하면 null(호출한 쪽이 해석 오류 설명을 쓴다).
  - `public sealed record JsScanProblem(int Line, string Code, string Message)`; `JsTokenScan.Problems(string textLf)` — `JS901` `{`/`}` 짝, `JS902` `(`/`)`, `JS903` `[`/`]`, `JS904` 닫히지 않은 문자열·템플릿, `JS905` 닫히지 않은 `/* */`. 정규식 리터럴: 앞의 의미 있는 문자가 없거나 `( , = : [ ! & | ? { } ;`이면 `/`를 정규식 시작으로 본다. `JsTokenScan.FunctionDeclarations(string textLf) -> IReadOnlyList<JsFunction>` — `function 이름(…) {…}` 모양만, 중괄호 짝으로 끝 줄.
- [ ] **Step 1: 실패 테스트** — Levels: `Detect_PureEs5_Es5`, `Detect_OneLet_Es2015`, `Detect_Exponent_Es2016`, `Detect_ImportExport_Module`, `Detect_Broken_NullWithError`, `FromSetting_AutoNullAndValues`. Document: `Functions_AllShapes`(다섯 모양의 `QualifiedName`), `Functions_AnonymousExcluded`(`$(function(){})`), `Functions_LeadingCommentIncluded`, `FreeNames_LocalAndParamResolved`, `FreeNames_MemberAndKeyExcluded`, `FreeNames_HoistedFunctionResolved`, `Calls_CalleePaths`(`eval(x)` → `eval`, `window.setTimeout("f()", 1)` → `window.setTimeout`·`StringLiteral`, `setTimeout("a" + b, 1)` → `StringConcat`, `$("#a").on("click", f)` → `.on`, `new ActiveXObject("x")` → `IsNew`), `CutsStatement_Partial_True`, `CutsStatement_InsideFunctionBody_False`, `Error_ColumnOneBased`. Features: `Arrow_Es5_Found`, `TemplateLiteral_Es5_Found`, `Let_Es5_Found`, `OptionalChain_Es2015_Found`, `NothingAbove_Null`. Scan: `Balanced_NoProblems`, `ExtraBrace_JS901`, `BraceInString_Ignored`, `RegexWithQuote_NotString`(`var r = /'/g;`), `UnclosedComment_JS905`, `FunctionDeclarations_FoundWithEndLine`.
- [ ] **Step 2:** 네 필터 → 실패.
- [ ] **Step 3:** 구현(AST 순회는 Acornima `AstVisitor`).
- [ ] **Step 4:** 통과.
- [ ] **Step 5:** 커밋 `feat(web): JavaScript document, language levels and token scan`

### Task 3: CSS 해석기

**Files:**
- Create: `src/PellyCoder.Core/Web/CssSheet.cs`, `CssSelector.cs`
- Test: `tests/PellyCoder.Core.Tests/Web/CssSheetTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record CssDeclaration(string Property, string Value, int Line)` — `Property` 원문(`*zoom`, `_height` 그대로).
  - `public sealed record CssRule(string Selector, IReadOnlyList<string> Selectors, string? Container, int StartLine, int EndLine, IReadOnlyList<CssDeclaration> Declarations, bool IsAtRule)` — `Selector` = 정규화한 머리(선택자 목록 또는 `@media …`), `Selectors` = 최상위 쉼표로 나눈 정규화 선택자, `Container` = 감싼 묶음 규칙 머리(중첩이면 ` > `로 이음, 없으면 null), `IsAtRule` = 블록이 있는 `@` 규칙 자체(`@media`, `@font-face`, `@keyframes` …).
  - `public sealed record CssProblem(int Line, int Column, string Code, string Message)` — 코드(그대로): `CSS001` 닫히지 않은 주석, `CSS002` 닫히지 않은 문자열, `CSS003` 여는 짝 없는 `}`, `CSS004` 닫히지 않은 `{`, `CSS005` `(`·`[` 짝, `CSS006` 선언 끝 `;` 빠짐(선언 값이 여러 줄이고 뒤 줄이 괄호 밖에서 `^\s*[*_]?-?[A-Za-z][\w-]*\s*:`로 시작하면 그 줄에), `CSS007` 규칙 밖 선언(최상위에서 `{` 없이 `;`로 끝나는 `@`가 아닌 머리).
  - `public sealed class CssSheet` — `static CssSheet Parse(string textLf)`; `Rules`(문서 순서, 묶음 규칙 포함), `Problems`, `bool UsesIeHacks`(`*`/`_` 접두 속성, `filter` 속성, `expression(`, 값 끝 `\9`), `bool UsesVendorPrefixes`(`-webkit-`·`-moz-`·`-ms-`·`-o-` 속성), `string LineText(int)`.
  - `CssSelector.Normalize(string) -> string` — 공백 묶음을 하나로, `>`·`+`·`~`·`,` 앞뒤 공백 제거 후 `>`·`+`·`~` 앞뒤에 공백 하나, 대소문자 그대로. `CssSelector.Matches(CssRule rule, string query)` — `query` 정규화가 `Selectors` 중 하나와 같으면 true.
- [ ] **Step 1: 실패 테스트** — `Rules_SelectorsSplitAndNormalized`(`.a  >b, .c` → `.a > b`, `.c`), `Rules_InsideMedia_HaveContainer`, `AtRule_IsAtRuleBlock`, `Declarations_WithLines`, `IeHacks_NoProblems`(Review Focus 5의 네 줄 → 문제 0, `UsesIeHacks`), `VendorPrefixes_Detected`, `UnclosedComment_CSS001`, `UnclosedString_CSS002`, `StrayBrace_CSS003`, `UnclosedBlock_CSS004`, `MissingSemicolon_CSS006`(`color: red` 다음 줄 `margin: 0;`), `MultiLineValue_NotCSS006`(`font-family: a,` 다음 줄 `b;`), `DeclarationOutsideRule_CSS007`, `BraceInString_Ignored`(`content: "}"`), `UrlUnquoted_Ignored`(`url(a;b.png)`), `Matches_PartOfList`.
- [ ] **Step 2:** `--filter-class "*CssSheetTests"` → 실패.
- [ ] **Step 3:** 구현(손 토큰 해석기, CSS Syntax Level 3 토큰 규칙).
- [ ] **Step 4:** 통과.
- [ ] **Step 5:** 커밋 `feat(web): CSS sheet parser`

### Task 4: 파일 판정·jQuery 버전·내장 표

**Files:**
- Create: `src/PellyCoder.Core/Web/WebGlob.cs`, `WebFileFilter.cs`, `JqueryVersions.cs`, `WebBuiltins.cs`, `src/PellyCoder.Core/Web/Resources/browser-globals.txt`, `es-apis.txt`, `jquery-apis.txt`, `css-properties.txt`
- Test: `tests/PellyCoder.Core.Tests/Web/WebFileFilterTests.cs`, `JqueryVersionsTests.cs`, `WebBuiltinsTests.cs`

**Interfaces:**
- Produces:
  - `WebGlob.IsMatch(string pattern, string relativePath) -> bool` — `/` 구분(`\`는 `/`로), 대소문자 무시, `**`(0개 이상 폴더), `*`(한 구간 안), `?`.
  - `public enum WebFileVerdict { Ok, Minified, Vendor }`; `WebFileFilter(string siteRoot, IReadOnlyList<string> vendorPaths)`; `Classify(string fullPath, string textLf) -> (WebFileVerdict Verdict, string? Reason)` — 압축: 이름이 `*.min.js`·`*.min.css`, 또는 평균 줄 길이 > 500, 또는 1,000자 넘는 줄이 줄 수의 절반 이상. 라이브러리: 사이트 루트 기준 상대 경로가 `vendorPaths` 중 하나에 맞음(사이트 루트 밖이면 경로 규칙은 보지 않음), 또는 앞 40줄의 첫 블록 주석에 `jQuery JavaScript Library`·`@license`가 있거나 `(c)`와 `license`가 한 줄에 함께 있음(대소문자 무시). `Reason`은 한국어(「라이브러리 파일은 고치지 않습니다(lib/** 경로). web.vendorPaths에서 빼면 고칠 수 있습니다」, 「압축된 파일은 고치지 않습니다(*.min.js)」).
  - `public sealed record JqueryFound(Version Version, string RelativePath)`; `JqueryVersions.Find(IFileSystem fs, string siteRoot, int maxFiles = 5000) -> IReadOnlyList<JqueryFound>` — 이름이 `jquery`로 시작하는 `.js`, 앞 4 KB에서 `jQuery (?:JavaScript Library )?v(\d+\.\d+(?:\.\d+)?)`, 없으면 이름 `^jquery-(\d+\.\d+(?:\.\d+)?)(?:\.min)?\.js$`; 플러그인 머리(`jQuery Validation Plugin v1.19`)는 맞지 않는다. `JqueryVersions.Baseline(IReadOnlyList<JqueryFound> found, string? configured) -> Version?` — 설정이 이기고, 아니면 가장 낮은 버전.
  - `public sealed class WebBuiltins { static WebBuiltins Default; bool IsGlobal(string name); EcmaVersion? EsApiLevel(string key) /* key: 식별자 · Obj.member · .member */; (Version? Added, Version? Removed)? Jquery(string member); bool IsCssProperty(string nameLower); }` — 리소스 형식: 한 줄에 한 항목, `#` 주석. `browser-globals.txt` 이름만(대소문자 구별). `es-apis.txt` `<key> <ES 판>`. `jquery-apis.txt` `<member> <added|-> <removed|->`. `css-properties.txt` 소문자 속성 이름. 최소 내용(테스트가 확인): 전역 `window`, `document`, `alert`, `confirm`, `JSON`, `Math`, `Date`, `parseInt`, `undefined`, `NaN`, `Infinity`, `setTimeout`, `location`, `navigator`, `console`, `XMLHttpRequest`, `encodeURIComponent`, `escape`, `event`; ES API `.includes ES2016`, `.startsWith ES2015`, `.endsWith ES2015`, `.find ES2015`, `.findIndex ES2015`, `.padStart ES2017`, `Array.from ES2015`, `Object.assign ES2015`, `Object.entries ES2017`, `Object.values ES2017`, `Promise ES2015`, `Map ES2015`, `Set ES2015`, `Symbol ES2015`, `fetch ES2015`(브라우저 API지만 같은 시기로 묶음); jQuery 설계 §4.4 표(`closest 1.3 -`, `live 1.3 1.9`, `delegate 1.4.2 -`, `parseJSON 1.4.1 -`, `Deferred 1.5 -`, `promise 1.5 -`, `prop 1.6 -`, `on 1.7 -`, `off 1.7 -`, `addBack 1.8 -`, `browser - 1.9`, `size - 3.0`, `bind - 3.0`, `unbind - 3.0`). CSS 속성은 MDN CSS 속성 목록 기준(빌더가 만들고 출처를 파일 머리 주석에 적음).
- [ ] **Step 1: 실패 테스트** — Glob: `DoubleStar_AnyDepth`, `Star_OneSegment`, `CaseInsensitive_Backslash`. Filter: `MinName_Minified`, `LongLines_Minified`, `NormalFile_Ok`, `LibPath_Vendor`, `JqueryName_Vendor`, `LicenseHeader_Vendor`, `VendorPathRemoved_Ok`(설정에서 `**/lib/**`를 뺌), `OutsideSiteRoot_PathRulesIgnored`. jQuery: `Header_Version`, `MinHeader_Version`(`/*! jQuery v1.8.3 | … */`), `FileNameOnly_Version`, `PluginHeader_NotMatched`, `Several_BaselineLowest`, `Configured_Wins`. Builtins: `Globals_Known`, `EsApi_Levels`, `Jquery_AddedRemoved`, `CssProperty_Known`(`color`, `zoom`, `-webkit-` 없는 `transition`).
- [ ] **Step 2:** 세 필터 → 실패.
- [ ] **Step 3:** 구현.
- [ ] **Step 4:** 통과.
- [ ] **Step 5:** 커밋 `feat(web): file filter, jQuery versions and built-in tables`

### Task 5: 사이트 색인

**Files:**
- Create: `src/PellyCoder.Core/Web/WebSiteIndex.cs`
- Test: `tests/PellyCoder.Core.Tests/Web/WebSiteIndexTests.cs`

**Interfaces:**
- Consumes: Task 1 `WebTextCodec`, Task 2 `JsDocument`·`JsTokenScan`, Task 4 `WebFileFilter`.
- Produces:
  - `public sealed record JsIndexEntry(string Name, string RelativePath, int Line, string Declaration)`; `public sealed record WebSiteIndexResult(IReadOnlyList<JsIndexEntry> Entries, int Files, bool Truncated) { IReadOnlySet<string> Names; }`
  - `WebSiteIndex.Load(IFileSystem fs, string siteRoot, WebFileFilter filter, ITextFileCodec codec, string cachePath, int maxFiles = 2000, long maxBytes = 1_048_576) -> WebSiteIndexResult` — 사이트 루트 아래 `.js` 중 `Classify`가 `Ok`이고 `maxBytes` 이하인 파일의 최상위 함수 선언·`var`/`let`/`const` 이름(`Latest`로 해석, 실패면 `JsTokenScan.FunctionDeclarations`). 캐시 JSON(`{"version":1,"files":{"<상대 경로>":{"sha256":"…","entries":[…]}}}`)은 SHA-256이 같으면 다시 쓰고, 사라진 파일은 뺀다. 쓰기는 임시 파일 뒤 이동. 캐시를 못 읽으면(깨짐·버전 다름) 조용히 다시 만든다. 파일을 못 읽으면 건너뛴다. 경로 순서는 정렬해서 결정적으로.
  - 캐시 경로는 `<작업 공간 루트>/.pelly/cache/web-site-index.json`(Task 6에서 정함).
- [ ] **Step 1: 실패 테스트** — `Index_TopLevelFunctionsAndVars`, `Index_NestedFunctionsExcluded`, `Index_SkipsVendorAndMinified`, `Index_UnparseableFile_FallbackScan`, `Index_CacheWritten_AndUpdatedOnChange`(파일 내용 바꿈 → 새 이름), `Index_CorruptCache_Rebuilt`, `Index_Truncated_AtMaxFiles`, `Index_TooLarge_Skipped`.
- [ ] **Step 2:** `--filter-class "*WebSiteIndexTests"` → 실패.
- [ ] **Step 3:** 구현.
- [ ] **Step 4:** 통과.
- [ ] **Step 5:** 커밋 `feat(web): site index of top-level JavaScript names`

### Task 6: 실행 상태·대상 찾기(LOCATE)·요청

**Files:**
- Create: `src/PellyCoder.Core/Web/WebRunState.cs`, `src/PellyCoder.Core/Fsm/Phases/JsLocatePhase.cs`, `CssLocatePhase.cs`
- Modify: `src/PellyCoder.Core/Fsm/RunRequest.cs`(`RunRequest` 끝에 `string? Selector = null`, `RunContext`에 `public WebRunState? Web { get; set; }`), `src/PellyCoder.Core/Fsm/FsmOrchestrator.cs`(`Describe`: Js/Css의 LOCATE 「대상 찾기: 함수 <이름> / 선택자 <선택자> / <N>번 줄 / 줄 A-B / 파일에서」, VERIFY Js 「규칙·이름·API 검사」, Css 「규칙·속성 검사」)
- Test: `tests/PellyCoder.Core.Tests/Fsm/JsLocatePhaseTests.cs`, `CssLocatePhaseTests.cs`

**Interfaces:**
- Consumes: Task 1~5, `AspSiteRoot.Resolve`.
- Produces:
  - `public enum WebTargetUnit { Function, Rule, Lines }`
  - `public sealed class WebRunState` — `EditKind Kind`, `WebTargetUnit Unit`, `TextSpanInfo OriginalSpan`, `TextSpanInfo CurrentSpan { get; set; }`, `JsFunction? Function`, `CssRule? Rule`, `string EncodingName`, `bool OriginalAscii`, `string SiteRoot`, `Lazy<WebSiteIndexResult> SiteIndex`; JS: `JsDocument? OriginalJs`(해석 안 되면 null), `bool Fallback`, `string? FallbackReason`(「원본이 해석되지 않음: 줄 N」), `bool Module`, `EcmaVersion? OriginalLevel`, `EcmaVersion? TargetLevel`, `IReadOnlyList<JqueryFound> JqueryFound`, `Version? Jquery`; CSS: `CssSheet? OriginalCss`; 계수 `int LevelRejections`, `int NameWarnings`, `int ApiWarnings`, `int PropertyWarnings`(모두 `{ get; set; }`); `Dictionary<string, AspCheck> Checks`, `void Record(string name, AspCheckStatus status, string? detail = null)`(ASP와 같은 의미); `IReadOnlyList<EditableRegion> EditableRegions() => [new(CurrentSpan, "target")]`.
  - `JsLocatePhase(IFileSystem fs, IUserInteraction ui, PellyOptions options, WebTextCodec codec) : IPhase`:
    - 확장자가 `.js`가 아님 → `WebUnsupportedFile`, `NotFound`(종료 3, 「pelly js는 .js 파일만 고칩니다」). `WebFileFilter.Classify`가 `Minified`/`Vendor` → `WebMinifiedFile`/`WebVendorFile`, `NotFound`, 이유 문구.
    - `JsLevels.Detect`. `Level` null이면 `Fallback = true`, `ctx.Messages`에 「JS 문법 검사 건너뜀(원본이 해석되지 않음: 줄 N)」. 아니면 `TargetLevel` = Global Constraints 규칙, 설정이 원본보다 낮으면 「원본이 <ES2017> 문법을 써서 web.jsTarget(<ES5>) 대신 <ES2017>을 기준으로 봅니다」.
    - 대상: `--function`(= `RunRequest.MethodName`, `QualifiedName` 또는 `Name`과 대소문자 구별 일치; 여럿이면 줄 번호와 함께 `ui` 선택, 비대화형이면 `Ambiguous`) / `--line`(그 줄을 감싸는 가장 안쪽 **이름 있는** 함수, 없으면 「N번 줄을 감싸는 이름 있는 함수가 없습니다. --lines A-B로 범위를 주십시오」 `NotFound`) / `--lines`(파일 밖이면 `NotFound`, `CutsStatement`면 「줄 A-B가 문장 중간을 자릅니다. 문장 경계로 범위를 주십시오」 `NotFound`; 대체 모드에서는 자르기 검사를 하지 않음) / 없음(이름 있는 함수 하나면 그것, 여럿이면 선택·비대화형 `Ambiguous`, 0개면 「--lines A-B로 범위를 주십시오」 `NotFound`). 대체 모드의 `--function`은 `JsTokenScan.FunctionDeclarations`, `--line`은 「--lines」 안내.
    - 둘 이상 함께 → `EnvError`(CLI도 막음).
    - `ctx.Target = new EditTarget(EditKind.Js, path, display, span, null)` — display `function fnSearch`(Owner 있으면 `Owner.Name`) 또는 `줄 A-B`. `ctx.ProjectStyle = "Web"`. 사이트 루트 해석, jQuery 찾기(`Baseline`), 버전이 둘 이상이면 `ctx.Messages`에 「jQuery 버전이 여럿입니다: 1.4.2(lib/…), 1.8.3(…) — 가장 낮은 버전(1.4.2)을 기준으로 봅니다」. `SiteIndex`는 `Lazy`(캐시 경로 `<작업 공간 루트>/.pelly/cache/web-site-index.json`). `OriginalAscii` = 원본 바이트가 모두 0x80 미만.
  - `CssLocatePhase(IFileSystem fs, IUserInteraction ui, PellyOptions options, WebTextCodec codec) : IPhase` — 같은 파일 판정(`.css`). 대상: `--selector`(= `RunRequest.Selector`, `CssSelector.Matches`, 묶음 규칙 제외; 여럿이면 줄 번호와 `Container`로 선택·비대화형 `Ambiguous`) / `--line`(가장 안쪽 규칙, 묶음 규칙 포함) / `--lines`(범위 처음·끝이 규칙 중간이면 「줄 A-B가 규칙 중간을 자릅니다」 `NotFound`) / 없음(규칙 하나면 그것, 아니면 「--selector, --line, --lines 중 하나를 주십시오」 `NotFound`). 원본에 CSS 문제가 있어도 멈추지 않는다(기준선). display는 선택자(묶음 안이면 `@media print > .a`) 또는 `줄 A-B`.
- [ ] **Step 1: 실패 테스트** — Js: `Function_ByName`, `Function_ByQualifiedName`(`Order.search`, `Order.prototype.save`), `Function_CaseSensitive_NotFound`, `Function_Several_NonInteractive_Ambiguous`, `Line_NamedInsideReady_PicksNamed`, `Line_InsideAnonymousOnly_AsksForLines`, `Lines_CutsStatement_NotFound`, `Lines_Valid_Display`, `NoOption_OneFunction_Auto`, `NotJs_Refused`, `MinFile_Refused`, `VendorPath_Refused`, `Level_AutoUsesOriginal`, `Level_ConfiguredAboveOriginal_UsesConfigured`, `Level_ConfiguredBelowOriginal_UsesOriginalWithNotice`, `Broken_FallbackWithMessage`, `Broken_FunctionStillFoundByScan`, `Jquery_SeveralVersions_Message`, `OriginalAscii_Set`. Css: `Selector_Found`, `Selector_InMediaAndTop_NonInteractive_Ambiguous`, `Line_InnermostRule`, `Line_OnMediaHeader_AtRule`, `Lines_CutsRule_NotFound`, `NotCss_Refused`, `OriginalProblems_NotBlocking`.
- [ ] **Step 2:** 두 필터 → 실패.
- [ ] **Step 3:** 구현.
- [ ] **Step 4:** 통과.
- [ ] **Step 5:** 커밋 `feat(web): run state and LOCATE for js and css`

### Task 7: 막는 검사 — 문법·수준·모양·안전·구조

**Files:**
- Create: `src/PellyCoder.Core/Web/JsChecks.cs`, `JsSafetyGuard.cs`, `CssChecks.cs`, `CssSafetyGuard.cs`
- Modify: `src/PellyCoder.Core/Rules/CodeMask.cs`(`Js`, `Css`)
- Test: `tests/PellyCoder.Core.Tests/Web/JsChecksTests.cs`, `JsSafetyGuardTests.cs`, `CssChecksTests.cs`, `CssSafetyGuardTests.cs`, `tests/PellyCoder.Core.Tests/Rules/CodeMaskTests.cs`(추가)

**Interfaces:**
- Consumes: Task 2·3·6.
- Produces:
  - `public sealed record JsSyntaxVerdict(JsDocument? Patched, IReadOnlyList<Diagnostic> Errors, bool LevelExceeded, string? Feature, int? FeatureLine)`; `JsChecks.Syntax(WebRunState state, string fileName, string patchedText) -> JsSyntaxVerdict` — 대체 모드: `JsTokenScan.Problems`를 원본과 비교해 `(Code, Message)` 개수가 는 것만 `Diagnostic(file, line, 1, "error", code, message)`. 보통: `TargetLevel`·원본 모듈 여부로 해석 → 성공이면 오류 없음; 실패면 `Latest`로 해석해 성공하면 `LevelExceeded = true`와 `JsFeatures.FirstAbove`(없으면 `Feature` = 해석 오류 설명, `FeatureLine` = 오류 줄); `Latest`도 실패면 `Diagnostic(file, line, column, "error", "JS", description)`.
  - `JsChecks.ShapeViolation(WebRunState state, JsDocument? patched, string patchedText) -> string?` — 함수 대상: 같은 `QualifiedName` 함수가 정확히 하나(「함수 fnSearch를 찾을 수 없습니다(이름을 바꾸지 마십시오)」), 매개변수 목록(이름·순서) 같음(「함수 fnSearch의 매개변수를 바꿀 수 없습니다: (a, b) → (a, b, c)」), `TopLevelNames`에 원본에 없던 이름이 없음(「함수 밖에 새 최상위 선언을 만들 수 없습니다: x」). 대체 모드는 `JsTokenScan.FunctionDeclarations`로 이름·매개변수만. 줄 범위 대상은 null.
  - `public sealed record WebSafetyFinding(string Rule, int Line, string Text)`; `JsSafetyGuard.Findings(JsDocument doc)`, `JsSafetyGuard.NewFindings(JsDocument original, JsDocument patched)` — 규칙(`Rule`)별 호출 개수가 는 것, 는 만큼(원본에 없던 텍스트의 호출을 먼저 보고). 규칙: `JsEval` (`eval`, `window.eval`), `JsFunctionConstructor` (`Function`, `window.Function`, `new Function`), `JsStringTimer` (`setTimeout`·`setInterval`·`window.` 붙은 것, `FirstArg`가 `StringLiteral`·`StringConcat`), `JsDocumentWrite` (`document.write`, `document.writeln`), `JsExecScript` (`execScript`, `window.execScript`), `JsActiveX` (`new ActiveXObject`). `JsSafetyGuard.NewFindingsText(string originalLf, string patchedLf)` — 대체 모드용: `CodeMask.Js`한 텍스트에서 정규식(`\beval\s*\(`, `\bnew\s+Function\s*\(`, `\b(?:set(?:Timeout|Interval))\s*\(\s*["']`, `\bdocument\.write(?:ln)?\s*\(`, `\bexecScript\s*\(`, `\bnew\s+ActiveXObject\s*\(`), 규칙별 개수 비교.
  - `CssChecks.NewProblems(CssSheet original, CssSheet patched) -> IReadOnlyList<CssProblem>` — `(Code, Message)` 개수 비교. `CssChecks.ShapeViolation(WebRunState state, CssSheet patched) -> string?` — 선택자 대상: 같은 `Selector`·`Container`의 규칙이 정확히 하나(「선택자 .a를 바꿀 수 없습니다」).
  - `CssSafetyGuard.NewFindings(CssSheet original, CssSheet patched) -> IReadOnlyList<WebSafetyFinding>` — `CssExpression`(값에 `expression(`), `CssBehavior`(속성 `behavior`), `CssMozBinding`(속성 `-moz-binding`), `CssJavascriptUrl`(값의 `url(`에 `javascript:`), 모두 대소문자 무시, `(Rule, "<속성>: <값>" 정규화)` 개수 비교.
  - `CodeMask.Js(string textLf)` — 주석·문자열·템플릿 리터럴 안의 문자를 공백으로(줄 바꿈 유지). `CodeMask.Css(string textLf)` — 주석만.
- [ ] **Step 1: 실패 테스트** — JsChecks: `Syntax_Valid_NoErrors`, `Syntax_Broken_DiagnosticWithColumn`, `Level_ArrowInEs5_Exceeded_FeatureNamed`, `Level_ArrowInEs2015_Ok`, `Module_ParsedAsModule`, `Fallback_NewUnbalancedBrace_Reported`, `Fallback_ExistingImbalance_NotCounted`, `Shape_Renamed`, `Shape_ParamAdded`, `Shape_NewTopLevel`, `Shape_LinesTarget_Null`, `Shape_Fallback_ParamsCompared`. JsSafety: `Eval_New_Rejected`, `Eval_Existing_NotCounted`, `StringTimer_Rejected`, `FunctionTimer_Allowed`(`setTimeout(f, 10)`), `DocumentWrite_Rejected`, `ActiveX_Rejected`, `FallbackText_Eval_Rejected`, `EvalInString_NotCounted`(대체 모드, `"eval("`). Css: `NewUnclosedBlock_Reported`, `ExistingProblem_NotCounted`, `Shape_SelectorChanged`, `Expression_New_Rejected`, `Behavior_Rejected`, `JavascriptUrl_Rejected`, `ExistingExpression_NotCounted`. CodeMask: `Js_StringsAndCommentsMasked`, `Css_CommentsMasked`.
- [ ] **Step 2:** 다섯 필터 → 실패.
- [ ] **Step 3:** 구현.
- [ ] **Step 4:** 통과.
- [ ] **Step 5:** 커밋 `feat(web): syntax, level, shape and safety checks`

### Task 8: 경고 검사 — 이름·API·속성·덮어쓰기

**Files:**
- Create: `src/PellyCoder.Core/Web/JsNameCheck.cs`, `JsApiCheck.cs`, `CssPropertyCheck.cs`
- Test: `tests/PellyCoder.Core.Tests/Web/JsNameCheckTests.cs`, `JsApiCheckTests.cs`, `CssPropertyCheckTests.cs`

**Interfaces:**
- Consumes: Task 2·3·4·5.
- Produces:
  - `public sealed record JsUnknownName(string Name, int Line)`; `JsNameCheck.NewUnknown(JsDocument original, JsDocument patched, Func<string, bool> isKnown) -> IReadOnlyList<JsUnknownName>` — 자유 이름 중 `isKnown`이 false인 것의 이름별 개수가 원본보다 는 것(는 줄만). `JsNameCheck.KnownSet(WebBuiltins b, bool jquery, IReadOnlySet<string> siteNames, IReadOnlyList<string> globals) -> Func<string, bool>` — 내장 전역, jQuery를 찾았으면 `$`·`jQuery`, 사이트 색인, `web.globals`.
  - `JsApiCheck.NewWarnings(JsDocument original, JsDocument patched, EcmaVersion target, Version? jquery, WebBuiltins b) -> IReadOnlyList<string>` — 호출 `Callee`로 키를 만든다(식별자 → 그대로, `Obj.m` → 그대로와 `.m`, `.m` → 그대로; 전역 식별자 참조 `Promise`·`Map` 등은 자유 이름에서도). 문구(그대로): 「`<key>`는 <ES2016> 기능입니다(이 파일 기준 <ES5>)」, 「`.<m>`은 jQuery <1.7>부터 있습니다(사이트 기준 <1.4.2>)」, 「`.<m>`은 jQuery <1.9>에서 없어졌습니다(사이트 기준 <1.10.2>)」. 개수 비교로 새것만, 같은 문구는 한 번(줄은 첫 줄을 「(줄 N)」으로 덧붙임 — 경고 표시용이라 이 문구에는 줄을 넣는다).
  - `CssPropertyCheck.NewUnknown(CssSheet original, CssSheet patched, WebBuiltins b) -> IReadOnlyList<string>` — 원본이 `UsesIeHacks`면 `*`·`_` 접두를 떼고 보고 `filter`는 알려진 것으로, 원본이 `UsesVendorPrefixes`면 벤더 접두 속성은 모두 알려진 것으로; 모르는 속성 이름의 개수 증가 → 「알 수 없는 CSS 속성: <이름>(줄 N)」.
  - `CssPropertyCheck.OverrideNotices(CssSheet patched, TextSpanInfo span) -> IReadOnlyList<string>` — 범위 안 규칙의 선언마다, 같은 `Container`에서 뒤에 오는 규칙이 `Selectors`를 하나라도 공유하고 같은 속성(대소문자 무시)을 선언하면 「줄 N의 `<속성>`은 줄 M의 `<선택자>` 규칙이 덮어씁니다」.
- [ ] **Step 1: 실패 테스트** — Names: `Invented_Warned`, `SiteIndexName_Known`, `JqueryDollar_KnownOnlyWithJquery`, `Globals_Known`, `ExistingUnknown_NotCounted`, `LocalVariable_NotFree`. Api: `Includes_Es5_Warned`, `Includes_Es2016_NotWarned`, `On_Jquery142_Warned`, `On_Jquery183_NotWarned`, `Live_Jquery1102_RemovedWarned`, `NoJquery_NoJqueryWarnings`, `Existing_NotCounted`, `PromiseIdentifier_Es5_Warned`. Css: `Unknown_Warned`, `IeHackPrefix_AllowedWhenOriginalUses`, `IeHackPrefix_WarnedWhenOriginalDoesNot`, `VendorPrefix_AllowedWhenOriginalUses`, `Override_SameContainerLaterRule_Notice`, `Override_OtherMedia_NoNotice`.
- [ ] **Step 2:** 세 필터 → 실패.
- [ ] **Step 3:** 구현.
- [ ] **Step 4:** 통과.
- [ ] **Step 5:** 커밋 `feat(web): name, API, property and override warnings`

### Task 9: 문맥(SLICE)·프롬프트

**Files:**
- Create: `src/PellyCoder.Core/Fsm/Phases/WebSlicePhase.cs`, `src/PellyCoder.Core/Llm/Resources/prompts/system.js.txt`, `fewshot.js.txt`, `system.css.txt`, `fewshot.css.txt`
- Modify: `src/PellyCoder.Core/Llm/PromptResources.cs`(끝에 `string SystemJs = "", string FewShotJs = "", string JsVersion = "", string SystemCss = "", string FewShotCss = "", string CssVersion = ""`; 쌍의 버전이 다르면 기존처럼 예외; `VersionFor(Js) = "js-v1"`, `VersionFor(Css) = "css-v1"`), `src/PellyCoder.Core/Llm/PromptBuilder.cs`(`BuildWeb`), `src/PellyCoder.Core/Fsm/Phases/EditPhase.cs`(`EditKind.Js or EditKind.Css => prompts.BuildWeb(...)`)
- Test: `tests/PellyCoder.Core.Tests/Fsm/WebSlicePhaseTests.cs`, `tests/PellyCoder.Core.Tests/Llm/PromptBuilderTests.cs`(추가), `PromptResourcesTests.cs`(추가)

**Interfaces:**
- Consumes: Task 2~6.
- Produces:
  - `WebSlicePhase(FileGuard fileGuard, ITokenCounter tokens, BudgetOptions budget) : IPhase` — `AspSlicePhase`처럼 외부 변경 확인(`FileChangedExternally`). 현재 텍스트에서 대상을 다시 찾는다(함수: `QualifiedName`, 규칙: `Selector`+`Container`, 못 찾으면 기존 `TargetNotFound`; 줄 범위: `CurrentSpan`) → `CurrentSpan` 갱신. 부분(`SlicePart.Kind`, 높은 우선순위 순):
    - JS: `target`, `env`(「Language level: ES5 — do not use let/const, arrow functions, template literals, classes …」처럼 목표 수준보다 높은 `JsFeatures` 표 이름을 영어로 나열; `jQuery 1.8.3` 또는 `jQuery: not found`; `File encoding: CP949`), `callees`(대상 안 `Calls`의 `Callee`가 같은 파일 `Functions`의 `QualifiedName`/`Name` 또는 사이트 색인 이름이면 그 `Declaration`과 바로 위 주석 줄; 색인 항목은 `// <상대 경로>` 머리와 함께), `file`(파일의 이름 있는 함수 `Declaration` 목록), `around`(앞뒤 10줄). 예산이 모자라면 `around` → `file` → `callees` → `env` 순으로 뺀다.
    - CSS: `target`, `env`(인코딩, 「IE hacks used in this file: yes/no」, 「Vendor prefixes used in this file: yes/no」), `related`(같은 파일에서 대상 규칙과 `Selectors`를 공유하는 다른 규칙 전체), `around`. 빼는 순서 `around` → `related` → `env`.
    - `target`이 혼자 넘으면 `BudgetExceeded`. `LanguageHint` = JS `JavaScript <ES5|…> (<script|module>); jQuery <버전|none>; file encoding <CP949|UTF-8>`, CSS `CSS; file encoding <…>; IE hacks: <yes|no>`. `ctx.SliceResult.Regions = state.EditableRegions()`.
  - `PromptBuilder.BuildWeb(EditTarget target, Slice slice, string request, Feedback? feedback, IReadOnlyList<ProjectRule>? rules = null) -> PromptBuildResult` — `target.Kind`로 리소스 고름. 헤더 순서 JS: `[READ-ONLY: env]`, `[READ-ONLY: callees]`, `[READ-ONLY: file]`, `[READ-ONLY: around]`, `[EDITABLE: target]`; CSS: `[READ-ONLY: env]`, `[READ-ONLY: related]`, `[READ-ONLY: around]`, `[EDITABLE: target]`. `### TARGET` 줄 = `<display> — file: <파일 이름>`. 나머지(요청·규칙·피드백 위치)는 `BuildAsp`와 같다.
  - `system.js.txt`(`# version: 1`, 영어, `system.asp.txt`와 같은 블록 형식 설명 뒤 규칙): 1 SEARCH/REPLACE만 / 2 SEARCH는 [EDITABLE: target]에서만 / 3 [READ-ONLY: env]의 language level과 jQuery version 안에서만 / 4 없는 함수를 만들지 말고 [READ-ONLY: callees]·[READ-ONLY: file]과 내장만 / 5 함수 이름·매개변수를 바꾸지 않음, 함수 밖에 새 최상위 선언을 만들지 않음 / 6 `eval`, `new Function`, 문자열 `setTimeout`/`setInterval`, `document.write` 추가 금지 / 7 원본의 들여쓰기·따옴표·세미콜론 모양을 따름 / 8 Target: {LanguageHint} / 9 FEEDBACK이 있으면 그 문제를 고침. `fewshot.js.txt`: ES5 + jQuery 이벤트 처리기 줄 범위 예(CP949 한글 `alert` 문구 포함) 하나, 코퍼스와 다른 이름.
  - `system.css.txt`(`# version: 1`): 1·2 같음 / 3 선택자를 바꾸지 않음 / 4 `expression()`, `behavior`, `-moz-binding`, `javascript:` URL 금지 / 5 원본이 IE 핵·벤더 접두를 쓰면 같은 방식을 따르고, 쓰지 않으면 새로 넣지 않음 / 6 원본 모양(들여쓰기, 한 줄 규칙 여부) / 7 Target: {LanguageHint} / 8 FEEDBACK. `fewshot.css.txt`: 규칙 하나 수정 예.
- [ ] **Step 1: 실패 테스트** — Slice: `Js_PartsInOrder`, `Js_CalleeFromSiteIndex_WithPathHeader`, `Js_EnvListsForbiddenFeaturesForEs5`, `Js_Budget_DropsAroundFirst`, `Js_TargetTooLarge_BudgetExceeded`, `Js_FunctionMovedAfterWrite_Refound`, `Js_ExternalChange_EnvError`, `Css_PartsInOrder`, `Css_RelatedSharesSelector`. Prompt: `BuildWeb_Js_HeaderOrder`, `BuildWeb_Css_HeaderOrder`, `Resources_JsVersion_js-v1`, `Resources_CssVersion_css-v1`.
- [ ] **Step 2:** 필터 실행 → 실패.
- [ ] **Step 3:** 구현.
- [ ] **Step 4:** 통과.
- [ ] **Step 5:** 커밋 `feat(web): SLICE and the js-v1, css-v1 prompts`

### Task 10: 패치 적용(APPLY)과 규칙 검사

**Files:**
- Create: `src/PellyCoder.Core/Fsm/Phases/WebApplyPhase.cs`
- Modify: `src/PellyCoder.Core/Feedback/FeedbackBuilder.cs`(`ForJsLevel`, `ForWebSafety`), `src/PellyCoder.Core/Verification/RulesStep.cs`(`CodeOf`: Js → `CodeMask.Js`, Css → `CodeMask.Css`)
- Test: `tests/PellyCoder.Core.Tests/Fsm/WebApplyPhaseTests.cs`, `tests/PellyCoder.Core.Tests/Verification/RulesStepTests.cs`(추가)

**Interfaces:**
- Consumes: Task 1·6·7.
- Produces:
  - `WebApplyPhase(FileGuard fileGuard, FeedbackBuilder feedback, IUserInteraction ui, ITextFileCodec codec) : IPhase` — 상수 `EncodingCheck = "Encoding"`, `SyntaxCheck = "Syntax"`, `LevelCheck = "Level"`, `ShapeCheck = "Shape"`, `SafetyCheck = "Safety"`. 순서(`AspApplyPhase`와 같은 모양): 패치(`MatchFailed`) → 인코딩(`WebEncoding`, 기존 `ForEncoding`) → JS: 문법(`JsSyntaxErrors`, `ForDiagnostics("JS SYNTAX CHECK FAILED", …)`, `MatchFailed`; 대체 모드면 `Syntax`를 `Skipped`+`FallbackReason`로 기록하되 짝 검사 실패는 같은 피드백) → 수준(`JsLevelExceeded`, `state.LevelRejections++`, `ForJsLevel`, `MatchFailed`; 대체 모드면 `Level` `Skipped`) → 모양(`JsTargetChanged`, `ForShapeRejection`, `ShapeRejected`) → 안전(`JsSafetyRejected`, `ForWebSafety`, `SafetyRejected`) / CSS: 구조(`CssStructureErrors`, `ForDiagnostics("CSS STRUCTURE CHECK FAILED", …)`) → 모양(`CssTargetChanged`) → 안전(`CssSafetyRejected`) → 원본과 같으면 `NoChange` → `DryRun`이면 diff, `DryRunCompleted` → `PatchWriter.WriteAsync` → 성공이면 `CurrentSpan` 끝 줄을 줄 수 차이만큼 옮김.
  - `FeedbackBuilder.ForJsLevel(string levelName, string feature, int line, string lineText)` — 머리 `LANGUAGE LEVEL EXCEEDED`, 본문 「This file is written in <ES5>. <화살표 함수(=>)> cannot be used (line N): <줄>」 + 「Rewrite it with <ES5> syntax only.」 + 요청 유지 문구(기존 `KeepRequest`). `ForWebSafety(IReadOnlyList<WebSafetyFinding>)` — 규칙별 한 줄 이유(영어) + 줄 + 문장.
- [ ] **Step 1: 실패 테스트** — `Apply_OutsideRegion_MatchFailed`, `Apply_Unencodable_WebEncoding`, `Apply_JsSyntaxError_Feedback`, `Apply_ArrowInEs5_LevelFeedback_CountsRejection`, `Apply_FunctionRenamed_JsTargetChanged`, `Apply_Eval_SafetyRejected`, `Apply_Fallback_SyntaxSkippedRecorded`, `Apply_CssUnclosed_StructureFeedback`, `Apply_CssSelectorChanged_TargetChanged`, `Apply_CssExpression_SafetyRejected`, `Apply_Unchanged_NoChange`, `Apply_DryRun_NotWritten`, `Apply_Written_SpanShifted`, `Rules_Js_StringsMasked`(금지 패턴이 문자열 안에만 있으면 위반 아님).
- [ ] **Step 2:** 필터 실행 → 실패.
- [ ] **Step 3:** 구현.
- [ ] **Step 4:** 통과.
- [ ] **Step 5:** 커밋 `feat(web): APPLY with encoding, syntax, level, shape and safety checks`

### Task 11: 검증 단계·조립·검토·기록·보고서

**Files:**
- Create: `src/PellyCoder.Core/Verification/JsNamesStep.cs`, `JsApiStep.cs`, `CssPropertyStep.cs`
- Modify: `src/PellyCoder.Core/Fsm/FsmOrchestratorFactory.cs`(`CreateWeb`), `src/PellyCoder.Core/Fsm/Phases/ReviewPhase.cs`, `src/PellyCoder.Core/Logging/RunLogger.cs`(`RunSummary` 끝 `WebSummary? Web = null`), `src/PellyCoder.Core/Reporting/RunReport.cs`·`ReportSanitizer.cs`·보고서 렌더러
- Test: `tests/PellyCoder.Core.Tests/Verification/WebStepsTests.cs`, `tests/PellyCoder.Core.Tests/Fsm/WebOrchestratorTests.cs`, `tests/PellyCoder.Core.Tests/Logging/RunLoggerTests.cs`(추가), `tests/PellyCoder.Core.Tests/Reporting/*`(추가)

**Interfaces:**
- Consumes: Task 1~10.
- Produces:
  - `JsNamesStep(IReadOnlyList<string> globals) : IVerifierStep` — `Name = "Names"`. 대체 모드면 `Record(Skipped, FallbackReason)`. 아니면 `JsNameCheck` → 있으면 `Record(Warned, "fnX(줄 12), y(줄 30)")`, `NameWarnings = 개수`, 없으면 `Passed`. 언제나 `Pass`.
  - `JsApiStep() : IVerifierStep` — `Name = "Api"`, 같은 방식(`ApiWarnings`).
  - `CssPropertyStep() : IVerifierStep` — `Name = "Properties"`(`PropertyWarnings`), 덮어쓰기 알림은 `Record("Override", Warned, …)`.
  - `FsmOrchestratorFactory.CreateWeb(IFileSystem fs, ILlmBackend backend, IUserInteraction ui, PellyOptions options, TimeProvider time, EditKind kind) -> FsmOrchestrator` — `var codec = new WebTextCodec(options.Asp.Encoding); var fileGuard = new FileGuard(fs, time, codec);` 단계: `PreflightPhase`, `kind == Js ? JsLocatePhase : CssLocatePhase`, `WebSlicePhase`, `EditPhase`, `WebApplyPhase`, `VerifyPhase(Js: [RulesStep, JsNamesStep(options.Web.Globals), JsApiStep]; Css: [RulesStep, CssPropertyStep])`, `ReviewPhase`. `kind`가 Js/Css가 아니면 `ArgumentOutOfRangeException`.
  - `ReviewPhase`: `ctx.Web`가 있으면 `ShowDiff` 전에 검사 줄(ASP와 같은 형식, 순서와 표시 이름: `Encoding` 인코딩, `Syntax` 문법, `Level` 문법 수준, `Shape` 대상 모양, `Safety` 안전, `Rules`는 넣지 않음, `Names` 이름, `Api` API, `Properties` 속성, `Override` 덮어쓰기; 기록 없는 것은 생략), `OriginalAscii`이고 최종 텍스트에 0x7F 넘는 문자가 있으면 ASCII 알림, 그리고 고정 문구 `WebNotice`(Global Constraints)를 `ui.Warn`과 `ctx.Record`로(`--yes`여도). ASP 쪽 출력은 바뀌지 않는다(검사 줄 만드는 private 메서드를 `(Dictionary<string, AspCheck>, labels)`를 받게 일반화).
  - `public sealed record WebSummary(string Unit, string Encoding, string? OriginalLevel, string? TargetLevel, string? Jquery, IReadOnlyDictionary<string, string> Checks, int LevelRejections, int NameWarnings, int ApiWarnings, int PropertyWarnings)` — `Unit` `function`/`rule`/`lines`, `Checks` 값 `passed`/`warned`/`skipped`/`failed`. `RunLogger.Summarize`가 `ctx.Web`에서 채움.
  - 보고서: `public sealed record WebCounts(int Runs, int Succeeded, int LevelRejected, IReadOnlyDictionary<string, int> FailureKinds)`, `RunReport`에 `WebCounts? Js = null, WebCounts? Css = null`. 줄(그대로): 「JS: 실행 N건, 성공 K건, 문법 수준 거절 M건, 실패 유형 …」(M = `LevelRejections > 0`인 실행 수), 「CSS: 실행 N건, 성공 K건, 실패 유형 …」. K = 종료 0이고 dry-run 아님(ASP와 같은 정의). 정화본은 개수만, `ProjectStyles`에 `"Web"`.
- [ ] **Step 1: 실패 테스트** — Steps: `Names_Unknown_WarnedAndCounted`, `Names_Fallback_Skipped`, `Api_Warned`, `Properties_UnknownWarned`, `Override_Recorded`, `Steps_NeverFail`. Orchestrator(`MockFileSystem` 작은 사이트 + `ScriptedLlmBackend`): `Js_HappyPath_Exit0_SummaryHasWebBlock`(`kind` = `Js`), `Js_LevelThenFixed_Exit0_LevelRejections1`, `Js_RetriesExhausted_Exit1_RestoredBytes`, `Js_EvalExhausted_Exit5_Restored`, `Js_Rejected_Exit2_Restored`, `Css_HappyPath_Exit0`, `Review_ShowsChecksAndBanner_EvenWithYes`, `Review_AsciiNotice`, `Asp_ReviewUnchanged`(기존 ASP 검사 줄 문자열 그대로). Logger·Report: `Summary_WebBlock`, `Report_JsAndCssLines`, `Sanitize_WebCountsOnly`.
- [ ] **Step 2:** 필터 실행 → 실패.
- [ ] **Step 3:** 구현.
- [ ] **Step 4:** 통과 + Core 테스트 전체 실패 0.
- [ ] **Step 5:** 커밋 `feat(web): verify steps, CreateWeb, review notice, run summary and report lines`

### Task 12: CLI `pelly js`·`pelly css`·doctor·자동완성

**Files:**
- Create: `src/PellyCoder.Cli/Commands/JsCommand.cs`, `CssCommand.cs`
- Modify: `src/PellyCoder.Cli/Commands/RunArguments.cs`(`ValidateJs`, `ValidateCss`, `PrepareWeb`, `JsRequest`, `CssRequest`), `src/PellyCoder.Cli/Composition.cs`(`request.Kind is EditKind.Js or EditKind.Css` → `FsmOrchestratorFactory.CreateWeb(…, request.Kind)`), `src/PellyCoder.Cli/CliApp.cs`(설명 그대로: js 「JavaScript 파일의 함수 하나나 줄 범위를 요청대로 수정합니다.」, css 「CSS 파일의 규칙 하나나 줄 범위를 요청대로 수정합니다.」; 설정 형식 표에 `["js"]`·`["css"]`), `src/PellyCoder.Cli/Completion/CompletionTree.cs`, `src/PellyCoder.Core/Doctor/DoctorService.cs`(+ `DoctorFacts.Web = "JS·CSS"`)
- Test: `tests/PellyCoder.Cli.Tests/Commands/JsCommandTests.cs`, `CssCommandTests.cs`, 기존 자동완성 테스트, `tests/PellyCoder.Core.Tests/Doctor/*`(추가)

**Interfaces:**
- Consumes: Task 11, `AspCommand`의 모양(요청문·취소·종료 처리 그대로).
- Produces:
  - `JsSettings : RunSettings` — `<FILE>`(「수정할 JavaScript 파일 (.js)」), `[REQUEST]`, `--function <NAME>`(「대상 함수 이름, 예: fnSearch, Order.save (--line, --lines와 함께 쓸 수 없음)」), `--line <N>`, `--lines <A-B>`. `CssSettings : RunSettings` — `<FILE>`(「수정할 CSS 파일 (.css)」), `[REQUEST]`, `--selector <SELECTOR>`(「대상 규칙의 선택자, 예: ".list_tbl th" (--line, --lines와 함께 쓸 수 없음)」), `--line`, `--lines`. 검증(종료 4): 「--function, --line, --lines 중 하나만 쓸 수 있습니다」/「--selector, --line, --lines 중 하나만 쓸 수 있습니다」, `--lines` 형식은 ASP와 같은 문구, `--line` ≤ 0 기존 문구, `--selector` 빈 문자열 → 「--selector가 비어 있습니다」.
  - `RunArguments.JsRequest(JsSettings s, string file, string requestText) -> RunRequest` — `MethodName: s.Function`, `Kind: EditKind.Js`. `CssRequest` — `Selector: s.Selector`, `Kind: EditKind.Css`. `PrepareWeb`는 `PrepareAsp`와 같은 준비(파일·설정·작업 공간).
  - doctor: `options.Web.Configured || options.Asp.Configured`일 때 행 하나 「JS·CSS」 — 찾은 jQuery가 있으면 Ok(「jQuery 1.4.2, 1.8.3(기준 1.4.2); jsTarget auto」), 없으면 Warn(「jQuery를 찾지 못함 — jQuery 버전 검사는 건너뜁니다」, 고침 안내 「web.jqueryVersion을 설정하거나 asp.siteRoot를 확인하세요」). 사이트 루트 자체 확인은 ASP 행이 한다.
- [ ] **Step 1: 실패 테스트** — `Js_TwoTargetOptions_Rejected`, `Js_LinesFormat_Rejected`, `Js_PassesRequestToRunner`(`Kind`·`MethodName`·`Lines`), `Js_NonInteractiveNoRequest_Fails`, `Css_TwoTargetOptions_Rejected`, `Css_EmptySelector_Rejected`, `Css_PassesSelector`, 자동완성에 `js`·`css`와 옵션, `Doctor_WebRow_OnlyWhenConfigured`, `Doctor_WebRow_JqueryFound_Ok`, `Doctor_WebRow_NoJquery_Warn`.
- [ ] **Step 2:** 필터 실행 → 실패.
- [ ] **Step 3:** 구현.
- [ ] **Step 4:** 통과 + `dotnet build -warnaserror`, `dotnet test` 전체 실패 0.
- [ ] **Step 5:** 커밋 `feat(cli): pelly js and pelly css commands, doctor row and completion`

### Task 13: 코퍼스와 통합·E2E 테스트

**Files:**
- Create(사이트 루트 `eval/corpus/Asp` 안, 기존 ASP 파일은 건드리지 않는다):
  - `js/common.js` — ES5, CP949(한글 주석·`alert` 문구), `function isEmpty(v)`, `function checkDate(v)`, `function formatNumber(n)`.
  - `js/order/list.js` — ES5, CP949, `var Order = { search: function () {…} }`, `function fnSearch()`(검색 폼 값을 모아 `$.ajax`로 보냄, `checkDate` 사용), `$(function () { $("#btnSearch").click(function () { fnSearch(); }); … })` 안에 이름 없는 처리기 여럿.
  - `js/member/edit.js` — 순수 ASCII, CRLF·LF 섞임, 끝 줄바꿈 없음, `function fnSave()`.
  - `js/legacy/broken.js` — 원본이 해석되지 않는 파일(남는 `}` 한 개), `function oldPopup(url)`.
  - `js/order/modern.js` — 한 줄의 `let`(원본 수준 ES2015 확인용).
  - `lib/jquery-1.8.3.min.js` — 실제 jQuery가 아닌 **머리 주석과 한 줄짜리 가짜**(`/*! jQuery v1.8.3 | (fake header for tests) */` + `window.jQuery = window.$ = function () {};`).
  - `css/admin.css` — CP949 주석, IE 핵(Review Focus 5의 모양), `.list_tbl th`, `@media print { .list_tbl th { … } }`, 벤더 접두 없음.
  - `pelly.json`에 `"web": { "globals": ["gfnOpenPopup"] }` 추가.
- Create: `tests/PellyCoder.Integration.Tests/Web/WebIntegrationTests.cs`, `tests/PellyCoder.Integration.Tests/E2E/WebEndToEndTests.cs`

**Interfaces:**
- Consumes: Task 12, `CannedLlmBackend`, 기존 코퍼스 복사 패턴, `[OllamaFact]`.
- Produces: 코퍼스와 테스트.
- [ ] **Step 1: 테스트 작성**(코퍼스 사본, 정해진 모델 응답):
  - `ListJs_AddStatusParam_Approved_OutsideBytesIdentical` — `list.js --function fnSearch` → 종료 0, 범위 밖 바이트 동일, `summary.json`의 `web.checks.syntax == "passed"`, `web.jquery == "1.8.3"`.
  - `Es5File_ArrowFunction_ThenFixed_Exit0` — 1차 응답에 화살표 함수 → 2차 프롬프트 FEEDBACK에 「LANGUAGE LEVEL EXCEEDED」와 `=>`, 2차 ES5 → 종료 0, `web.levelRejections == 1`.
  - `ModernFile_ArrowAllowed_Exit0` — `modern.js`에 화살표 함수 → 종료 0.
  - `MixedEndingsAsciiJs_KoreanAdded_Cp949_NoticeShown` — `edit.js` 한 줄에 한글 `alert` → 그 줄만 CP949로 바뀜, 다른 줄 끝 그대로, 끝 줄바꿈 없음 유지, 검토 출력에 ASCII 알림.
  - `Eval_Exit5_Restored` — `eval`을 넣는 응답 4번 → 종료 5, 원본 바이트.
  - `InventedFunction_WarnedNotBlocked` — `fnUnknown()` 호출 → 종료 0, 검토 출력에 「이름 경고」와 `fnUnknown`, `gfnOpenPopup`·`checkDate`는 경고 없음.
  - `VendorFile_Refused_Exit3` — `lib/jquery-1.8.3.min.js`.
  - `BrokenJs_FallbackChecks_SkippedShown` — `broken.js --function oldPopup` → 종료 0, `web.checks.syntax == "skipped"`.
  - `Css_SelectorInMediaAndTop_NonInteractive_Exit3`, `Css_IeHacksOriginal_EditPasses`(`.list_tbl th`를 `--line`으로 지정해 높이 변경 → 종료 0, 구조 통과).
  - E2E `[OllamaFact]` `RealModel_ListJs_StatusParam` — 종료 0 또는 1이어도 되지만 범위 밖 바이트 동일, 종료 0이면 문법·수준·안전 통과.
- [ ] **Step 2:** `dotnet test tests/PellyCoder.Integration.Tests -- --filter-class "*WebIntegrationTests"` → 통과(실패하면 원인을 고치고 기록). E2E는 `PELLY_TEST_OLLAMA=http://localhost:11434 PELLY_TEST_MODEL=qwen3.6:35b-a3b-q4_K_M`로 1회. 기존 ASP 통합 테스트(`*AspIntegrationTests`)도 그대로 통과.
- [ ] **Step 3:** 커밋 `test(web): corpus, integration and e2e`

### Task 14: 문서·예비 측정·크기 기록

**Files:**
- Modify: `README.md`(`pelly js`·`pelly css` 절: 사용법, 대상 지정, 판정 표(막는 것·경고만), 문법 수준과 `web.jsTarget`, jQuery 버전, 「브라우저 실행 검증 없음」, 설정 `web.*`과 `asp.siteRoot`·`asp.encoding`을 함께 쓴다는 점), `docs/PRD.md`(§1.4 범위 표에 JS·CSS 줄, §11.1 반입 구성에 Acornima와 `pelly.exe` 크기 변화, §12.2 로드맵 V2-7 행, 새 절 §12.11 「V2-7. 클라이언트 JavaScript·CSS 수정 (pelly js·pelly css)」, 변경 이력 v1.24), `deploy/usage.md`, `deploy/field-results.md`(새 절: 설계 §8 현장 항목), `docs/superpowers/specs/2026-11-04-pelly-js-css-design.md`(이 계획의 「설계와 다른 점」 세 가지 반영, 상태 줄에 「구현됨」은 넣지 않음)
- Create: `eval/benchmark/web/js-001..008.yaml`, `css-001..004.yaml`(`asp-*.yaml`과 같은 형식, `function`·`selector`·`lines` 키; 구성은 설계 §9: JS 함수 대상 3, JS 줄 범위 3, ES5 수준 함정 2, CSS 선택자 2, CSS 줄 범위 2), `eval/benchmark/web/run.sh`(`eval/benchmark/asp/run.sh` 방식), `docs/web-premeasure.md`
- Test: 없음(문서). 측정은 로컬 현장 모델로 12건 각 3회.

- [ ] **Step 1:** 크기: Task 1 이전 커밋과 이 커밋에서 각각 `dotnet publish src/PellyCoder.Cli -c Release -r win-x64 --self-contained -p:PublishSingleFile=true`의 `pelly.exe` 바이트를 재서 PRD §11.1에 「macOS 교차 publish 측정, Windows 값은 CI」로 적는다. 패키지 라이선스 게이트(`deploy/build-package.ps1`)는 CI `package` 잡 결과로 확인하고, 실패하면 멈추고 사람에게 알린다(예외를 임의로 넣지 않는다).
- [ ] **Step 2:** 문서 작성.
- [ ] **Step 3:** 측정: `eval/benchmark/web/run.sh --repeat 3`. 결과를 `docs/web-premeasure.md`에 표로(성공률, 시도 수, 문법 수준 거절 수, 검사별 실패·경고, 시간). 「예비, 현장 측정 별도」 표시.
- [ ] **Step 4:** `dotnet build -warnaserror`, `dotnet test` 전체 실패 0.
- [ ] **Step 5:** 커밋 `docs: pelly js and pelly css in README, PRD, field checks and premeasure`
