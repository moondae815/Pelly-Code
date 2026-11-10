# `pelly js`·`pelly css` — 클라이언트 JavaScript(jQuery)·CSS 수정 설계

작성 2026-11-04. 상태: 설계 초안, 사람 결정 3건 반영(2026-11-04, §8). 사람이 고른 진행 방식은 「질문 없이 바로 초안」이고, 알려 준 사실은 「다른 세션에서 Classic ASP 지원 작업을 진행 중」(2026-11-04) 하나다. 그래서 대상 사이트는 ASP 설계(`asp` 브랜치 `docs/superpowers/specs/2026-11-04-pelly-asp-design.md`)와 같은 **25년 된 백오피스**로 본다(Claude 짐작). 「Claude 결정」·「Claude 짐작」으로 표시한 것은 사람이 아직 확인하지 않은 판단이고, 「사람 결정」은 사람이 정한 것이다. 빌더 확인은 아직 하지 않았다. 2026-11-05: W1 계획(`docs/superpowers/plans/2026-11-04-pelly-js-css.md`)을 실행하며 정한 점을 본문에 반영했다(목록은 §11).

## 1. 왜 하는가

백오피스 화면은 ASP(VBScript) + SP만이 아니라 클라이언트 JavaScript(대개 jQuery)와 CSS로도 되어 있다(Claude 짐작). 그래서 「검색 조건을 추가해 줘」 같은 요청은 SP·ASP·JS(입력 검사, `$.ajax` 매개변수)를 함께 고쳐야 하는 경우가 많다. ASP 설계는 클라이언트 JS·HTML을 **줄 범위 안의 그냥 텍스트**로 고치고 HTML 태그 짝만 검사한다(ASP 설계 §2 비목표). 그러면 JS 오타나 괄호 하나 빠진 수정이 그대로 승인 화면에 올라오고, 운영 브라우저에서야 드러난다.

pelly의 원칙은 「모델이 쓰고, 코드가 판정한다」이다. JS·CSS에는 컴파일러가 없고, 폐쇄망에는 Node·npm·ESLint가 없을 가능성이 크다(Claude 짐작). 그래서 ASP와 같이 **실행 없이, 외부 도구 없이 pelly 안에서 할 수 있는 판정**을 모은다:
- 수정 범위 밖 바이트가 그대로인가(기존 패처, ASP의 바이트 이어 붙이기).
- JS가 문법적으로 해석되는가(.NET JS 해석기를 pelly에 넣는다).
- **원본보다 높은 문법 수준을 쓰지 않았는가**(예: ES5 사이트에 화살표 함수). 이 사이트가 오래된 브라우저(IE·Edge IE 모드)를 대상으로 한다면 가장 흔하고 조용한 고장이다(Claude 짐작).
- 사이트의 jQuery 버전에 없는 API를 쓰지 않았는가(경고).
- 위험한 구문(`eval`, CSS `expression()` 등)을 새로 넣지 않았는가.
- CSS의 블록·문자열·주석 짝이 맞는가.

브라우저 실행 검증은 없다. 결과에 「브라우저에서 확인하지 않음」을 분명히 적고 사람 검토를 반드시 거친다.

## 2. 목표 / 비목표 / 실패 조건

**목표 (1단계 W1 — 따로 있는 `.js`·`.css` 파일)**
1. `pelly js <FILE> "<요청>"`이 `.js` 파일 하나에서 대상 하나(함수 하나, 또는 사람이 준 줄 범위)를 고친다.
2. `pelly css <FILE> "<요청>"`이 `.css` 파일 하나에서 대상 하나(규칙 하나, 또는 줄 범위)를 고친다.
3. 흐름은 `cs`·`sql`·`asp`와 같다: PREFLIGHT → LOCATE → SLICE → EDIT → APPLY → VERIFY → REVIEW, 실패하면 피드백으로 재시도(`retry.max`), 끝내 실패하거나 거부하면 원래 바이트로 되돌린다.
4. 인코딩(CP949·UTF-8, BOM)과 줄 끝을 지키고 편집 영역 밖은 바이트가 같다(ASP 설계 §4.3과 같은 부품).
5. `summary.json`·`pelly report`·`pelly doctor`·자동완성·README·PRD에 반영한다.

**목표 (2단계 W2 — 페이지 안의 `<script>`·`<style>`, 별도 계획)**
6. `.asp`·`.inc`·`.htm`·`.html` 안의 클라이언트 `<script>`·`<style>` 블록을 `pelly js`·`pelly css`로 고친다(§4.8).
7. `pelly asp --lines`로 고친 범위가 클라이언트 블록에 걸치면 그 블록에도 JS·CSS 판정을 더한다(§4.8).

**목표 (3단계 W3 — 나중에 결정)**
8. 명세(`pelly spec`·`apply`)에 `js`·`css` 작업 종류, JS 함수의 이름·매개변수를 바꿀 때 부르는 곳(다른 `.js`, ASP의 `onclick=` 등) 영향 탐색. W1·W2 결과를 보고 정한다. **→ W3a 구현됨(2026-11-10), 설계 `2026-11-10-pelly-web-w3-design.md`. 폼 필드 짝 검사는 W3b.**

**비목표**
- 브라우저 실행 검증, 화면 캡처 비교. 폐쇄망 개발 PC에서 로그인·세션이 필요한 백오피스 페이지를 자동으로 띄우기 어렵다(ASP와 같은 이유, Claude 결정).
- Node·npm·ESLint·Prettier 등 **외부 도구 의존**. 폐쇄망 반입과 「외부 통신 없음」 원칙 때문에 판정 도구는 pelly 실행 파일 안에 들어 있어야 한다(Claude 결정).
- 압축된 파일(`*.min.js`, `*.min.css`)과 외부 라이브러리 파일(jQuery 본체, 플러그인, 부트스트랩 등) 수정. 멈추고 안내한다(§4.1).
- TypeScript, JSX, ES 모듈 번들러, SCSS·LESS. 오래된 백오피스에는 없을 것으로 본다(Claude 짐작). 나오면 확장자로 거절된다.
- 서버 쪽 JScript(ASP 설계의 비목표와 같다).
- 코드 모양 정리(들여쓰기·따옴표 통일). 원본 모양을 따른다.
- XSS 검사(`.html(사용자 입력)`, `innerHTML`). 기존 코드에 흔하고 판정 기준이 현장 규칙에 달려 있다. 필요하면 규칙 파일의 금지 패턴으로 넣는다(ASP와 같은 결정).
- 여러 파일 동시 수정, 새 파일 만들기.
- `pelly ui js`·`pelly ui css`. CLI만 만든다(ASP와 같은 결정).

