# 현장 확인 안내

현장에서 사람이 확인할 일을 **할 순서대로** 한 곳에 모았습니다. 이 문서는 「무엇을 어떻게 하나」만 다루고, 결과는 같은 폴더의 `field-results.md`(현장 결과 양식) 칸에 적습니다. 항목마다 결과를 적을 칸 번호(예: A2)를 붙였습니다. 자세한 설명이 필요하면 각 항목 끝의 「자세히」 문서를 보십시오.

* **적지 않는 것:** 서버 주소, 계정, 비밀번호, 사내 시스템·화면 이름, 경로, 함수 본문, 요청문. 종류와 예/아니오, 수치만 적습니다.
* **불합격은 고치지 말고 그대로 적습니다.** 막힌 명령과 이상한 출력은 `field-results.md` 15절 「메모」에 적습니다.
* 명령은 개발 PC의 `cmd`나 PowerShell에서, 작업 공간(소스 폴더) 안에서 실행합니다. `<…>`는 자기 값으로 바꿉니다.

## 순서 한눈에 보기

| 순서 | 할 일 | 결과 칸 | 걸리는 시간 (대략) |
| :-: | :--- | :--- | :--- |
| 0 | 공통 준비: 설정 파일과 `pelly doctor` | `field-results.md` 맨 위 측정일·측정자 | 30분 |
| 1 | ASP 페이지 수정 (`pelly asp`) | 16절 A1–A6 | 반나절 |
| 2 | JS·CSS 수정 (`pelly js`·`pelly css`) | 17절 W1–W14 | 반나절 |
| 3 | 현재 동작 고정 테스트 (`pelly test`) | 13절 T1–T4 | 2시간 |
| 4 | 승인 창·IDE·자동완성 (`pelly ui`, External Tools, Tab 완성) | 9절 U1–U4, 10절 C1–C2, 11절 | 1시간 |
| 5 | 명세 초안 (`pelly spec draft`) | 12절 D1–D3 | 1시간 |
| 6 | 운영 DB 읽기 전용 계정 (DB 관리자) | 15절 메모 | 30분 |
| 7 | 서버·보안 질문지 남은 칸 (서버 관리자 등) | 질문지 「답」 칸 | — |
| 8 | 기준선 측정 (`pelly-eval`, 현장 키트) | 1–8절 | 하루 |
| 9 | 반출 | 14절 | 30분 |

1·2를 먼저 권합니다. 현장 백오피스(Classic ASP + SP + JS)에 바로 쓰는 기능이고, 다음 개발을 고를 근거가 여기서 나옵니다.

## 0. 공통 준비

1. `dotnet --list-runtimes`에 `Microsoft.NETCore.App 10.0.x`가 있는지 봅니다. `pelly.exe`는 PC의 .NET 10 런타임으로 실행됩니다(.NET 10 SDK에 함께 들어 있음). 없으면 `pelly.exe`가 .NET 설치 안내를 내고 시작하지 않습니다. 그 안내 문구를 15절 메모에 적습니다.
1. `pelly.exe`와 같은 폴더에 `BuildHost-net472`, `BuildHost-netcore` 폴더와 `av_libglesv2.dll`, `libHarfBuzzSharp.dll`, `libSkiaSharp.dll`이 있는지 봅니다(반입 zip을 그대로 푼 상태).
2. 작업 공간 루트에 `pelly.json`을 둡니다(`pelly.json.template`을 복사). `llm.endpoint`·`llm.model`을 채우고, 1·2를 할 것이므로 `asp`·`web` 절도 넣습니다:

   ```json
   {
     "asp": { "siteRoot": ".", "encoding": "cp949" },
     "web": { "jsTarget": "auto" }
   }
   ```

   `siteRoot`는 `<!--#include virtual="…"-->`의 기준 폴더(사이트 폴더)입니다. 소스 관리(`.git`·`.svn`)가 없는 사이트는 작업 공간 루트가 파일 폴더로 잡힐 수 있으니 사이트 폴더 맨 위에서 실행하십시오.
3. 점검합니다:

   ```
   pelly doctor
   ```

   볼 것: 서버·모델 행이 Ok인지, 「ASP 구문 검사(cscript)」·「ASP 사이트 루트」·「JS·CSS」·「사이트 색인」 행. 이 네 행의 결과는 1·2에서 A1·A2·W2·W14 칸에 옮겨 적습니다. 「사이트 색인」 행은 `.pelly/cache/web-site-index.json`을 만듭니다(다음 실행이 빨라짐).

