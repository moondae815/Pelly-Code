# pelly asp 예비 측정

> **예비 — Windows·현장 측정 별도.** 에이전트가 개발 맥(macOS)의 로컬 Ollama로 잰 값이다. 맥에는 `cscript`가 없어 **VBScript 컴파일 검사는 36회 모두 「건너뜀」**이고, SQL 연결을 설정하지 않아 SP 확인도 모두 「건너뜀」이다. 사례 12건 × 3회, 표본이 작고 코퍼스가 작아 비율보다 **판정이 무엇을 잡고 무엇을 놓치는지**를 읽는 자료다. KPI 판정에는 쓰지 않는다(설계 §7). Windows 컴파일 검사와 현장 확인은 `deploy/field-results.md` 16절에 사람이 적는다.

설계: [docs/superpowers/specs/2026-11-04-pelly-asp-design.md](superpowers/specs/2026-11-04-pelly-asp-design.md) §7 · 사례와 실행기: `eval/benchmark/asp/`

## 1. 요약

- 종료 0은 **36/36**, 모두 **첫 시도**에 통과했다(재시도 0회). 예상 문자열 판정(`mustContain`·`mustNotContain`)도 36/36 맞았다.
- 편집 영역 밖 바이트는 36/36 원본과 같았다(CP949, 줄 끝 섞인 `save.asp`, UTF-8 `list_excel.asp` 포함). 검사 실패(구조·안전·인코딩·이름)는 한 번도 나지 않았다.
- 그런데 **판정을 다 통과한 결과 중 사람이 읽으면 틀린 것이 4회(2사례)** 있었다. 하나는 실행하면 페이지가 오류로 멈추는 수정(asp-006, 3/3), 하나는 기존 줄을 지워 입력값을 버린 수정(asp-007, 1/3)이다. 둘 다 컴파일이 되는 코드라 Windows `cscript`로도 잡히지 않는다. 「실행 검증 없음」 문구와 사람 검토가 필요한 이유다(5절).
- 전체 걸린 시간 **89초**(사례당 평균 2.25초, 모델 생성 0.8~2.8초). 모델이 짧은 SEARCH/REPLACE만 쓰고 생각 토큰을 쓰지 않았다(생각 모드 `retry`, 재시도가 없어 0).

## 2. 조건

| 항목 | 값 |
| :--- | :--- |
| 모델 | 태그 `qwen3.6:35b-a3b-q4_K_M`(현장과 같은 태그). 로컬 `ollama list`에 같은 태그가 ID가 다른 항목 둘(`b52ca43760e9`, `b8cba206bbea`)로 있어 어느 쪽이 쓰였는지는 구분하지 못했다. 현장 ID는 `07d35212591f` |
| 서버 | 로컬 Ollama 0.40.0, `http://localhost:11434` |
| 기기 | Mac (Apple M5 Pro), macOS Darwin 25.6.0 |
| 코드 | 브랜치 `asp` `c2ea975`(pelly 0.9.1), 프롬프트 `asp-v1`, 생각 모드 `retry`(기본) |
| 실행 일시 | 2026-11-04 (사례 12건 × 3회, 전체 89초) |
| 실행 방법 | `eval/benchmark/asp/run.sh --repeat 3` — 회마다 `eval/corpus/Asp`를 새 임시 폴더에 복사하고(`.git` 폴더를 만들어 작업 공간 루트로 삼음) `pelly asp <파일> "<요청>" --proc|--lines … --yes` |
| 설정 | 코퍼스 `pelly.json`(`asp.siteRoot` `.`, `asp.sqlSanitizers` `["SafeSQL"]`), 그 밖은 기본값(`retry.max` 3 = 시도 최대 4번). `sql.connectionString` 없음 |
| 판정 | 종료 0 + 결과 파일에 `mustContain` 전부 있음 + `mustNotContain` 없음. 종료 0이 아니면 대상 파일 해시가 실행 전과 같아야 함(36회 중 해당 없음). 편집 영역 밖 바이트 비교는 측정 뒤 따로 함 |

## 3. 사례별 결과