**실패 조건 (기술적으로 돌아가도 실패로 본다)**
- 수정 범위 밖의 바이트가 바뀐다. 특히 인코딩이 바뀌어 한글(알림 문구, 주석)이 깨진다.
- ES5 사이트에 `let`·`const`·화살표 함수·템플릿 문자열이 들어가 판정을 통과한다. 오래된 브라우저에서는 그 파일 전체가 실행되지 않는데 오류 화면도 없다.
- 실행 검증을 안 했는데 결과가 「검증 통과」처럼 보인다.
- 원본에 이미 있던 문제(해석 오류, 정의를 못 찾는 이름) 때문에 아무것도 못 고친다. 원본의 문제는 기준선으로 빼고 **새로 생긴 문제만** 본다.
- 이름 검사가 정상 코드를 막는다. 페이지에 흩어진 전역 함수를 다 알 수 없으므로 JS 이름 검사는 **경고만** 한다(§4.5).

## 3. 사용 흐름

```
pelly js  js/order/list.js "검색 버튼 누를 때 주문 상태도 넘겨 줘" --function fnSearch
pelly js  js/common.js "날짜 입력 검사에서 빈 값은 통과" --line 214
pelly js  js/member.js "휴대폰 번호 하이픈 자동 입력" --lines 40-95
pelly css css/admin.css "목록 표 머리글을 고정 높이 32px로" --selector ".list_tbl th"
pelly css css/admin.css "버튼 간격 줄이기" --lines 300-340
```

**`pelly js` 대상 지정**
- `--function <이름>`: 그 이름의 함수. 알아보는 모양(Claude 결정):
  - `function 이름(…) {…}` 선언
  - `var|let|const 이름 = function (…) {…}`·화살표 함수
  - `이름: function (…) {…}`, `객체.이름 = function (…) {…}`, `객체.prototype.이름 = function …` — `--function 객체.이름`으로도 쓸 수 있다.
  - 같은 이름이 여럿이면 줄 번호와 함께 선택 화면(비대화형이면 종료 3).
- `--line N`: N번 줄을 감싸는 **가장 바깥이 아닌 가장 안쪽의 이름 있는 함수**. jQuery 코드는 `$(function () { … })`·`$(document).ready(…)` 안에 이름 없는 함수가 겹겹이 있으므로, 이름 없는 함수는 건너뛰고 이름 있는 것을 찾는다. 없으면 멈추고 「`--lines A-B`로 범위를 주십시오」라고 안내한다(Claude 결정 — 이름 없는 `ready` 함수 전체가 대상이 되면 수백 줄이 된다).
- `--lines A-B`: 줄 범위 그대로. 범위의 처음·끝이 문장 중간을 자르면 「줄 A-B 범위가 문장 중간을 자릅니다. 문장 경계로 범위를 주십시오」로 멈춘다(종료 3). jQuery 이벤트 처리기(`$("#btn").click(function () { … })`)는 이름이 없으므로 이 방식이 주력일 것으로 본다(Claude 짐작).
- 셋 다 없으면: 이름 있는 함수가 하나면 그것, 여럿이면 선택 화면(비대화형이면 종료 3). 모델이 고르지 않는다(ASP·SQL과 같은 이유).

**`pelly css` 대상 지정**
- `--selector "<선택자>"`: 선택자 목록이 같은 규칙(공백·쉼표 뒤 공백을 정규화해서 비교, 대소문자 그대로). `a, b { }`는 `a`만 줘도 맞는다. `@media` 안의 규칙도 찾는다. 여럿이면 줄 번호·감싼 `@media`와 함께 선택 화면.
- `--line N`: N번 줄을 감싸는 규칙(가장 안쪽). `@media`·`@font-face` 등 묶음 규칙 자체도 대상이 될 수 있다.
- `--lines A-B`: 줄 범위. 한 규칙 안에 다 들어가는 범위(선언 몇 줄)는 받는다. 범위가 규칙 중간을 자르면(규칙 하나의 일부와 그 밖을 함께 담으면) 「줄 A-B 범위가 규칙 중간을 자릅니다」로 멈춘다(종료 3).
- 셋 다 없으면 「--selector, --line, --lines 중 하나를 주십시오」로 멈춘다(종료 3, `pelly js`와 다름).

그 밖의 옵션(`--dry-run`, `--max-retries`, `--yes`, `--verbose`, `--pause-on-exit`, `--config`, `--model`, `--endpoint`)과 종료 코드는 `pelly asp`와 같다(0 성공, 1 검증 실패, 2 거부·중단, 3 대상 못 찾음·모호, 4 환경, 5 안전 가드).

## 4. 설계

### 4.1 대상 파일