자세히: `install.md`, `usage.md`의 「환경 점검: `pelly doctor`」.

## 1. ASP 페이지 수정 → 16절 A1–A6

**A1 소스와 인코딩.** 소스 관리 유무, 파일 인코딩(CP949·UTF-8 섞임), `<%@ … CodePage=N %>` 사용 여부, 줄 끝이 섞인 파일, include 기준 폴더를 확인해 적습니다. `pelly doctor`의 「ASP 사이트 루트」 행을 메모에 옮깁니다.

**A2 cscript (컴파일 검사).** cmd에서:

1. `winver` — Windows 11 24H2 이상·Server 2025면 VBScript가 선택적 기능입니다. PowerShell `Get-WindowsCapability -Online | Where-Object Name -like 'VBSCRIPT*'`가 `Installed`여야 합니다.
2. `echo WScript.Echo "ok" > %TEMP%\t.vbs` 후 `cscript //Nologo %TEMP%\t.vbs` → `ok`.
3. `echo If x Then > %TEMP%\bad.vbs` 후 `cscript //Nologo %TEMP%\bad.vbs` → 컴파일 오류 문구. 출력 모양을 메모에 그대로 옮기되 경로의 사용자 이름은 가립니다.

`pelly doctor`의 「ASP 구문 검사(cscript)」가 Ok인지도 적습니다. 안 되면 `pelly asp`는 컴파일 검사만 건너뛰고 동작합니다.

**A3 SP 호출 모양.** SP를 `ADODB.Command`로 직접 부르는지, 공용 래퍼 함수(예: `ExecSP "usp_X", …`)를 거치는지, SQL 문자열을 만들 때 쓰는 처리 함수(예: `SafeSQL`)가 있는지 봅니다. 있으면 `pelly.json`에 넣습니다:

```json
{ "asp": { "spCallFunctions": [{ "name": "<래퍼 함수>", "argument": 1 }], "sqlSanitizers": ["<처리 함수>"] } }
```

`argument`는 SP 이름이 몇 번째 인수인지(1부터)입니다.

**A4 실제 요청 3건.** 평소 하는 수정 3건(줄 범위 2, 프로시저 1)을 돌리고, 승인한 뒤 **화면을 직접 띄워** 확인합니다. pelly는 페이지를 실행하지 않습니다.

```
pelly asp <페이지.asp> "<요청>" --lines 24-37
pelly asp <파일.inc> "<요청>" --proc <함수 이름>
```

결과는 16절 A4 표에 적습니다. 종료 코드, 시도 수, 검사 줄은 화면 끝과 `.pelly\runs\<실행 ID>\summary.json`에 있습니다. 실행 뒤 버전 관리 diff로 편집 범위 밖과 한글이 그대로인지 봅니다.

**A5 SP 영향.** A3 설정을 넣은 뒤, 여러 페이지가 부르는 SP 하나로 명세를 만들어 영향 경고를 봅니다:

```
pelly spec new <id>
pelly spec check changes\<id>.md --impact
```

명세 파일에는 `- [ ] T1 sql <SP 파일>#dbo.<SP 이름> <요청>` 한 줄만 적어도 됩니다. 경고(`ImpactMissing`) 수와, 실제 호출과 맞았는지·놓친 호출이 있었는지를 적습니다.

**A6 sql + asp 함께.** 실제 업무 요청 1건을 SP와 ASP 두 작업으로 적어 돌리고 화면을 확인합니다:

```
pelly spec check changes\<id>.md --impact
pelly apply changes\<id>.md
```

함께 되돌려야 하면 두 작업 줄 끝에 `[G1]`을 붙입니다. 그룹 사용 여부, 영향 경고가 맞았는지, 「고치지 않은 호출」 경고가 났는지를 적습니다.

자세히: `usage.md`의 「`pelly asp`」, 「`pelly spec`·`pelly apply`」.

## 2. JS·CSS 수정 → 17절 W1–W14

**먼저 보고 적을 것 (W1–W6, W8, W9, W13, W14).** 명령을 돌리기 전에 소스와 화면을 보고 적습니다.