| 사례 | 종류 | 파일 (인코딩) | 대상 | 성공 | 평균 시도 | 평균 시간 | 사람이 읽은 판정 |
| :--- | :--- | :--- | :--- | :-: | -: | -: | :--- |
| asp-001 | 프로시저 | `inc/func_order.inc` (CP949) | `GetOrderAmount` — 원 미만 `Fix`로 버림 | 3/3 | 1.0 | 3.7초 | 맞음 |
| asp-002 | 프로시저 | `inc/func_order.inc` (CP949) | `StatusName` — 상태 4 「반품」 | 3/3 | 1.0 | 1.7초 | 맞음 |
| asp-003 | 프로시저 | `inc/func_common.inc` (CP949) | `WriteSelectOptions` — 값·이름 개수가 다르면 끝냄 | 3/3 | 1.0 | 2.3초 | 맞음 |
| asp-004 | 프로시저 | `inc/dbconn.asp` (ASCII → CP949) | `OpenDb` — `ConnectionTimeout = 30` | 3/3 | 1.0 | 1.7초 | 맞음 |
| asp-005 | 줄 범위 | `admin/order/list.asp` (CP949) | 24-37 — `@Status` 검색 조건 | 3/3 | 1.0 | 2.0초 | 맞음(아래 관찰 3) |
| asp-006 | 줄 범위 | `admin/order/list.asp` (CP949) | 15-22 — `pageSize`를 요청값으로, 1~100 밖이면 20 | 3/3 | 1.0 | 2.0초 | **틀림 3/3**(관찰 1) |
| asp-007 | 줄 범위 | `admin/order/save.asp` (ASCII, CRLF·LF 섞임) | 12-17 — 취소 사유 200자로 자름 | 3/3 | 1.0 | 2.0초 | 맞음 2, **틀림 1**(관찰 2) |
| asp-008 | 줄 범위 | `admin/member/list_excel.asp` (UTF-8) | 15-18 — 가입일 최신순 정렬 | 3/3 | 1.0 | 1.7초 | 맞음 |
| asp-009 | 줄 범위, HTML 섞임 | `admin/order/edit.asp` (CP949) | 65-69 — 취소 주문 문구 (`<% If %>`가 HTML을 사이에 둠) | 3/3 | 1.0 | 2.7초 | 맞음 |
| asp-010 | 줄 범위, HTML 섞임 | `admin/order/list.asp` (CP949) | 86-92 — 고객 칸에 고객 번호 (`Do While`가 범위 밖에서 열림) | 3/3 | 1.0 | 1.7초 | 맞음 |
| asp-011 | SP 호출 변경 | `admin/order/list.asp` (CP949) | 24-37 — `usp_SearchOrdersDynamic`으로 바꿈 | 3/3 | 1.0 | 3.0초 | 맞음 |
| asp-012 | SP 호출 변경 | `admin/order/save.asp` (ASCII) | 21-27 — `Command` → `DbConn.Execute` 문자열 | 3/3 | 1.0 | 2.7초 | 맞음(관찰 4) |

「평균 시간」은 실행기 기준 벽시계 시간(정수 초의 평균)이다. 「사람이 읽은 판정」은 에이전트가 결과 diff 36개를 읽고 매긴 것이다.

## 4. 합계

| 지표 | 값 |
| :--- | :--- |
| 실행 | 36 |
| 종료 0 | 36 (100%) |
| 종료 1·2·3·4·5 | 0 |
| 예상 문자열 판정 통과 | 36/36 |
| 사람이 읽고 맞다고 본 결과 | 32/36 (틀림 4: asp-006 3회, asp-007 1회) |
| 평균 시도 | 1.0 |
| 시도별 실패 판정 | 없음(패치·인코딩·구조·안전·이름 모두 0) |
| 컴파일 검사 | 건너뜀 36(Windows가 아님) |
| SP 확인 | 건너뜀 36(개발 DB 연결 없음) |
| 편집 영역 밖 바이트 | 36/36 같음 |
| 시간 | 전체 89초, 평균 2.25초(모델 생성 평균 1.5초) |
| `summary.json`의 `asp` 블록 | 36/36 기록(`executed: false`, SP 이름, include 찾음 수: `list.asp`·`edit.asp` 4, `save.asp` 3, `list_excel.asp` 2, 못 찾음 0) |