- 확장자: `pelly js`는 `.js`, `pelly css`는 `.css`(대소문자 무시). W2에서 `.asp`·`.inc`·`.htm`·`.html`이 더해진다.
- 멈추는 경우(종료 3, 이유를 한국어로):
  - **압축 파일:** 이름이 `*.min.js`·`*.min.css`이거나, 평균 줄 길이 500자 초과 또는 1,000자 넘는 줄이 전체의 절반 이상(Claude 결정 — 이름이 `.min`이 아닌 압축본도 있다).
  - **외부 라이브러리:** 사이트 루트 기준 경로가 `web.vendorPaths`(기본 `["**/lib/**", "**/plugin*/**", "**/vendor/**", "**/jquery*.js", "**/jquery*.css", "**/bootstrap*"]`, `/` 구분, 대소문자 무시, Claude 결정)에 맞거나, 첫 주석에 `jQuery JavaScript Library`·`@license`·`(c) … | … license` 같은 배포본 머리가 있다. 파일 이름 규칙(`jquery*`, `bootstrap*`)은 별도 목록이 아니라 `web.vendorPaths` 기본값에 넣어 설정으로 뺄 수 있게 했다(계획에서 정함). 경로로 걸리면 「라이브러리 파일은 고치지 않습니다(<패턴> 경로). web.vendorPaths에서 빼면 고칠 수 있습니다」, 머리 주석으로 걸리면 설정과 무관하게 고치지 않는다고 안내한다.
  - **확장자가 맞지 않음:** `pelly js`에 `.js`가 아닌 파일, `pelly css`에 `.css`가 아닌 파일(실패 유형 `WebUnsupportedFile`, 계획에서 더함).
  - 대상 범위가 해석기로 나뉘지 않는다(닫히지 않은 블록이 범위 앞에서 시작 등).

### 4.2 해석기

**JS — `Acornima`(.NET용 ECMAScript 해석기, NuGet)를 넣는다(Claude 결정).**
- 손 해석기를 만들지 않는 이유: JS 문법은 VBScript보다 훨씬 크고(정규식 리터럴과 나눗셈 구분, 자동 세미콜론, 템플릿 문자열), 문법 수준(ES 판)별 판정이 핵심이라 검증된 해석기가 필요하다.
- 고른 이유: 순수 .NET이라 Windows·Mac에서 같게 돌고 단일 파일 publish에 들어간다. `ecmaVersion`을 골라 해석할 수 있어 §4.4의 문법 수준 판정을 그대로 할 수 있다. 오류 위치(줄·열)를 준다. Jint가 쓰는 해석기라 유지되고 있다.
- **반입 패키지에 넣는다(사람 결정 2026-11-04).** 빌더가 확인할 것: 최신 안정 버전, 라이선스(BSD 계열로 알고 있으나 `deploy/` 라이선스 게이트로 확인), .NET 10·단일 파일 publish 동작, `pelly.exe` 크기 증가(PRD §11.1에 기록), `THIRD-PARTY-NOTICES.md` 갱신. 게이트에 걸리면 예외를 임의로 넣지 않고 사람에게 승인을 받는다. 대안은 `Esprima`(.NET판, 같은 계열의 이전 판)다. **확인 결과(2026-11-05):** `Acornima` 1.9.0 버전(`Directory.Packages.props`에 고정), 라이선스 BSD-3-Clause(nuspec `license type="expression"`이라 게이트 허용 목록에 그대로 맞아 예외 불필요, CI `package` 잡이 확인), `pelly.exe` 137,036,927 → 137,615,007바이트(+578,080, macOS 교차 publish, PRD §11.1). NuGet 고지는 SBOM에서 만들어지므로 저장소 `THIRD-PARTY-NOTICES.md`는 바꾸지 않았다.
- 해석은 **script 모드**가 기본이고, 원본에 `import`·`export`가 있으면 module 모드.
- 결과물: 함수 목록(이름·줄 범위·매개변수), 최상위 선언(전역 함수·`var`), 이름이 쓰인 곳(자유 식별자), 해석 오류. 원본의 해석 결과는 기준선으로 저장한다.
- **원본이 해석되지 않을 때**(드물지만 IE 전용 구문, 깨진 파일): 멈추지 않고 §4.5의 구조 검사를 「괄호·문자열·주석 짝 비교」로 낮추고 「JS 문법 검사 건너뜀(원본이 해석되지 않음: 줄 N)」을 보여 준다(Claude 결정 — 실패 조건 「원본 문제 때문에 못 고친다」를 피한다). 이때 판정 표의 문법·문법 수준과 이름·API 경고 단계도 「건너뜀」으로 기록한다(계획 실행 중 정함). 대상 지정은 같은 토큰 수준 해석으로 `function 이름(` 모양만 찾는다.

**CSS — 손 토큰 해석기를 만든다(Claude 결정).**
- CSS Syntax Level 3의 토큰 규칙(문자열, 주석, `url(…)`, `{}`·`()`·`[]` 짝, `;`)과 규칙·선언 구조만 본다. 속성 값의 의미는 해석하지 않는다.
- 라이브러리를 쓰지 않는 이유: 오래된 CSS의 IE 핵(`*zoom: 1`, `_height`, `filter: progid:DXImageTransform…`, `expression(…)`, `\9`)을 기존 CSS 해석 라이브러리들은 버리거나 오류로 본다. 판정에 필요한 것은 구조 짝뿐이라 손 해석기가 작고 정확하다.
- 결과물: 규칙 목록(선택자·줄 범위·감싼 묶음 규칙), 선언 목록(속성 이름·값 원문), 해석 오류. 원본은 기준선.

### 4.3 인코딩과 줄 끝

ASP 설계 §4.3의 부품(인코딩 정하기·엄격한 다시 읽기·**바이트 이어 붙이기 쓰기**·표현할 수 없는 문자 피드백)을 그대로 쓴다. 다른 점만 적는다:
- 인코딩 정하기: BOM → (CSS) 파일 맨 앞의 `@charset "<x>";`(`utf-8` → UTF-8, `euc-kr`·`ks_c_5601-1987`·`cp949`·`windows-949` → CP949, 그 밖은 종료 4 「@charset "<x>"은 지원하지 않습니다」) → ASCII가 아닌 바이트가 있으면 엄격한 UTF-8, 안 되면 CP949 → 순수 ASCII 파일이면 `asp.encoding`(기본 `cp949`). JS·CSS 전용 인코딩 설정은 두지 않는다(사람 결정 2026-11-04 — ASP 설정을 그대로 읽는다, §6).
- JS 파일은 그 파일을 부르는 페이지의 `<script charset>`·`CodePage`를 따라 읽히지만, 파일 하나에서 그 페이지를 알 수 없다. 그래서 순수 ASCII JS 파일에 한글을 넣을 때는 REVIEW에 「이 파일은 ASCII였고 CP949로 저장했습니다」 알림을 보인다(Claude 결정 — 조용히 넘어가면 실패 조건 「한글이 깨진다」에 걸릴 수 있다).