* W1 백오피스를 쓰는 브라우저(IE 모드인가 최신 Edge·Chrome인가). IE 모드면 `web.jsTarget`을 `es5`로 정할지 적습니다.
* W2 jQuery 버전: `pelly doctor`의 「JS·CSS」 행에 찾은 버전이 나옵니다.
* W3 JS·CSS 파일 인코딩, W4 공용 JS(`common.js` 류)와 페이지 전역 함수, W5 라이브러리 폴더 위치(`lib/`·`plugin*/`·`vendor/`·`jquery*`·`bootstrap*`에 맞는지).
* W6 수정이 주로 따로 있는 `.js`·`.css`에서 일어나는지 페이지 안 `<script>`·`<style>`에서 일어나는지.
* W8 페이지 안 `<script>`에 `<% If %>` 같은 서버 문장이 얼마나 섞였는지. **이 답으로 다음 개발(분기별 해석)을 할지 정합니다.**
* W9 공용 머리 `.inc`가 jQuery를 `<script src>`로 부르는지.
* W13 저장 페이지가 `Request.Form`과 `Request`를 섞어 쓰는지, ajax 저장이 `serialize()`를 쓰는지, 폼 action을 JS가 정하는지.
* W14 사이트 규모: `pelly doctor`의 「사이트 색인」 행(페이지·`.js` 수, 합계, 뺀 파일 수)을 옮기고, 가장 큰 파일 크기와 100KB 넘는 파일 수를 적습니다. 한도(종류마다 2000개)에 걸리면 Warn이 납니다.

**W7 따로 있는 파일 요청 3건(JS 2, CSS 1).**

```
pelly js  <파일.js> "<요청>" --function <함수 이름>
pelly js  <파일.js> "<요청>" --lines 45-50
pelly css <파일.css> "<요청>" --selector ".list_tbl td"
```

승인 뒤 브라우저에서 강력 새로 고침(Ctrl+F5)으로 화면을 확인합니다. 결과는 17절 W7 표에 적습니다.

**W10 페이지 안 요청 3건(페이지 안 JS 2, 서버+클라이언트 1).**

```
pelly js  <페이지.asp> "<요청>" --function <함수 이름>
pelly js  <페이지.asp> "<요청>" --lines 140-175
pelly asp <페이지.asp> "<요청>" --lines 24-37
```

마지막 줄은 서버 코드와 `<script>` 블록에 함께 걸친 범위로 고릅니다. W7 표 형식으로 적고 `js`(페이지)·`asp`(걸친 블록)를 구분합니다.

**W11·W12 함수 서명 변경과 부르는 곳.** 공용 JS 함수에 매개변수를 더하는 실제 요청 1건을 명세로 씁니다. 함수 작업 한 줄과 그 함수를 부르는 페이지 작업 줄을 적고:

```
pelly spec check changes\<id>.md --impact
pelly apply changes\<id>.md
```

W11에는 부르는 곳 수와 `JsImpactNotInSpec` 경고 수를, W12에는 종료 코드, 남은 호출 경고가 맞았는지, 모델이 부르는 곳을 맞게 고쳤는지를 적습니다. 폼 입력란이나 `Request.Form` 키를 고친 요청이면 「폼 필드」 경고가 맞았는지도 W13 메모에 적습니다.

자세히: `usage.md`의 「`pelly js`·`pelly css`」.

## 3. 현재 동작 고정 테스트 → 13절 T1–T4

1. T1 레거시 솔루션에 테스트 프로젝트가 있는지, 없으면 MSTest 테스트 프로젝트를 오프라인 NuGet으로 만들 수 있는지 봅니다. pelly는 테스트 프로젝트를 만들어 주지 않습니다.
2. T2 레거시 프로젝트는 `pelly.json`의 `test.command`에 `vstest.console.exe … /logger:trx`와 `{trx}` 자리를 넣어야 합니다. 없으면 종료 코드 4입니다.
3. T3 실제 레거시 메서드 3건을 돌립니다:

   ```
   pelly test <파일.cs> --method <메서드> --count 3
   ```

   남긴 테스트의 기대값을 읽고 지금의 버그가 굳은 것이 없는지 봅니다. 실행 뒤 대상 파일이 바뀌지 않았는지 버전 관리 diff로 확인합니다.
4. T4 변이 검사 중 운영 파일이 잠깐 바뀌는 것(IDE가 다시 읽을 수 있음)을 받아들일 수 있는지 적습니다.

자세히: `usage.md`의 「`pelly test`」.

## 4. 승인 창·IDE·자동완성 → 9–11절

