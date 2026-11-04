# `pelly asp` — Classic ASP(VBScript) 페이지 수정 설계

작성 2026-11-04. 상태: 설계, 사람 확인 전. 사람이 알려 준 사실은 하나다: 「25년 전에 구축된 백오피스 사이트가 아직 운영 중이고, 업무 로직뿐 아니라 사이트 전체가 ASP + SP로 되어 있다」(2026-11-04). 설계는 「질문 없이 바로 설계」를 골랐다. 「Claude 결정」으로 표시한 것은 사람이 아직 확인하지 않은 판단이다. 설계 검토(빌더 확인) 반영: 인코딩 판정, 줄 끝 섞인 파일, SQL 문자열 규칙, 이름 변경, DB 연결.

## 1. 왜 하는가

pelly는 지금 C#(`pelly cs`·`pelly test`)과 SQL(`pelly sql`)만 고친다(`EditKind { CSharp, Sql, CSharpTest }`). 현장 백오피스는 화면·업무 로직이 Classic ASP(VBScript)이고 데이터는 SP다. 그래서 백오피스 요청은 대개 「SP를 고치고, 그 SP를 부르는 ASP 페이지도 고친다」가 한 묶음이다. 지금은 SP 쪽만 pelly가 돕고 ASP 쪽은 사람이 손으로 고친다.

pelly의 원칙은 「모델이 쓰고, 코드가 판정한다」이다. C#은 컴파일러와 테스트가 판정해 주지만 ASP에는 둘 다 없다. 컴파일 단계가 없어 오타·없는 함수 호출이 운영에서 실행될 때 드러나고, 테스트 도구도 없고, 실행하려면 IIS가 필요하다. 이 설계의 핵심은 **실행 없이 코드로 할 수 있는 판정을 최대한 모으는 것**이다:
- 수정 범위 밖 바이트가 그대로인가(기존 패처).
- `<% %>`·VBScript 블록(`If … End If` 등)의 짝이 맞는가(새로 만드는 해석기).
- VBScript가 컴파일되는가(Windows 내장 `cscript`로 실행하지 않고 컴파일만).
- 모델이 없는 함수·변수를 지어내지 않았는가(이름 검사).
- 위험한 구문(SQL 문자열에 `Request` 값을 바로 붙이기, `WScript.Shell` 등)을 새로 넣지 않았는가(안전 검사).
- 부르는 SP가 DB에 실제로 있는가(SQL 연결이 있을 때).

그래도 실행 검증은 없다. 그래서 결과에 「실행 검증 없음」을 분명히 적고, 사람 검토를 반드시 거친다.

## 2. 목표 / 비목표 / 실패 조건

**목표 (1단계 A1)**
1. `pelly asp <FILE> "<요청>"`이 `.asp`·`.inc` 파일 하나에서 대상 하나(VBScript `Function`·`Sub` 하나, 또는 사람이 준 줄 범위)를 모델로 고친다. 흐름은 `cs`·`sql`과 같다: PREFLIGHT → LOCATE → SLICE → EDIT → APPLY → VERIFY → REVIEW, 실패하면 피드백으로 재시도(`retry.max`), 끝내 실패하거나 거부하면 원래 바이트로 되돌린다.
2. 파일 인코딩(CP949·UTF-8, BOM 유무)과 줄 끝을 그대로 지킨다. 편집 영역 밖은 바이트가 같다(줄 끝이 섞인 파일 포함, §4.3). 25년 된 사이트는 대부분 CP949다.
3. §4.5의 판정을 통과한 수정만 사람에게 보여 준다. 판정할 수 없던 단계(예: Mac에서 `cscript`)는 「건너뜀」으로 분명히 표시한다.
4. `summary.json`·`pelly report`·`pelly doctor`·자동완성·README·PRD에 반영한다.

**목표 (2단계 A2, 별도 계획)**
5. 명세(`pelly spec`·`apply`)에 `asp` 작업 종류를 더해 「SP 변경 + 그 SP를 부르는 ASP 수정」을 한 명세로 순서대로 실행한다.
6. `spec check --impact`가 SP를 바꾸는 작업에 대해 그 SP를 부르는 ASP 페이지(`.asp`·`.inc`의 문자열)를 찾아 알린다.