### 4.4 문법 수준과 jQuery 버전

**JS 문법 수준(ES 판):**
- 원본의 수준 = 원본이 해석되는 **가장 낮은** `ecmaVersion`(ES3 → ES5 → ES2015 → … → 최신 순서로 시도, 처음 성공한 판). ES3은 ES5와 구분하지 않고 ES5로 묶는다(Claude 결정 — 차이가 예약어 정도라 실익이 적다).
- 수정된 파일은 `web.jsTarget`으로 해석해야 한다. 기본 `auto` = 원본의 수준(Claude 결정). 그러므로 ES5 파일에 화살표 함수를 넣으면 거절하고 피드백으로 재시도한다. 모델에게 주는 피드백은 다른 피드백과 같이 **영어**이고 기능 이름도 영어(「arrow functions」, 「template literals」 등)다. 한국어 이름(「화살표 함수(=>)」)은 사용자에게 보이는 판정 기록에만 쓴다(계획 실행 중 정함).
- 수준을 넘는 문법의 판은 ES 명세대로 센다. 배열·인수 펼치기(`[...a]`, `f(...a)`)는 ES2015, **객체 펼치기·나머지(`{...a}`, `{...rest}` 패턴)는 ES2018**이다(계획의 「ES2015」를 고침).
- 사이트 전체를 정하고 싶으면 `web.jsTarget: "es5"` 같이 고정한다. 원본이 이미 그보다 높으면 원본 수준을 기준선으로 쓰고 알림 「원본이 <원본 수준> 문법을 써서 web.jsTarget(<설정 수준>) 대신 원본 수준(<원본 수준>)을 기준으로 봅니다」를 보인다(원본 문제로 막지 않는다). 즉 목표 수준은 `auto`면 원본 수준, 아니면 설정과 원본 수준 중 높은 쪽이다. 한국어 메시지는 숫자 바로 뒤에 조사가 오지 않게 쓴다(「ES5로」 대신 「ES5 문법을」 식).
- 문법이 아닌 **API** 수준(`Array.prototype.includes`, `fetch`, `Promise`, `Object.assign` 등 ES5 환경에 없는 내장)은 기본 표로 **경고**만 한다(`.includes`는 문자열·사용자 개체일 수도 있어 확신할 수 없다, Claude 결정).

**jQuery 버전:**
- 사이트 루트(`asp.siteRoot`, 없으면 작업 공간 루트) 아래에서 jQuery 본체를 찾아 버전을 읽는다: 파일 머리 `jQuery JavaScript Library v1.8.3`·`jQuery v1.8.3`, 없으면 파일 이름 `jquery-1.8.3(.min).js`. `web.jqueryVersion`으로 직접 줄 수 있다.
- 버전이 여럿이면(흔하다 — 페이지마다 다른 jQuery) **가장 낮은 버전**을 기준으로 하고 doctor·REVIEW에 목록을 보인다(Claude 결정 — 어느 페이지가 그 JS를 부르는지 W1에서는 모른다).
- 쓰임: 모델 입력 [READ-ONLY: env]에 버전을 넣고, 새로 쓴 jQuery 메서드가 그 버전에 없으면 **경고**. 기본 표는 자주 틀리는 것만 담는다(빌더가 출처를 정함): 1.3 `.closest`·`.live`, 1.4 `.delegate`·`$.parseJSON`, 1.5 `$.Deferred`·`.promise`, 1.6 `.prop`, 1.7 `.on`·`.off`, 1.8 `.addBack`, 3.0 버전에서 없어진 `.size`·`.selector`, 1.9 버전에서 없어진 `.live`·`.die`·`$.browser`. `.bind`·`.unbind`는 3.0 버전에서 **사용 중단 표시만** 됐고 없어지지 않았으므로 표에 넣지 않는다(계획 실행 중 고침). `.load(fn)`도 표에 넣지 않았다(같은 이름의 Ajax `.load(url)`와 구분하지 못함). `.` 뒤 이름만 보므로 다른 개체의 같은 이름 메서드일 수 있어 막지 않는다.

### 4.5 판정 (순서대로, 앞이 실패하면 뒤는 하지 않는다)

모든 검사는 **원본 기준선과 비교해 새로 생긴 것만** 센다. 「새로」는 위치가 아니라 내용으로 센다(같은 구문의 개수가 늘었는가, ASP와 같은 방식).

**공통**
1. **패치 적용**(기존 `SearchReplacePatcher`): 편집 영역 밖 SEARCH, 일치 실패, 변경 없음 → 재시도.
2. **인코딩**(§4.3) → 재시도.
3. **규칙**(기존 `RulesStep`, 프로젝트 규칙의 금지 패턴).