**U1–U4 `pelly ui`.** `ide-external-tools.md`의 「GUI 항목」대로 Visual Studio External Tools에 등록하고(「출력 창 사용」 켬) 실제 파일로 엽니다:

```
pelly ui cs <파일.cs> "<요청>" --line 120
```

U1 한글 IME 입력(조합·확정·백스페이스), U2 External Tools에서 창이 뜨고 콘솔 창이 따로 안 뜨며 적용 뒤 파일이 다시 읽히는지, U3 125%·150% 배율에서 글자가 흐리지 않은지, U4 백신·응용 프로그램 제어가 DLL을 막지 않는지 봅니다. 화면에 보인 문구를 메모에 적습니다.

**11절 External Tools.** `ide-external-tools.md`의 「Visual Studio 실기 확인 결과」 표를 채웁니다.

**C1·C2 PowerShell 자동완성.** 먼저 `Get-ExecutionPolicy -List`로 프로필 실행이 허용되는지 봅니다(C1). 허용되면:

```
pelly completion powershell > "$HOME\pelly-completion.ps1"
Add-Content $PROFILE '. "$HOME\pelly-completion.ps1"'
```

새 PowerShell 5.1 창에서 `pelly sp`+Tab이 `spec`이 되는지, `pelly cs .\`+Tab이 파일을 완성하는지 봅니다(C2).

자세히: `usage.md`의 「`pelly ui`」, 「PowerShell 자동완성」, `ide-external-tools.md`.

## 5. 명세 초안 → 12절 D1–D3

현장 코드에서 평소 하는 변경 3건(한 곳 변경, C#+SQL, 같은 파일 여러 메서드 등)으로 초안을 받아 봅니다:

```
pelly spec draft <id> "<요청>" --scope <폴더> --scope <SP 파일.sql>
```

예산(4096토큰)을 넘으면 종료 4와 함께 큰 파일을 알려 주니 `--scope`를 좁힙니다. 판정 칸에 「고치지 않고 쓸 만함 / 고쳐서 씀 / 못 씀」을 적고, 버린 줄 수와 빠진 작업을 메모에 적습니다. 걸린 시간도 적습니다.

자세히: `usage.md`의 「명세 초안을 모델에게 받기」.

## 6. 운영 DB 읽기 전용 계정 (DB 관리자)

DB 관리자에게 `server-checklist.md`의 「운영 SQL Server 읽기 전용 계정」을 전달합니다. 권한은 `CONNECT`와 `VIEW DEFINITION` 두 가지뿐입니다. 연결 문자열은 환경 변수 `PELLY_SQL_PROD_CONNECTION`으로만 넣고, `pelly.json`에 `sql.production.allowedHosts`·`allowedDatabases`를 채운 뒤 `pelly doctor`의 「SQL 운영(읽기 전용)」 행이 Ok인지 봅니다.

그다음 주요 테이블을 쓰는 SP 하나로 `pelly spec check changes\<id>.md --impact`를 돌려, 테이블당 경고 상한 10개가 적당한지 15절 메모에 적습니다.

## 7. 서버·보안 질문지 남은 칸

`docs/m4-field-questionnaire.md`(저장소 안 문서)의 「답」 칸 중 비어 있는 것을 담당자에게 받습니다. 특히 1-3 Ollama 서버 환경 변수(`OLLAMA_NUM_PARALLEL`, `OLLAMA_FLASH_ATTENTION`, `OLLAMA_KV_CACHE_TYPE`, `OLLAMA_KEEP_ALIVE`, `OLLAMA_HOST`)의 현재 값이 아직 없습니다. 권장 값은 `server-checklist.md` 위쪽 표에 있습니다.

## 8. 기준선 측정 → 1–8절

현장 키트 zip(`pelly-eval.exe`)으로 현장 코드에서 성공률을 잽니다. 절차가 길어 따로 있습니다: 현장 키트의 `docs\field-guide.md`(순서)와 `docs\field-benchmark.md`(작업 작성). 결과는 `pelly-eval`이 만든 `summary.md`·`report.md`에서 1–8절로 옮겨 적습니다.

## 9. 반출 → 14절

반출할 자료는 이름을 가려 만듭니다:

```
pelly report --sanitize --out C:\export
pelly doctor --sanitize --out C:\export
```

반출 전에 파일을 직접 열어 사내 이름이 없는지 보고, 14절에 파일 목록과 승인한 사람·날짜를 적습니다. 채운 `field-results.md`도 함께 반출합니다.