**비목표**
- 실행 검증. IIS·브라우저로 페이지를 띄워 확인하지 않는다. 폐쇄망 개발 PC에 IIS와 운영과 같은 DB 연결이 있는지 현장마다 다르고, 백오피스 페이지는 로그인·세션이 필요해 자동 실행이 어렵다(Claude 결정).
- 서버 쪽 JScript(`<script language="JScript" runat="server">`, `<%@ Language="JScript" %>`). 대상 파일에 있으면 멈추고 안내한다. 클라이언트 쪽 JavaScript·HTML은 줄 범위 안에서 그냥 텍스트로 고칠 수 있고, HTML 태그 짝 검사만 한다.
- `global.asa`. 사이트 전체의 시작·세션 이벤트라 실수 비용이 크다(Claude 결정).
- 여러 파일 동시 수정, 새 파일 만들기, include 추가·삭제, `<%@ %>` 지시문 변경. 한 실행은 한 파일의 한 대상만 바꾼다.
- COM 구성 요소(VB6 DLL 등) 수정. 소스가 없거나 다른 언어다.
- ASP.NET 등으로 옮기기(마이그레이션).
- XSS 검사(`Response.Write Request(...)`). 기존 코드에 너무 흔하고 판정 기준이 현장 규칙에 달려 있다. 필요하면 규칙 파일의 금지 패턴으로 넣는다(Claude 결정).
- `pelly ui asp`. CLI만 만든다. 결과를 보고 나중에 정한다(Claude 결정).

**실패 조건 (기술적으로 돌아가도 실패로 본다)**
- 수정 범위 밖의 바이트가 바뀐다. 특히 인코딩이 바뀌어 한글이 깨진다.
- 실행 검증을 안 했는데 결과가 「검증 통과」처럼 보인다.
- 원래 코드에 이미 있던 문제(짝 안 맞는 태그, 정의를 못 찾는 이름) 때문에 아무것도 못 고친다. 원본의 문제는 기준선으로 빼고 **새로 생긴 문제만** 본다.
- 이름 검사가 정상 코드를 자꾸 막아 재시도를 다 써 버린다. 확신할 수 없을 때는 막지 않고 경고로 보여 준다(§4.5.5).

## 3. 사용 흐름

```
pelly asp admin/order/list.asp "검색 조건에 주문 상태를 추가해 줘" --lines 120-185
pelly asp admin/inc/func_order.inc "취소 주문은 금액을 0으로 표시" --proc GetOrderAmount
pelly asp admin/member/edit.asp "휴대폰 번호 저장 전에 하이픈 제거" --line 88
```

- `--proc <이름>`: 그 이름의 `Function`·`Sub`(파일 안, 대소문자 무시). `Class` 안의 메서드는 `클래스.이름`.
- `--line N`: N번 줄을 감싸는 `Function`·`Sub`. 감싸는 것이 없으면(페이지 본문 코드) 멈추고 「`--lines A-B`로 범위를 주십시오」라고 안내한다.
- `--lines A-B`: 줄 범위 그대로(HTML·`<% %>` 섞여도 됨). 백오피스 페이지는 함수 없이 페이지 본문에 코드가 이어진 경우가 많아 이 방식이 주력일 것으로 본다(Claude 짐작).
- 셋 다 없으면: 파일에 `Function`·`Sub`가 하나면 그것, 여럿이면 선택 화면(비대화형이면 종료 3). 모델이 고르지 않는다(SQL과 같은 이유 — 틀리게 골라도 판정으로 잡을 수 없다).
- 그 밖의 옵션(`--dry-run`, `--max-retries`, `--yes`가 있다면 그 동작 등)은 `pelly sql`과 같다. 종료 코드도 같다(0 성공, 1 검증 실패, 2 거부·중단, 3 대상 못 찾음·모호, 4 환경, 5 안전 가드).

## 4. 설계

### 4.1 대상