**`pelly js`**
4. **문법 검사**(APPLY에서 쓰기 전에): 수정된 **파일 전체**를 §4.4의 목표 수준으로 해석. 오류는 줄·열과 해석기 메시지(영어 원문 + 짧은 한국어 설명)로 피드백 → 재시도. 원본이 해석되지 않던 파일이면 괄호·문자열·주석 짝 비교로 대신한다(§4.2).
5. **대상 모양**: `--function` 대상이면 수정 뒤에도 그 함수가 하나 있고 이름·매개변수 목록이 같다. 이름·매개변수 변경은 W1에서 막는다(부르는 곳을 함께 고치지 못한다, ASP와 같은 결정). 함수 밖 최상위 선언을 새로 만들지 않는다(줄 범위 대상이면 범위 안에서는 허용).
6. **안전 검사**(새로 생긴 것만, 재시도, 끝내 남으면 종료 5): `eval(…)`, `new Function(…)`·`Function(…)`, 첫 인수가 문자열 리터럴·문자열 연결·템플릿 문자열인 `setTimeout`·`setInterval`(`JsStringTimer`), `document.write`의 추가(오래된 코드에 흔하지만 새로 늘리지 않는다, Claude 결정), `execScript`, `ActiveXObject`.
7. **이름 검사(경고만)**: 수정으로 새로 쓰인 자유 식별자 중 다음 어디에도 없는 것을 REVIEW에 경고로 보인다.
   - JS 내장·브라우저 전역 기본 목록(`window`, `document`, `alert`, `JSON` …), `$`·`jQuery`(jQuery를 찾았을 때).
   - 파일 안의 선언(감싸는 함수의 매개변수·지역 변수 포함).
   - **사이트 색인:** 사이트 루트 아래 라이브러리가 아닌 `.js` 파일들의 최상위 함수·`var` 이름(실행마다 다시 만들지 않고 `.pelly/cache/`에 파일 해시와 함께 저장, Claude 결정).
   - `web.globals` 설정(현장 공용 전역 이름).
   - 막지 않는 이유: 전역 함수가 페이지 안 `<script>`나 ASP가 출력한 코드에 있을 수 있어 「없다」를 확신할 수 없다.
8. **API 수준 경고**: §4.4의 ES API 표와 jQuery 버전 표.

**`pelly css`**
4. **구조 검사**: `{}`·`()`·`[]` 짝, 닫히지 않은 문자열·주석, 선언 끝 `;` 빠짐(다음 선언과 붙어 버린 경우), 규칙 밖 선언. 피드백 → 재시도.
5. **대상 모양**: `--selector` 대상이면 수정 뒤에도 그 선택자의 규칙이 하나 있다. 선택자 변경은 막는다(HTML·JS의 다른 곳이 그 이름을 쓴다, Claude 결정).
6. **안전 검사**(새로 생긴 것만, 종료 5): `expression(…)`, `behavior:`(`.htc`), `-moz-binding`, `javascript:` URL.
7. **속성 이름 경고**: 알려진 CSS 속성 표(빌더가 출처를 정함)에 없는 속성 이름. 벤더 접두사(`-webkit-` 등)와 IE 핵 모양(`*속성`, `_속성`, `filter`)은 원본에 이미 쓰고 있으면 경고하지 않는다.
8. **덮어쓰기 알림**: 새로 쓴 선언이 같은 파일 안 같은 선택자의 뒤쪽 규칙에 의해 덮일 때(같은 속성, 같은 `@media` 조건) 알림. 다른 파일과의 순서는 W1에서 모른다.

**REVIEW:** diff와 함께 판정 표(검사별 통과·경고·건너뜀)와 고정 문구 「JS·CSS는 브라우저에서 실행해 확인하지 않았습니다. 화면에서 직접 확인하십시오. 브라우저 캐시 때문에 바뀐 파일이 바로 반영되지 않을 수 있습니다.」를 보인다(Claude 결정 — 오래된 사이트는 `?v=` 캐시 무효화가 없을 수 있다). `--yes`이면 묻지 않고 승인하되 이 문구는 실행 기록과 `summary.json`에 남긴다.

### 4.6 모델 입력

형식은 `sql`·`asp`와 같은 SEARCH/REPLACE 블록. 새 프롬프트 `system.js.txt`·`fewshot.js.txt`(판 `js-v1`), `system.css.txt`·`fewshot.css.txt`(판 `css-v1`). 예산은 `budget.*` 그대로.

**`pelly js`** (앞이 우선, 예산이 모자라면 뒤에서부터 뺀다)
1. **[EDITABLE: target]** 대상 범위.
2. **[READ-ONLY: env]** 문법 수준(예: `ES5 — let/const/화살표 함수/템플릿 문자열/클래스 금지`), jQuery 버전, 파일 인코딩.
3. **[READ-ONLY: callees]** 대상이 부르는 함수의 선언 줄과 바로 위 주석(같은 파일 + 사이트 색인). 본문은 넣지 않는다.
4. **[READ-ONLY: file]** 파일의 최상위 선언 목록(이름·매개변수).
5. **[READ-ONLY: around]** 대상 앞뒤 10줄.

**`pelly css`**
1. **[EDITABLE: target]**
2. **[READ-ONLY: env]** 인코딩, 원본이 쓰는 IE 핵·벤더 접두사 유무(「원본 모양을 따르라」 힌트).
3. **[READ-ONLY: related]** 같은 파일에서 대상 선택자와 같은 선택자를 쓰는 다른 규칙(덮어쓰기 판단용).
4. **[READ-ONLY: around]** 앞뒤 10줄.

시스템 프롬프트의 고정 규칙(요지): 대상 밖을 SEARCH에 쓰지 않는다 / [READ-ONLY: env]의 문법 수준·jQuery 버전 안에서만 쓴다 / 없는 함수를 지어내지 말고 [READ-ONLY: callees]·[READ-ONLY: file]과 내장만 쓴다 / 함수 이름·매개변수, CSS 선택자를 바꾸지 않는다 / `eval`·문자열 `setTimeout`을 쓰지 않는다 / 원본의 들여쓰기·따옴표·세미콜론 모양을 따른다.

### 4.7 결과와 기록