## 5. 관찰 (결과 diff를 읽고)

기록 원본(사례별 `console.log`, `summary.json`, 결과 파일, `run/attempt-*/`)은 로컬 임시 폴더에 두었고 저장소에 포함하지 않는다.

1. **asp-006: 컴파일되지만 실행하면 멈추는 코드(3/3 같은 출력).** 모델은 `If Not IsNumeric(Request("pageSize")) Or CLng(Request("pageSize")) < 1 Or … Then`을 썼다. VBScript의 `Or`는 단락 평가를 하지 않으므로 `pageSize`가 없거나 숫자가 아니면 `CLng("")`이 실행되어 「형식이 일치하지 않습니다」로 페이지가 멈춘다. 매개변수 없이 여는 평소 목록 화면이 깨지는 수정이다. 구조·안전·이름 검사로는 잡을 수 없고 Windows `cscript` 컴파일 검사로도 잡히지 않는다(런타임 오류). 프롬프트 규칙 후보: 「VBScript의 `And`·`Or`는 단락 평가를 하지 않는다. 변환은 검사 뒤 별도 `If`에서 하라」.
2. **asp-007 1회: 기존 줄을 지움.** 3회째 출력이 `reason = Trim(Request.Form("reason"))`을 SEARCH에 넣고 `Dim maxReasonLen` 등으로 바꿔, 취소 사유를 폼에서 읽는 줄이 사라졌다. 이후 `If reason = "" Then reason = "admin"` 때문에 사유가 늘 `admin`이 된다. 이름 검사(새 이름은 모두 선언됨)와 구조 검사는 통과했고, 예상 문자열 `Left(`도 있었다. 줄 범위 대상에서 범위 안의 기존 문장을 지우는 것은 판정 대상이 아니다. diff에는 `-` 줄로 분명히 보이므로 사람 검토가 잡을 수 있다.
3. **asp-005: 기존 위험 줄까지 고쳤다(3/3).** `@Status` 조건을 더하면서 같은 범위의 기존 `", @To = '" & Request("toDate") & "'"`을 `SafeSQL(toDate)`로 바꿨다. 요청하지 않은 변경이지만 안전 쪽이다. 「이미 위험한 줄을 고치면 새 문장으로 보고 거부」 규칙(재시도 비용 우려)은 이번 사례에서 재시도를 일으키지 않았다. `@Status`는 `SafeSQL`로 감싼 문자열(`'1'`)로 넘겨 SQL Server의 암시적 변환에 기댄다. `CInt`가 더 맞지만 틀리지는 않다.
4. **asp-012: 요청대로 매개변수 방식을 문자열 연결로 바꿨다(3/3).** `CLng(orderId)`와 `SafeSQL(reason)`로 감싸 안전 검사를 통과했다. 요청을 그대로 따른 결과지만 `CreateParameter`보다 약한 방식으로 내려간 것이고, pelly는 「요청 의도가 맞는가」를 판정하지 않는다. 새 `Dim sql`을 범위 안에 두어 `Option Explicit`도 지켰다.
5. **HTML을 사이에 둔 블록(asp-009·010)은 문제없었다.** asp-009는 `<% If CInt(status) < 2 Then %>` 앞에 `<% If status = 9 Then %> … <% ElseIf … %>`를 더했고, asp-010은 범위 밖에서 열린 `Do While` 안의 `<td>`만 고쳤다. 구조 검사가 파일 전체로 짝을 세므로 범위 밖에서 열린 블록 때문에 거부된 적이 없다.
6. **인코딩:** 순수 ASCII `dbconn.asp`·`save.asp`는 `asp.encoding` 기본값 CP949로, `list_excel.asp`는 `CodePage=65001`로 UTF-8로 정해졌다. 한글을 넣은 CP949 사례(asp-002·009)도 결과 파일을 CP949로 읽을 수 있었다.
7. **너무 쉬운 사례일 수 있다.** 코퍼스 파일이 짧고(최대 115줄) 요청이 한두 줄 변경이라 첫 시도 100%는 일반 성공률이 아니다. 재시도·검사 실패 분포는 이번에 보지 못했다. 이름 검사의 경고(못 찾은 include)도 코퍼스에 못 찾는 include가 없어 나오지 않았다.