- 확장자: `.asp`, `.inc`(대소문자 무시). `asp.extensions` 설정으로 더할 수 있다(예: `.asa`는 넣지 않는 것이 기본, `.html`에 ASP를 쓰는 사이트용).
- 대상 단위 둘:
  - **프로시저:** `Function`·`Sub`(앞의 `Public`·`Private` 포함)부터 `End Function`·`End Sub`까지, 바로 위의 주석 줄 포함. `Property Get/Let/Set`도 같은 취급.
  - **줄 범위:** `--lines A-B` 그대로. 상한은 따로 두지 않고 문맥 예산이 정한다(넘으면 `BudgetExceeded`, 종료 4 — `sql`과 같다).
- 멈추는 경우(종료 3, 이유를 한국어로):
  - 파일 언어가 VBScript가 아니다(`<%@ Language=JScript %>`, 또는 대상 범위에 서버 JScript 블록).
  - 대상이 지시문(`<%@ %>`)이나 include 줄에 걸친다. 줄 범위 안에 있어도 그 줄들은 [READ-ONLY]로 두고 편집 영역에서 뺀다(Claude 결정 — 범위를 거절하지 않고 그 줄만 잠근다).
  - 해석기가 대상 범위를 해석하지 못한다(예: 닫히지 않은 `<%`가 범위 앞에서 시작해 끝까지 감).

### 4.2 ASP 해석기 (새로 만든다)

정규식 몇 개가 아니라 작은 손 해석기 둘을 둔다(Claude 결정 — 문자열 안의 `%>`, 주석 안의 `End If` 때문에 정규식으로는 틀린다).

1. **페이지 해석:** 파일을 구간으로 나눈다 — HTML, `<% … %>`(코드), `<%= … %>`(출력식), `<%@ … %>`(지시문), `<script runat="server" language="vbscript"> … </script>`, `<!--#include file="…"-->`·`<!--#include virtual="…"-->`. 각 구간은 원본 줄·열을 안다.
2. **VBScript 어휘·블록 해석:** 코드 구간을 문장으로 나눈다 — 문자열(`""` 이스케이프), 주석(`'`, `Rem`), 줄 이음(` _`), `:` 여러 문장, 한 줄 `If … Then …`. 블록 짝을 센다: `If/End If`, `For/Next`, `For Each/Next`, `Do/Loop`, `While/Wend`, `Select Case/End Select`, `With/End With`, `Sub/End Sub`, `Function/End Function`, `Property/End Property`, `Class/End Class`. HTML 구간을 사이에 두고 블록이 이어지는 경우(`<% If x Then %> … <% End If %>`)가 흔하므로 블록 짝은 파일 전체의 코드 구간을 이어서 센다.