- `EditKind.Js`, `EditKind.Css`. `summary.json`에 `web` 블록: 대상 단위, 인코딩, 원본·목표 문법 수준, jQuery 기준 버전, 검사별 결과(통과·경고·건너뜀), 경고 개수(이름·API·속성).
- `pelly report` 한 줄씩: 「JS: 실행 N건, 성공 K건, 문법 수준 거절 M건 …」, 「CSS: …」.
- 새 실패 유형: `JsSyntaxErrors`, `JsLevelExceeded`, `JsSafetyRejected`, `JsTargetChanged`, `CssStructureErrors`, `CssSafetyRejected`, `CssTargetChanged`, `WebVendorFile`, `WebMinifiedFile`, `WebUnsupportedFile`(확장자가 맞지 않음), `WebEncoding`(파일 인코딩으로 쓸 수 없는 문자 — ASP의 `AspEncoding`은 바꾸지 않고 따로 둠). 뒤의 둘은 계획에서 더했다.
- `summary.json`의 `web` 블록 키는 camelCase(`unit`, `encoding`, `originalLevel`, `targetLevel`, `jquery`, `checks`, `levelRejections`, `nameWarnings`, `apiWarnings`, `propertyWarnings`)이고, `checks` 안의 검사 이름은 ASP 블록과 같이 PascalCase(`Encoding`, `Syntax`, `Level`, `Shape`, `Safety`, `Names`, `Api`, `Properties`, `Override`)다. CSS 구조 검사도 `Syntax`로 기록한다.
- `pelly doctor` 행: 「JS·CSS」 — 찾은 jQuery 버전 목록과 기준 버전, `web.jsTarget`(설정에 `web`이나 `asp` 절이 있을 때, jQuery를 못 찾으면 Warn). 사이트 루트 자체는 ASP 행이 본다. **사이트 색인 파일 수는 넣지 않는다** — doctor를 실행할 때마다 색인을 만들게 되기 때문이다(계획에서 정함).

### 4.8 2단계 W2 — 페이지 안의 `<script>`·`<style>` (별도 계획)

- ASP 페이지 해석기(ASP 설계 §4.2)에 **클라이언트 블록 구간**을 더한다: `runat="server"`가 없는 `<script>`(형식이 없거나 `text/javascript`·`javascript`인 것만, `text/template` 등은 제외)와 `<style>`. 지금 ASP 해석기는 이것을 HTML 구간으로 본다 — **ASP 세션과 맞출 것**(§6).
- `pelly js`·`pelly css`가 `.asp`·`.inc`·`.htm`·`.html`을 받는다. 대상(함수·규칙·줄 범위)이 **클라이언트 블록 하나 안에 완전히** 들어 있어야 한다. 걸치면 「`pelly asp --lines`를 쓰십시오」로 멈춘다. 쓰기·인코딩은 ASP 부품, ASP 구조 검사는 「바뀌지 않음」 확인만 한다.
- **블록 안의 ASP 코드:** 클라이언트 JS 안에 `<%= … %>`가 흔하다(`var no = '<%= orderNo %>';`, `var cnt = <%= cnt %>;`). JS 판정 전에 각 `<%= … %>`를 같은 줄에 머무는 자리 표시 이름(`__asp0__` 등)으로 바꿔 해석한다 — 문자열 안이면 문자열 일부로, 밖이면 식별자로 해석된다. `<% … %>` **문장 블록**(`<% If … %>`)이 블록 안에 있으면 JS 판정은 「건너뜀(서버 코드가 섞인 스크립트)」으로 하고 구조 짝 비교만 한다(Claude 결정 — 분기마다 다른 JS가 나와 한 번에 해석할 수 없다). 자리 표시 이름은 이름 검사에서 뺀다.
- 페이지 안 블록의 문법 수준 기준선은 그 블록, jQuery 버전은 그 페이지가 `<script src>`로 부르는 jQuery 파일(찾으면)이다. 이것이 W1보다 정확하다.
- `pelly asp --lines` 범위가 클라이언트 블록에 걸치면 그 블록 전체에 위 JS·CSS 판정을 더한다(ASP 판정 표에 「JS 문법」·「CSS 구조」 행 추가).
- JS 이름 검사에 페이지 안 `<script>` 블록의 최상위 선언과 그 페이지가 부르는 `.js` 파일의 선언을 더한다.

### 4.9 3단계 W3 (메모만)

- 명세 작업 `kind: js`·`kind: css`.
- JS 함수 이름·매개변수 변경 + 부르는 곳(`.js`, 페이지 `<script>`, `onclick="fnX(…)"` 같은 HTML 속성) 함께 고치기.
- CSS 선택자 변경 + 그 클래스·id를 쓰는 HTML·JS 찾기(문자열 수준).
- `spec check --impact`: 바뀐 ASP 페이지의 폼 필드 이름(`Request.Form("x")`)과 JS가 보내는 이름(`$.ajax data`, `name="x"`)이 어긋나는지 경고 — 백오피스 요청에서 가장 자주 빠지는 짝일 것으로 본다(Claude 짐작).

## 5. 바꾸는 곳 (짐작 — 계획에서 확정)