## 6. 의미와 한계

- 표본이 작고(12건 × 3회) 같은 입력에 모델 출력이 거의 같았다(asp-007만 달랐음). 3회 반복은 분산보다 재현성을 보여 준다.
- **예상 문자열 판정과 pelly 판정은 「요청한 것이 들어갔는가」만 본다.** 이번 측정의 실제 위험은 판정을 통과한 런타임 오류(asp-006)와 지워진 기존 줄(asp-007)이었다. 둘 다 사람이 diff를 읽어야 잡힌다. 검토 화면의 고정 문구가 이 한계를 알린다.
- 컴파일 검사(Windows `cscript`)는 재지 못했다. 현장 Windows에서는 컴파일 오류 재시도가 더해질 수 있고, 이번 틀린 4회는 그 검사로도 잡히지 않는다.
- SP 확인은 개발 DB 연결이 없어 재지 못했다. asp-011·012의 SP 이름은 코퍼스 SQL(`eval/corpus/Sql`)에 있는 것이다.
- 사례 위치는 설계 §7의 `eval/benchmark/tasks/`가 아니라 `eval/benchmark/asp/`다(전용 실행기 `run.sh`와 함께 둠, 기존 벤치마크 50건 집합에 섞지 않음).
- 개선 후보: (1) 프롬프트에 VBScript 단락 평가 없음 규칙, (2) 줄 범위 대상에서 기존 `Request` 대입 줄이 사라지면 검토 화면에 경고, (3) 재시도·검사 실패를 볼 수 있는 더 큰 페이지와 더 어려운 요청(예: 여러 블록에 걸친 변경)으로 사례 보강.
- 측정 뒤 반영(이 측정 결과에는 들어 있지 않음): (1)은 `system.asp.txt` 규칙 9, (2)는 검토 경고 「논리」(대상 안의 `If`·`ElseIf` 한 조건에서 같은 식을 `IsNumeric`·`IsDate`로 검사하고 `CLng`·`CInt`·`CDbl`·`CDate`로 변환하면 「And/Or는 단축 평가를 하지 않습니다: …」, 원본 대상에서 `Request`를 읽던 변수의 대입이 사라지면 「기존 입력값 읽기 줄이 사라졌습니다: …」). 경고일 뿐 거부하지 않습니다. (3)은 그대로 남은 후보입니다.

---

# A2 예비 측정 (명세 asp 작업·SP→ASP 영향)

> **예비 — Windows·현장 측정 별도.** 개발 맥(macOS)의 로컬 Ollama로 잰 값이다. **개발 SQL Server 연결(`PELLY_SQL_CONNECTION`)을 설정하지 않았으므로 `sql` 작업이 든 사례 2건(a2-001·a2-002)의 `apply`는 돌리지 못했다**(「건너뜀(DB 없음)」). 돌린 것은 `spec check --impact`(3사례)와 asp 작업만 있는 a2-003의 `apply`다. 그래서 A2의 핵심인 「sql→asp 순서 실행·그룹 되돌리기·바뀐 뒤 서명·apply 뒤 경고」는 이 측정에서 **재지 못했다**(자체 테스트는 통과). KPI 판정에는 쓰지 않는다.

설계: [docs/superpowers/specs/2026-11-09-pelly-asp-a2-design.md](superpowers/specs/2026-11-09-pelly-asp-a2-design.md) §6 · 사례와 실행기: `eval/benchmark/asp-a2/`(`run.sh`)

## A2-1. 코퍼스와 사례

같은 SP `dbo.usp_OrderList`(`eval/corpus/Asp/db/proc/usp_OrderList.sql`, 매개변수 `@CustomerId`·`@From`·`@To`, 모두 기본값 NULL)를 세 방식으로 부르는 페이지를 `eval/corpus/Asp`에 더했다. 기존 페이지는 고치지 않았다.