결과물: 구간 목록, 프로시저 목록(이름·줄 범위·매개변수), 선언 목록(`Dim`·`ReDim`·`Const`·`Class`·매개변수), 이름이 쓰인 곳, 해석 오류 목록. 원본의 해석 오류는 기준선으로 저장한다(C#의 `OriginalSyntax`와 같은 방식).

**include 해석:** `file=`은 그 파일 기준 상대 경로, `virtual=`은 사이트 루트 기준. 사이트 루트는 `asp.siteRoot` 설정, 없으면 작업 공간 루트(Claude 결정 — ASP 사이트는 `.git`·`.sln`이 없을 수 있어 작업 공간 루트가 파일 폴더로 떨어질 수 있다. 그때 `virtual=` include를 못 찾으면 doctor와 실행 메시지가 `asp.siteRoot`를 안내한다). include는 깊이 5, 파일 40개까지 따라간다(Claude 결정). 못 찾은 include가 있으면 이름 검사를 경고로 낮춘다(§4.5.5).

### 4.3 인코딩과 줄 끝

공용 규칙(`TextDecoding`: BOM → 엄격한 UTF-8 → CP949)을 ASP에 그대로 쓰면 두 가지가 깨진다(빌더 확인에서 코드로 확인). `cs`·`sql`의 공용 규칙은 바꾸지 않고 ASP 경로에만 다음을 둔다(Claude 결정).

**인코딩 정하기 (읽을 때, 앞에서 정해지면 끝):**
1. BOM이 있으면 BOM.
2. `<%@ … CodePage=N %>`가 있으면 N(65001 → UTF-8, 949 → CP949; 그 밖의 값은 종료 4로 멈추고 안내).
3. ASCII가 아닌 바이트가 있고 엄격한 UTF-8로 읽히면 UTF-8, 아니면 CP949.
4. 순수 ASCII 파일이면 `asp.encoding` 설정(기본 `cp949`). 공용 규칙대로 UTF-8로 보면 모델이 한글을 넣을 때 UTF-8 바이트로 써져 CodePage 949 사이트에서 깨진다 — 이것을 막는 규칙이다.
- 정한 인코딩으로 원본을 엄격하게 다시 읽어 실패하면(예: CodePage=949인데 UTF-8 한글) 종료 4로 멈추고 이유를 보인다. 추측해서 쓰지 않는다.
- `Response.CodePage`·`Response.Charset`은 기록만 한다.

**쓰기는 바이트 이어 붙이기:** 편집 영역 밖은 원본 **바이트를 그대로** 두고, 바뀐 영역만 정한 인코딩으로 인코딩해 끼운다. 그래서 줄 끝이 섞인 파일(25년 된 파일에 흔하다)도 영역 밖은 한 바이트도 바뀌지 않는다. 공용 `TextFileCodec`은 줄 끝이 섞이면 파일 전체를 다수 쪽으로 바꾸므로 ASP에는 쓰지 않는다. 바뀐 영역 안의 새 줄 끝은 그 영역 원본 줄들의 다수 줄 끝을 따른다. 끝 줄바꿈 유무도 원본 그대로.

**표현할 수 없는 문자:** 모델이 낸 새 텍스트가 정한 인코딩으로 표현되지 않으면(CP949 파일에 이모지·일부 특수 문자) APPLY에서 쓰지 않고 피드백으로 재시도한다: 「이 파일은 CP949입니다. 다음 문자를 쓸 수 없습니다: …」.

### 4.4 모델 입력

형식은 `sql`과 같은 SEARCH/REPLACE 블록이다. 새 프롬프트 `system.asp.txt`·`fewshot.asp.txt`(판 `asp-v1`), 예산은 `budget.*` 그대로.

넣는 것(앞이 우선, 예산이 모자라면 뒤에서부터 뺀다):
1. **[EDITABLE: target]** 대상 범위(지시문·include 줄은 제외).
2. **[READ-ONLY: page]** 파일 머리: 지시문, include 목록, `Option Explicit` 유무, 파일 인코딩.
3. **[READ-ONLY: procedures]** 대상이 부르는 `Function`·`Sub`의 선언 줄과 바로 위 주석(같은 파일 + include). 본문은 넣지 않는다.
4. **[READ-ONLY: variables]** 대상이 쓰는 페이지 변수·상수의 선언 줄(`Dim`, `Const`)과 처음 대입된 줄.
5. **[READ-ONLY: sql]** 대상이 부르는 SP(§4.6)의 매개변수 목록, 그리고 `sys.dm_exec_describe_first_result_set`으로 얻을 수 있으면 첫 결과 집합의 열 이름·형식. 개발 DB 연결(`sql.connectionString`)이 설정됐을 때만 쓴다. 운영 읽기 전용 연결(`sql.production`)은 ASP 문맥·검사에 쓰지 않는다(Claude 결정 — 운영 연결은 V2-1d에서 정의 비교 전용으로 정했고, 문맥 수집 때문에 운영에 질의를 늘리지 않는다). 결과 열을 못 얻으면(임시 테이블 등) 매개변수만 넣는다.
6. **[READ-ONLY: around]** 줄 범위 대상이면 범위 앞뒤 10줄(Claude 결정 — 블록이 범위 밖에서 열리고 닫히는 경우가 흔하다).

시스템 프롬프트의 고정 규칙(요지): 대상 밖을 SEARCH에 쓰지 않는다 / 지시문·include를 바꾸지 않는다 / 새 `Function`·`Sub`는 줄 범위 대상일 때만 범위 안에 둘 수 있다 / 없는 함수를 지어내지 말고 [READ-ONLY: procedures]와 VBScript 내장 함수만 쓴다 / `Option Explicit` 페이지에서 새 변수는 `Dim`한다 / SQL 문자열에 `Request` 값을 직접 붙이지 않는다 / `On Error Resume Next`를 새로 넣지 않는다.

### 4.5 판정 (순서대로, 앞이 실패하면 뒤는 하지 않는다)

모든 검사는 **원본 기준선과 비교해 새로 생긴 것만** 센다. 「새로」는 위치가 아니라 내용으로 센다(같은 문제 문장의 개수가 늘었는가 — 줄이 밀려도 틀리지 않게, Claude 결정).

1. **패치 적용**(기존 `SearchReplacePatcher`): 편집 영역 밖 SEARCH, 일치 실패, 변경 없음 → 재시도.
2. **인코딩**(§4.3) → 재시도.
3. **구조 검사**(§4.2 해석기, APPLY에서 쓰기 전에): `<% %>`·`<script runat>` 짝, VBScript 블록 짝, 닫히지 않은 문자열, 지시문·include 줄 불변, 프로시저 대상이면 대상이 여전히 프로시저 하나이고 이름·매개변수가 그대로인지. 이름·매개변수 변경은 A1에서 요청과 상관없이 막는다(Claude 결정 — 부르는 곳을 함께 고치지 못하고, 요청의 의도를 코드가 판정할 수 없다. 필요하면 A2의 명세 작업으로). 실패는 줄 번호와 함께 피드백 → 재시도.
4. **안전 검사**(APPLY에서 쓰기 전에, `SqlSafetyGuard`처럼): 새로 생긴 다음 구문은 거부(재시도, 끝내 남으면 종료 5).
   - `CreateObject`·`Server.CreateObject`의 `WScript.Shell`, `Shell.Application`, `Scripting.FileSystemObject`(원본 파일에 이미 쓰고 있으면 허용), `WScript.Network`.
   - `Execute`, `ExecuteGlobal`, `Eval`.
   - `On Error Resume Next` 추가.
   - SQL처럼 보이는 문자열(`SELECT`·`INSERT`·`UPDATE`·`DELETE`·`EXEC`·`MERGE`를 단어로 포함)에 `&`로 `Request(…)`·`Request.Form`·`Request.QueryString`·`Request.Cookies`를 바로 붙이거나, 대상 안에서 `Request` 값을 바로 대입받은 변수를 붙이기. 다음으로 감싼 값은 허용한다(Claude 결정):
     - 숫자·날짜 변환 `CLng`·`CInt`·`CDbl`·`CBool`·`CDate`(따옴표를 깰 수 없다).
     - 문자열 값의 작은따옴표 이중화 `Replace(x, "'", "''")`(Classic ASP의 표준 처리. 이 규칙이 없으면 백오피스의 흔한 요청 — 문자열 검색 조건 추가 — 이 모두 종료 5가 된다).
     - `asp.sqlSanitizers` 설정의 현장 함수 이름(예: `SafeSQL`).
     - `ADODB.Command`의 매개변수(`CreateParameter`)로 넘긴 값은 문자열 연결이 아니므로 처음부터 대상이 아니다.
     프롬프트는 우선순위를 알려 준다: 같은 페이지가 `Command` 매개변수를 쓰면 그 방식, 아니면 `asp.sqlSanitizers`의 함수, 없으면 `Replace(x, "'", "''")`. `asp.sqlSanitizers`가 비어 있으면 doctor가 「현장 SQL 처리 함수가 설정되지 않음」을 알리기만 하고 막지 않는다.
   - 대상 범위에 이미 있던 같은 구문은 막지 않는다(R2와 같은 원칙: 원본의 위험 구문은 「pelly로 편집할 수 없음」이 아니라 「새로 늘리지 않음」 — SQL의 R2와 다르게 정한 이유: 25년 된 백오피스는 대부분의 페이지에 이런 문장이 있어 R2처럼 막으면 쓸 곳이 없다. Claude 결정).
5. **이름 검사:** 수정으로 새로 쓰인 이름 중 다음 어디에도 없는 것 → 오류 피드백 → 재시도.
   - VBScript 키워드·내장 함수·상수(`vbCrLf` 등), ASP 내장 개체(`Response`·`Request`·`Server`·`Session`·`Application`·`ObjectContext`·`ASPError`).
   - 파일과 include의 `Function`·`Sub`·`Class`·`Dim`·`ReDim`·`Const`, 감싸는 프로시저의 매개변수.
   - `Option Explicit`가 없는 페이지에서는 파일 어딘가에서 대입되는 이름도 변수로 인정한다.
   - `.` 뒤의 멤버 이름은 보지 않는다(COM 개체 멤버는 알 수 없다).
   - ADO 상수(`adOpenStatic` 등)는 `adovbs.inc`가 include됐거나 `<!--METADATA TYPE="typelib" …-->`가 있으면 기본 목록으로 인정한다.
   - include를 못 찾았거나 `Execute`·동적 include가 있는 파일이면 확신할 수 없으므로 **막지 않고 경고**로 REVIEW에 보여 준다.
6. **규칙**(기존 `RulesStep`, 프로젝트 규칙의 금지 패턴).
7. **VBScript 컴파일 검사 (Windows):** 서버 코드만 뽑아 임시 `.vbs`를 만들고 `cscript //Nologo //B //T:10`으로 **컴파일만** 한다.
   - 줄 번호를 원본과 같게 둔다: HTML 줄은 빈 줄, `<%= expr %>`는 `Response.Write (expr)`, 코드는 그대로.
   - 실행을 막기 위해 첫 실행 문장 자리에 `WScript.Quit 0`을 둔다. VBScript는 파일 전체를 컴파일한 뒤 실행하므로 컴파일 오류는 보고되고 어떤 문장도 실행되지 않는다. `Option Explicit`는 첫 문장이어야 하므로 그 뒤에 둔다(정확한 배치는 빌더 몫).
   - 컴파일 오류(`Microsoft VBScript compilation error`)의 줄을 원본 줄로 바꿔 피드백 → 재시도. 원본의 컴파일 오류는 기준선(같은 방법으로 원본도 한 번 컴파일).
   - include는 합치지 않고 대상 파일만 컴파일한다(Claude 결정 — 이름 중복 같은 파일 간 오류는 못 잡지만 단순하다).
   - Windows가 아니거나 `cscript`·VBScript를 쓸 수 없으면(Windows 11 24H2부터 VBScript는 선택적 기능이고 이후 제거 예정이다) **「건너뜀」**으로 기록하고 진행한다. 환경 오류로 멈추지 않는다(Claude 결정 — 다른 검사만으로도 쓸모가 있다. 결과 화면과 요약에 「VBScript 컴파일 검사 건너뜀」이 반드시 보인다).
8. **SP 확인 (개발 DB 연결이 있을 때, 운영 연결은 쓰지 않음):** 수정으로 새로 등장한 SP 이름(§4.6)이 개발 DB에 없으면 오류 피드백 → 재시도. 매개변수 개수가 명백히 다르면(`Command.Parameters.Append` 개수, `EXEC 이름 a, b` 인수 개수가 셀 수 있을 때) 경고. 개발 DB 연결이 없으면 「건너뜀」.

**REVIEW:** diff와 함께 판정 표를 보여 준다 — 각 검사의 통과·경고·건너뜀, 그리고 고정 문구 「ASP는 실행해서 확인하지 않았습니다. 화면에서 직접 확인하십시오.」. `--yes` 같은 자동 승인이 있으면 그대로 따르되(기존 명령과 같은 동작), 이 문구는 실행 기록과 `summary.json`에 남긴다.

### 4.6 ASP 안의 SP 호출 찾기

ADO 호출 모양 몇 가지를 문자열 수준에서 알아본다(Claude 결정 — 실제 현장 모양은 현장 확인 A3에서 보강):
- `cmd.CommandText = "dbo.usp_X"` (+ `CommandType = 4`/`adCmdStoredProc`)
- `conn.Execute("EXEC usp_X …")`, `"exec usp_X " & …`, `rs.Open "usp_X …", conn`
- `{call usp_X(?, ?)}`

이름 해석은 기존 `SqlObjectName`과 `SqlNameStringFinder`의 문자열 규칙을 재사용한다. 공용 함수(`include`의 `ExecSP "usp_X", …` 같은 래퍼)는 `asp.spCallFunctions` 설정에 함수 이름과 SP 이름 인수 위치를 적으면 알아본다.

이 찾기는 A1에서 §4.4(문맥)·§4.5.8(존재 확인)에, A2에서 영향 탐색(SP → ASP 페이지)에 쓰인다.

### 4.7 결과와 기록

- `EditKind.Asp`. `summary.json`에 `asp` 블록: 대상 단위(프로시저·줄 범위), 파일 인코딩, 검사별 결과(통과·경고·건너뜀), 찾은 SP 이름, include 해석 결과(찾음·못 찾음 개수).
- `pelly report` 한 줄: 「ASP: 실행 N건, 성공 K건, 컴파일 검사 건너뜀 M건, 실패 유형 …」.
- 새 실패 유형: `AspStructureErrors`, `AspUnknownNames`, `AspCompileErrors`, `AspSafetyRejected`, `AspEncoding`, `AspSpNotFound`, `AspUnsupportedLanguage`.
- `pelly doctor` 행: 「ASP 구문 검사(cscript)」 — 됨/안 됨(Windows가 아님, VBScript 기능 없음), `asp.siteRoot`가 있는 폴더인지.

### 4.8 2단계 A2 — SP와 함께 바꾸기 (별도 계획)

- 명세 작업에 `kind: asp`(대상: 파일 + `proc`/`lines`). `apply`는 기존처럼 작업을 순서대로, 각 작업은 A1 파이프라인으로 실행한다.
- 같은 명세의 앞선 SQL 작업이 SP를 바꿨으면, 뒤의 ASP 작업 문맥 [READ-ONLY: sql]에 **바뀐 뒤의** SP 매개변수·결과 열을 넣는다(파일 기준, DB 배포 전이므로 SQL 파일을 해석해서 — 결과 열은 개발 DB에 임시로 만들지 않는 한 모르므로 매개변수만, Claude 결정).
- `spec check --impact`: SP를 바꾸는 작업마다 그 SP를 부르는 `.asp`·`.inc` 파일·줄을 찾아 경고로 보여 준다(테이블당 경고 상한 10과 같은 방식). 명세에 그 ASP 작업이 없으면 「빠졌을 수 있음」.
- `spec draft`가 ASP 작업을 제안하는 것은 A2 범위 밖(Claude 결정).

## 5. 바꾸는 곳 (짐작 — 계획에서 확정)

- Core 새 폴더 `Asp/`: 페이지 해석기, VBScript 어휘·블록 해석기, include 해석, 기준선, 이름 검사, 안전 검사, SP 호출 찾기, `cscript` 컴파일 검사(`IProcessRunner` 사용), 인코딩 확인.
- Fsm: `AspLocatePhase`, `AspSlicePhase`, `AspApplyPhase`(구조·안전 검사 후 쓰기), 검증 단계 `AspNamesStep`·`AspCompileStep`·`AspSpStep`, `FsmOrchestratorFactory.CreateAsp`.
- 모델·설정: `EditKind.Asp`, `RunContext.Asp`, `PellyOptions.Asp`(`siteRoot`, `encoding`, `extensions`, `sqlSanitizers`, `spCallFunctions`), `FailureKinds`.
- 프롬프트: `system.asp.txt`, `fewshot.asp.txt`, `PromptResources`.
- CLI: `Commands/AspCommand.cs`, `RunArguments`(`--proc`, `--lines`), Runners, Composition, CliApp, 자동완성(PowerShell).
- 기록: `RunLogger`·`RunReport`·`ReportSanitizer`, doctor 행.
- 문서: README, PRD(§1.4 범위 표에 ASP 줄, §12에 V2-6), `deploy/usage.md`, `deploy/field-results.md` 새 절, `deploy/server-checklist.md`(해당 없으면 생략).
- 코퍼스: `eval/corpus/Asp/` — CP949 파일 포함, include(`dbconn.asp`, `func_common.inc`, `adovbs.inc` 모사), 목록(검색·페이징)·수정 폼·저장 처리·엑셀 내려받기 페이지, ADO로 `eval/corpus/Sql`의 SP를 부름. 함수 없이 본문 코드가 이어진 페이지를 반드시 포함.

## 6. 검증 (pelly 자체 테스트)

- 해석기 단위 테스트: 문자열 안의 `%>`, 주석 안의 `End If`, 줄 이음, `:` 여러 문장, 한 줄 `If`, HTML을 사이에 둔 블록, `<script runat=server>`, 닫히지 않은 블록·문자열(원본 기준선 포함).
- 인코딩: CP949 파일의 범위 밖 바이트 동일, 순수 ASCII 파일 + 한글 추가 → CP949로 쓰임, `CodePage=65001` → UTF-8, CodePage와 실제 바이트가 어긋남 → 종료 4, 줄 끝 섞인 파일의 범위 밖 바이트 동일, CP949로 못 쓰는 문자 거절, BOM·끝 줄바꿈 유지.
- 안전·이름 검사: 기존 구문은 통과·새 구문만 거부, sanitizer 설정, 못 찾은 include → 경고로 낮춤.
- 컴파일 검사: 임시 `.vbs` 생성(줄 번호 대응)은 모든 OS에서 단위 테스트, 실제 `cscript` 실행은 Windows CI에서만(`[WindowsFact]` 같은 기존 방식), 「실행되지 않음」을 확인하는 테스트(파일을 만드는 코드가 있는 페이지를 컴파일해도 파일이 생기지 않음).
- CLI·통합: 가짜 모델로 성공·재시도·거부·되돌림, Mac에서 「건너뜀」 표시.
- 게이트는 기존과 같다: `dotnet build -warnaserror` 종료 0, `dotnet test` 실패 0.

## 7. 측정 (예비)

- 벤치마크 `eval/benchmark/tasks/asp-001..012.yaml`(12건, Claude 결정): 프로시저 대상 4, 줄 범위 대상 6(HTML 섞임 2 포함), SP 호출 변경 2. 판정은 기존 판정기(예상 문자열 포함·불포함)와 「구조·안전·이름 검사 통과」.
- 외부(Mac)에서는 컴파일 검사가 건너뜀이라 Windows CI 또는 현장에서 따로 잰다.
- 기록 `docs/asp-premeasure.md`. KPI 판정에는 쓰지 않는다.

## 8. 사람이 확인할 것 (현장, `deploy/field-results.md` 새 절)

- A1: ASP 소스가 어디 있는가(소스 관리 유무 — `.git`·`.svn`이 없으면 작업 공간 루트와 `.pelly` 위치가 파일 폴더가 된다), 파일 인코딩(CP949/UTF-8 섞임, `CodePage` 지시문 사용 여부 → `asp.encoding`), 줄 끝이 섞인 파일이 있는지, `include virtual`의 사이트 루트.
- A2: 개발 PC에서 `cscript`로 VBScript를 쓸 수 있는가(Windows 버전, 그룹 정책으로 막혔는지, VBScript 선택적 기능).
- A3: SP를 부르는 실제 모양(직접 `Command`인가, 공용 래퍼 함수인가 → `asp.spCallFunctions`), SQL 문자열을 만들 때 쓰는 현장 sanitizer 함수 이름(→ `asp.sqlSanitizers`).
- A4: 실제 요청 3건(줄 범위 2, 프로시저 1)을 돌린 결과와, 사람이 화면에서 확인했을 때 문제가 있었는지.

## 9. 단계와 순서

1. **A1 `pelly asp`** — 이 문서 §4.1–§4.7. 계획 하나, 구현, 예비 측정.
2. **A2 SP와 함께** — §4.8. A1 결과와 현장 A3 답을 보고 계획한다.

## 10. 빌더에게 맡기는 것

- 해석기의 내부 구조(구간·토큰 자료형), 오류 메시지의 정확한 문구(한국어로).
- `WScript.Quit` 배치 등 컴파일 전용 `.vbs`를 만드는 정확한 방법(줄 번호 대응과 「실행 안 됨」만 지키면 된다).
- VBScript 내장 함수·상수 목록의 출처와 형식(코드 안 표 또는 리소스 파일).
- 기존 `cs`·`sql` 부품 중 재사용할 것과 새로 만들 것의 경계(예: `PatchWriter`, `FileGuard`, `FeedbackBuilder`는 그대로 쓸 것으로 짐작).
- 줄 범위 대상의 [READ-ONLY: around] 줄 수, include 깊이·개수 상한의 최종값(위 값은 출발점).