- Core 새 폴더 `Web/`(또는 `Js/`·`Css/` 둘): JS 해석 감싸기(Acornima), 문법 수준 판정, 함수·이름 수집, 사이트 색인, jQuery 버전 찾기·API 표, CSS 토큰·구조 해석기, 속성 표, 안전 검사, 압축·라이브러리 파일 판정.
- Fsm: `JsLocatePhase`·`CssLocatePhase`, SLICE, APPLY(문법·구조·안전 검사 후 쓰기 — ASP의 `AspApplyPhase` 모양을 따른다), 검증 단계 `JsNamesStep`·`JsApiStep`·`CssPropertyStep`, `FsmOrchestratorFactory.CreateJs`·`CreateCss`.
- 모델·설정: `EditKind.Js`·`Css`, `PellyOptions.Web`(`jsTarget`, `jqueryVersion`, `globals`, `vendorPaths`), `FailureKinds`.
- 공용으로 끌어올리기(ASP 쪽 코드): 인코딩 정하기와 바이트 이어 붙이기 쓰기, 사이트 루트 해석. §6 참고.
- 프롬프트: `system.js.txt`, `fewshot.js.txt`, `system.css.txt`, `fewshot.css.txt`, `PromptResources`.
- CLI: `Commands/JsCommand.cs`·`CssCommand.cs`, `RunArguments`(`--function`, `--selector`, `--lines`), Runners, Composition, CliApp, PowerShell 자동완성.
- 패키지: `Directory.Packages.props`에 Acornima, 라이선스 게이트, `THIRD-PARTY-NOTICES.md`, `deploy/package-files.txt`는 단일 파일에 들어가므로 바뀌지 않을 것으로 짐작.
- 기록: `RunLogger`·`RunReport`·`ReportSanitizer`, doctor 행.
- 문서: README, PRD(§1.4 범위 표에 JS·CSS 줄, §12에 **V2-7** — ASP가 V2-6을 쓴다고 보고), `deploy/usage.md`, `deploy/field-results.md` 새 절.
- 코퍼스는 `eval/corpus/Web/`이 아니라 **기존 `eval/corpus/Asp/` 사이트 안**(`js/`, `css/`, `lib/`)에 둔다 — 같은 사이트여야 W2(페이지 안 블록)에서 그대로 쓸 수 있다(계획에서 정함). 담는 것: ES5 `common.js`(날짜·숫자 검사 함수), 페이지별 JS(이름 없는 `$(function(){})` 안 처리기 포함), CP949 한글 알림 문구 파일, 순수 ASCII 파일, 원본이 해석되지 않는 파일 하나, IE 핵이 섞인 `admin.css`, `@media` 안 규칙, jQuery 버전 찾기용 **머리 주석만 있는 가짜** `lib/jquery-1.8.3.min.js`(실제 jQuery를 넣지 않는다).

## 6. Classic ASP 작업과의 관계

ASP 설계(`asp` 브랜치, A1 구현 중 — 2026-11-04 기준 해석기·LOCATE·안전·이름 검사까지 커밋됨)와 겹치는 곳:

- **순서:** W1은 A1이 main에 들어간 뒤 시작한다(사람 결정 2026-11-04). 인코딩·바이트 이어 붙이기를 ASP 쪽에서 가져다 써야 하고, 같은 파일(`EditKind`, `PellyOptions`, `CliApp`, `Composition`, 자동완성, README·PRD 표, `FailureKinds`)을 두 브랜치가 동시에 고치면 충돌이 크다.
- **공용으로 옮길 것:** ASP의 인코딩 정하기·바이트 이어 붙이기 쓰기, 사이트 루트. 설정은 **ASP 것을 그대로 읽는다**(사람 결정 2026-11-04): JS·CSS도 `asp.siteRoot`·`asp.encoding`을 쓰고 `site.*` 같은 공용 설정은 만들지 않는다. 그래서 ASP 세션에 요청할 설정 변경은 없다. 코드 부품(인코딩 정하기·바이트 이어 붙이기)은 A1 병합 뒤 W1에서 ASP 전용 폴더 밖으로 옮길지 빌더가 정한다. README·doctor에는 「`asp.siteRoot`·`asp.encoding`은 JS·CSS에도 쓰인다」를 적는다.
- **ASP 해석기 확장(W2):** 클라이언트 `<script>`·`<style>` 구간을 HTML과 구분해야 한다. ASP A1 범위에 넣을지, W2에서 더할지 ASP 세션과 정한다. 넣는다면 구간 종류 하나를 더하는 일이고 A1 동작은 바뀌지 않는다.
- **비목표 조정:** ASP 설계 §2의 「클라이언트 쪽 JavaScript·HTML은 줄 범위 안에서 그냥 텍스트로 고칠 수 있고, HTML 태그 짝 검사만 한다」는 W2가 들어가면 「… JS·CSS 판정을 더한다(V2-7 W2)」로 바뀐다.
- **PRD 번호:** ASP를 V2-6으로 본다. ASP 쪽이 다른 번호를 쓰면 이 문서의 V2-7을 맞춘다.

## 7. 검증 (pelly 자체 테스트)

- JS 해석: 함수 모양별 찾기(선언, `var = function`, 객체 속성, prototype, 화살표), `--line`이 이름 없는 jQuery 함수를 건너뜀, 범위가 문장 중간을 자르면 종료 3.
- 문법 수준: ES5 원본 + 화살표 함수·`let`·템플릿 문자열·클래스 추가 → 거절, ES2015 원본 + 화살표 → 통과, `web.jsTarget` 고정, 원본이 목표보다 높을 때 기준선 처리, 원본이 해석되지 않을 때 짝 비교로 낮춤.
- jQuery: 머리 주석·파일 이름에서 버전 찾기, 여러 버전 → 가장 낮은 것, 표에 있는 메서드 경고.
- 안전: 원본에 있던 `eval`은 통과, 새 `eval`·문자열 `setTimeout`·`expression()`은 거절.
- 이름(경고): 사이트 색인 이름은 경고 없음, 모르는 이름은 경고, 색인 캐시가 파일 변경 뒤 갱신.
- CSS: 구조 오류(괄호, 문자열, `;` 빠짐), IE 핵이 섞인 원본을 기준선으로 통과, `--selector` 정규화·`@media` 안 찾기·여럿일 때 선택, 선택자 변경 거절, 덮어쓰기 알림.
- 파일 판정: `.min.js`, 긴 줄 압축본, 라이브러리 머리 주석, `web.vendorPaths`.
- 인코딩·쓰기: ASP 부품 테스트에 JS·CSS 경우(`@charset`, ASCII JS + 한글 알림) 추가.
- CLI·통합: 가짜 모델로 성공·재시도·거부·되돌림.
- 게이트: `dotnet build -warnaserror` 종료 0, `dotnet test` 실패 0, 패키지 라이선스 게이트 통과.

## 8. 사람이 확인할 것

**결정 (2026-11-04 사람 결정)**
1. 사이트 설정: ASP 설정(`asp.siteRoot`·`asp.encoding`)을 그대로 읽는다. 공용 `site.*`는 만들지 않는다(§4.3·§6).
2. Acornima를 반입 패키지에 넣는다(§4.2). 라이선스 게이트에 걸리면 그때 예외 승인을 따로 받는다.
3. W1은 A1이 main에 병합된 뒤 시작한다(§6·§9).