| 파일 | 호출 방식 |
| :--- | :--- |
| `admin/order/summary.asp` | `DbConn.Execute("exec dbo.usp_OrderList …")`, `LoadOrderRows` 안(인수 모름) |
| `admin/order/export.asp` | `cmd.CommandText = "{call dbo.usp_OrderList(?, ?, ?)}"` (18행, 인수 3개) |
| `admin/order/dashboard.asp` | 래퍼 `ExecSP("dbo.usp_OrderList", …)`(`inc/func_report.inc`, 코퍼스 `pelly.json`의 `asp.spCallFunctions`) |

| 사례 | 내용 | 기대 |
| :--- | :--- | :--- |
| a2-001 | sql(`@Status` 조건) + asp(`summary.asp#LoadOrderRows`), 그룹 G1 | 영향 경고에 `export.asp:18`·`dashboard.asp:14`만, `apply` 종료 0, 두 파일에 `@Status` |
| a2-002 | sql(`@Status` 추가) + asp(`export.asp#L18-21`)만, 그룹 없음 | 영향 경고에 `summary.asp:15`·`dashboard.asp:14`, apply 뒤 경고(기본값 있는 매개변수라 「기존 호출은 그대로 동작할 수 있음」)와 `aspCallersNotUpdated` 2곳 이상 |
| a2-003 | asp 두 작업, 같은 파일 `summary.asp`의 `#FormatRow`·`#LoadOrderRows` | `spec check` 오류 0(`AspLinesAfterEdit` 없음), `apply` 종료 0, 영향 경고 없음 |

## A2-2. 조건

| 항목 | 값 |
| :--- | :--- |
| 모델 | 태그 `qwen3.6:35b-a3b-q4_K_M`, 로컬 Ollama `http://localhost:11434` |
| 기기 | Mac (Apple M5 Pro), macOS Darwin 25.6.0 |
| 코드 | 브랜치 `a2` (코퍼스·사례를 더하기 전 `b9cda3a`), 생각 모드 `retry`(기본) |
| 실행 일시 | 2026-11-09, 사례 3건 × 3회 |
| 실행 방법 | `eval/benchmark/asp-a2/run.sh --repeat 3` — 회마다 `eval/corpus/Asp`를 새 임시 폴더에 복사(`.git` 폴더로 작업 공간 루트), `pelly spec check changes/<id>.md --impact` 뒤 `pelly apply changes/<id>.md --yes` |
| 왜 `pelly-eval apply`가 아닌가 | 그 실행기는 `eval/corpus` 전체를 작업 공간으로 쓰고 사례가 승인된 S6 정답 명세 12건이다. A2 사례는 `Asp`가 작업 공간 루트여야(코퍼스 `pelly.json`의 래퍼 설정) 하고, 영향 경고·`apply.json`의 `aspCallersNotUpdated` 기대를 표현하지 못한다 |
| 설정 | 코퍼스 `pelly.json`(`asp.siteRoot` `.`, `asp.sqlSanitizers`, `asp.spCallFunctions` `ExecSP`), 그 밖은 기본값. `sql.connectionString` 없음 |

## A2-3. 결과

| 사례 | `spec check --impact` | `apply` | 평균 시간 |
| :--- | :-: | :--- | -: |
| a2-001 | 3/3 기대대로 | 건너뜀(DB 없음) | - |
| a2-002 | 3/3 기대대로 | 건너뜀(DB 없음) | - |
| a2-003 | 3/3 기대대로 | 3/3 종료 0, 모두 첫 시도, 기대 문자열(`FormatNumber(`) 있음 | 5초 |

**예비 측정 결과: 영향 경고 판정 9/9(사례 3 × 3회) 기대대로, `apply` 실행은 a2-003만 3/3 성공(종료 0, 첫 시도), a2-001·a2-002의 `apply`는 개발 DB가 없어 건너뜀(0/6 측정).** 실패한 실행은 없지만, 건너뛴 부분이 A2의 중심이라 이 수치로 A2 전체를 판단할 수 없다.

## A2-4. 관찰

1. **DB 없이도 ASP 영향 경고는 나온다.** 개발 DB가 없으면 「영향 탐색(SQL)… 접속하지 못해」 경고가 한 번 나오고, ASP 호출 색인은 그대로 돌아 명세 밖 호출 경고가 나왔다(래퍼 `ExecSP`·`{call}`·직접 `Execute` 세 방식 모두). 인수 개수는 `{call}`만 알고(3개) 나머지는 「인수 모름」이다.
2. **`sql` 작업은 DB 없이 `apply`가 시작도 못 한다.** 사전 점검에서 「SQL 연결 문자열이 없습니다」로 종료 4(`EnvError`)가 된다. 명세 밖 호출 경고는 그 앞에 `apply.json`에 남는다. 그래서 a2-001·a2-002의 `apply`는 `PELLY_SQL_CONNECTION`(개발 DB) 설정 뒤 `run.sh`를 다시 돌려야 잰다. 허용 목록(`sql.allowedHosts`, `sql.allowedDatabases`) 설정이 필요할 수 있다.
3. **a2-003 두 작업이 같은 파일에서 순서와 상관없이 성공했다.** 프로시저 대상이라 `AspLinesAfterEdit` 오류가 없었고, 3회 모두 `FormatRow`에 `FormatNumber(amount, 0)`, `LoadOrderRows`에 `customerId <= 0`이면 `NULL`을 넘기는 분기가 들어갔다(같은 출력). 컴파일 검사는 맥에서 「건너뜀」, SP 확인은 「건너뜀(개발 DB 연결이 설정되지 않았습니다)」이다. 이 사례의 `LoadOrderRows`가 부르는 `usp_OrderList`는 바뀌지 않은 SP라 파일 기준 서명이 없어서다(설계 §4.4).
4. **한계:** 사례가 3건, 코퍼스가 작고, 모델이 쉬운 요청을 첫 시도에 풀었다. 판정은 「요청한 문자열이 들어갔는가」와 경고 기대만 본다. 결과 코드를 사람이 읽는 검토는 이번 a2-003의 diff만 했다. Windows `cscript` 컴파일과 DB 연결 측정은 `deploy/field-results.md`에서 사람이 한다.

### 개발 DB 연결 측정 (2026-11-09, 1회)

로컬 코퍼스 SQL Server 컨테이너의 `PellyCorpus` DB에 `PELLY_SQL_CONNECTION`과 허용 목록 환경 변수(`PELLY__sql__allowedHosts__0`, `PELLY__sql__allowedDatabases__0`)를 주고 `run.sh --repeat 1`을 돌렸다. 모델과 엔드포인트는 위와 같다.

| 사례 | `spec check --impact` | `apply` | 시간 |
|---|---|---|---|
| a2-001 | 기대대로 | 종료 0, 두 파일에 `@Status` | 24초 |
| a2-002 | 기대대로 | 종료 0, 기대 문구 1개 없음(아래) | 16초 |
| a2-003 | 기대대로 | 종료 0 | 5초 |

1. **a2-001(그룹 G1)의 sql→asp 순서 실행이 개발 DB에서 성공했다.**
2. **a2-002는 사례 문장이 모호했다.** 모델이 `@Status`를 매개변수 끝이 아니라 `@CustomerId` 다음에 넣고 `export.asp`의 `?`와 `CreateParameter`도 같은 자리에 끼웠다. 끝에 더한 것이 아니므로 pelly가 「기존 호출은 그대로 동작할 수 있음」을 붙이지 않은 것은 설계(§4.5)대로다. 고치지 않은 `?` 세 개짜리 호출은 위치로 묶이므로 실제로 값이 어긋난다. 사례 문장에 「맨 끝에」를 넣어 기대와 맞췄다. 바꾼 문장으로 다시 잰 1회는 기대대로였다(종료 0, 「기존 호출은 그대로 동작할 수 있음」 경고, 15초).
3. **처음 실행은 세 사례 모두 종료 4였다.** `run.sh`가 메인 체크아웃의 오래된 빌드(A2 이전)를 써서 `asp` 작업 줄을 읽지 못했다. 이제 벤치마크 `run.sh`(asp, asp-a2, chartest, web)는 시작할 때 pelly를 빌드한다.