**현장 (`deploy/field-results.md` 새 절)**
- 대상 브라우저: 백오피스를 IE·Edge IE 모드로 쓰는가, 최신 Edge·Chrome인가. → `web.jsTarget` 기본값(지금은 `auto` = 원본 수준). IE 모드라면 ES API 경고를 오류로 올릴지 검토.
- jQuery 버전(들)과 위치, 페이지마다 다른 버전을 쓰는지.
- JS·CSS 파일의 인코딩(CP949·UTF-8 섞임), 순수 ASCII 파일 비율.
- 공용 JS(`common.js` 류)와 전역 함수 이름 규칙 → `web.globals`.
- 라이브러리·플러그인 폴더 위치 → `web.vendorPaths`.
- JS·CSS 수정이 대부분 따로 있는 `.js`·`.css` 파일에서 일어나는가, 페이지 안 `<script>`에서 일어나는가 → W2를 얼마나 앞당길지.
- 실제 요청 3건(JS 2, CSS 1)을 돌린 결과와 사람이 화면에서 확인했을 때 문제가 있었는지.

## 9. 단계와 순서

1. **W1 `pelly js`·`pelly css`** — 이 문서 §4.1–§4.7. A1 병합 뒤 시작(사람 결정). 계획 하나, 구현, 예비 측정(벤치마크 `eval/benchmark/web/js-001..008.yaml`·`css-001..004.yaml`과 실행기 `run.sh` — ASP와 같이 기존 50건 집합인 `tasks/`에 섞지 않음, 12건, Claude 결정: JS 함수 대상 3, JS 줄 범위 3, ES5 수준 함정 2, CSS 선택자 2, CSS 줄 범위 2; 기록 `docs/web-premeasure.md`, KPI 판정에는 쓰지 않음).
2. **W2 페이지 안 블록** — §4.8. A1 병합과 현장 답을 보고 계획한다.
3. **W3 명세·영향** — §4.9. W1·W2 결과와 A2(ASP + SP 명세) 결과를 보고 정한다.

## 10. 빌더에게 맡기는 것

- Acornima 버전·설정(오류 복구 여부, 위치 정보 옵션)과 감싸는 자료형. 게이트에 걸리면 대안 검토 후 사람에게 보고.
- CSS 토큰 해석기의 내부 구조, 오류 메시지 문구(한국어).
- 브라우저 전역 목록, ES API 표, jQuery 버전 표, CSS 속성 표의 출처와 형식(코드 안 표 또는 리소스 파일).
- 압축 파일 판정 기준값(줄 길이), `[READ-ONLY: around]` 줄 수, 사이트 색인 캐시 형식 — 위 값은 출발점.
- `cs`·`sql`·`asp` 부품 중 재사용할 것과 새로 만들 것의 경계(`SearchReplacePatcher`, `FileGuard`, `FeedbackBuilder`, ASP의 쓰기 부품은 그대로 쓸 것으로 짐작).

## 11. 구현하며 바뀐 점 (2026-11-05)

W1 계획의 「설계와 다른 점」과 계획 실행 중 정한 점이다. 본문의 해당 절도 고쳤다.

1. 실패 유형 `WebUnsupportedFile`(확장자가 맞지 않음)과 `WebEncoding`(ASP의 `AspEncoding` 대신)을 더했다(§4.1·§4.7).
2. 이름 규칙의 라이브러리 판정(`jquery*`, `bootstrap*`)을 `web.vendorPaths` 기본값에 넣어 설정으로 뺄 수 있게 했다(§4.1).
3. doctor 「JS·CSS」 행에 사이트 색인 파일 수를 넣지 않는다(§4.7).
4. 코퍼스는 `eval/corpus/Asp/` 사이트 안(`js/`, `css/`, `lib/`)에 둔다(§5). 벤치마크는 `eval/benchmark/web/`에 둔다(§9).
5. 객체 펼치기·나머지(`{...a}`)는 ES2018 문법으로 센다. 배열·인수 펼치기는 ES2015 그대로다(§4.4).
6. jQuery `.bind`·`.unbind`는 「없어진 것」 표에서 뺐다. 3.0 버전에서 사용 중단 표시만 됐다(§4.4).
7. CSS `--lines`는 한 규칙 안에 다 들어가는 범위(선언 몇 줄)를 받는다. 규칙 중간을 자르는 범위만 거절한다(§3).
8. 한국어 메시지는 숫자 바로 뒤에 조사가 오지 않게 썼다: 수준 알림 「원본이 <원본 수준> 문법을 써서 web.jsTarget(<설정 수준>) 대신 원본 수준(<원본 수준>)을 기준으로 봅니다」, JS 범위 「줄 A-B 범위가 문장 중간을 자릅니다. 문장 경계로 범위를 주십시오」, CSS 범위 「줄 A-B 범위가 규칙 중간을 자릅니다」(§3·§4.4).
9. 문법 수준 위반의 모델 피드백은 영어이고 영어 기능 이름(「arrow functions」)을 쓴다. 한국어 이름은 사용자에게 보이는 기록에만 쓴다(§4.4).
10. `setTimeout`·`setInterval`에 넘긴 템플릿 문자열도 문자열 타이머(`JsStringTimer`)로 센다(§4.5).
11. 원본 JS가 해석되지 않는 경우 이름·API 경고 단계도 문법·문법 수준 검사와 같이 「건너뜀」으로 기록한다(§4.2).
12. `summary.json` `web` 블록의 `checks` 키는 ASP 블록과 같이 PascalCase(`Syntax`, `Level`, …)다(§4.7).
13. 검사 결과는 ASP와 같은 `AspCheck`·`AspCheckStatus` 자료형을 그대로 쓴다(이름을 바꾸면 ASP 코드를 건드리게 되어서, Claude 결정).
