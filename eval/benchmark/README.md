# 벤치마크 작업 정의

`tasks/*.yaml` 한 파일이 작업 한 건입니다. 실행기 `eval/PellyCoder.Eval`이 이 정의를 읽어 실행하고 판정합니다(`dotnet run --project eval/PellyCoder.Eval -- --help`).
정의 형식은 `tests/PellyCoder.Integration.Tests/Benchmark/BenchmarkDefinitionTests.cs`가 검증합니다.

## 스키마

| 필드 | 형식 | 설명 |
| :--- | :--- | :--- |
| `id` | 문자열 | 파일 이름(확장자 제외)과 같음. 형식 `^(cs-(modern\|legacy)\|sql)-\d{3}$` |
| `kind` | `cs` \| `sql` | 작업 종류 |
| `category` | `mvc` \| `adonet` \| `dapper` (선택) | Legacy 작업의 앱 종류(MVC 컨트롤러·서비스 / ADO.NET 배치 / Dapper 리포지토리). Legacy 작업에만 두고 구성 테스트가 셉니다. 합격 판정의 범주(cs-legacy·cs-modern·sql)는 id 접두사입니다 |
| `file` | 문자열 | `eval/` 기준 상대 경로 |
| `method` | 문자열 (선택) | `cs`: 대상 메서드 이름. `method`와 `line` 중 하나만 |
| `line` | 정수 (선택) | `cs`: 대상 멤버 안의 줄 번호(1부터). IDE External Tools 사용을 흉내 냄 |
| `object` | 문자열 (선택) | `sql`: 대상 객체 이름. 파일에 객체가 여러 개면 필수 |
| `request` | 문자열 | 한국어 요청문 (사용자가 입력하는 그대로) |
| `judge.build` | bool | `cs` 전용. 빌드 통과 필요 |
| `judge.sqlCompile` | bool | `sql` 전용. NOEXEC 컴파일 통과 필요 |
| `judge.tests` | 문자열 목록 (선택) | 수정 뒤 통과해야 하는 테스트 전체 이름. 판정 프로젝트(`judges/`)와 코퍼스 테스트에서 찾음. **원본에서는 실패해야 함** |
| `judge.failingTests` | 문자열 목록 (선택) | 수정 뒤 **실패해야** 하는 코퍼스 테스트 전체 이름(수정 전 동작을 고정한 `*_CurrentlyCancels` 등). 코퍼스 테스트 프로젝트에서만 찾음. **원본에서는 통과해야 함** |
| `judge.mustContain` | 문자열 목록 (선택) | 수정된 대상에 있어야 하는 문자열. **원본 대상에는 없어야 함** |
| `judge.mustAdd` | 문자열 목록 (선택) | 수정된 대상에서 원본보다 **더 많이** 나와야 하는 문자열 (원본에 이미 있는 문자열을 새로 추가했는지 확인) |
| `judge.mustNotContain` | 문자열 목록 (선택) | 수정된 대상에 없어야 하는 문자열. **원본 대상에는 있어야 함** |

## 공통 판정 규칙
* **대상(멤버 또는 SQL 객체 배치)이 원본과 같으면 실패**입니다. 아무것도 고치지 않은 패치가 성공으로 집계되지 않게 하기 위함입니다.
* 대상 범위: `method` 지정이면 그 멤버, `line` 지정이면 그 줄을 감싸는 멤버, `sql`이면 대상 객체 배치.
* **공백 차이뿐인 수정(줄바꿈·들여쓰기만 바뀐 재포맷)도 "원본과 같음"으로 셉니다**(공백 연속을 한 칸으로 접어 비교).
* 정의의 `mustContain`·`mustNotContain`이 원본 대상(실행기와 같은 범위) 기준으로 의미가 있는지는 `BenchmarkDefinitionTests.Judges_RejectUnmodifiedTarget`이, sql 대상이 객체 배치 하나인지는 `SqlTasks_TargetIsAnObjectBatch`가 검사합니다.

## 동작 판정 (`tests`·`failingTests`)
* Modern C# 작업에만 둡니다(R1). Legacy·SQL은 문자열 판정을 유지합니다.
* 판정 테스트는 코퍼스 밖 `judges/Modern/`에 있습니다(R2). 원본에서 실패하는 테스트라 코퍼스 CI와 pelly `--test`가 보지 않게 하기 위함입니다.
  * `Shop.Api.JudgeTests`: `Shop.Api`를 참조해 서비스·컨트롤러를 가짜 저장소·서비스로 실행합니다.
  * `Shop.Data.JudgeTests`: 데이터 접근 코드(`OrderRepository`, `Shop.Jobs`의 `DailySettlementJob`)를 소스로 컴파일해 가짜 Dapper·`SqlConnection`에 묶습니다. DB 없이 보낸 SQL·매개변수와 결과를 봅니다(가짜 DB가 `ORDER BY`를 행에 적용).
  * 규칙·현재 테스트 표는 `judges/Modern/Shop.Api.JudgeTests/README.md`에 있습니다.
* 종료 0 작업에 `tests`나 `failingTests`가 있으면 독립 검증이 다음을 합니다.
  1. 판정 프로젝트를 `work/<id>/judges/Modern/`으로 복사합니다.
  2. `-p:JudgeTargetRoot=<work/<id>/Modern>`으로 복원·빌드합니다.
  3. 테스트 프로젝트마다 한 번 실행하고 TRX로 테스트별 결과를 읽습니다.
* `tests`는 모두 Passed, `failingTests`는 모두 Failed여야 합니다. **이름이 결과(TRX)에 없으면 실패**로 봅니다.
* 판정 프로젝트 빌드 오류, 시간 초과, TRX 없이 끝난 실행은 실패입니다(수정한 코드가 원인일 수 있음).
* 판정 도구 오류(판정 프로젝트 복원 실패, 테스트 프로젝트 없음, 테스트 호스트를 띄우지 못함)는 독립 검증 **생략**(`판정 도구 오류: …`)입니다. 일시적인 NuGet 문제가 검증 불변 조건 위반으로 세지지 않게 하기 위함입니다. 이때 작업은 성공으로 세지 않습니다(사유 `동작 판정 생략`).
* 문자열 판정·빌드 판정과 함께 적용합니다. **동작 판정이 있으면, 다른 올바른 수정을 떨어뜨릴 수 있는 문자열 판정은 두지 않습니다.** 테스트가 못 보는 것을 확인할 때만 남깁니다.
  * 예: cs-modern-001의 `mustAdd: OrderStatus.Shipped`는 올바른 수정(Shipped 예외를 넣고, 죽은 환불 조건의 `|| Shipped`를 지움)을 실패시켜 뺐습니다(2026-10-18 실측).
  * cs-modern-002의 `mustAdd: Math.Round`·`mustNotContain: Math.Floor`도 뺐습니다. `Math.Floor(refund / 10m + 0.5m) * 10m`도 올바른 반올림입니다.
  * 그래서 지금 Modern 12건은 모두 빌드 + 동작 판정만 씁니다.
* 통과하면 보고서의 독립 검증 칸에 실행한 판정 테스트 수가 나옵니다.
* `JudgeTestDefinitionTests`가 원본 코퍼스 복사본에서 `tests`는 실패하고 `failingTests`는 통과하는지 검사합니다.

## 주의
* 코퍼스의 `*_CurrentlyCancels` 테스트는 **수정 전** 동작을 고정한 것입니다. 해당 벤치마크 작업이 성공하면 이 테스트는 실패하는 것이 정상이므로 `judge.tests`가 아니라 `judge.failingTests`에 넣습니다.
* 레거시 작업(`cs-legacy-*`)의 빌드 판정은 Windows에서만 가능합니다. Mac에서는 "빌드 판정 생략" 모드로 실행합니다(설계 §10.6).

## 실행
```bash
export PELLY_SQL_CONNECTION="Server=localhost,14333;Database=PellyCorpus;User Id=sa;Password=<sa 비밀번호>;TrustServerCertificate=True"
dotnet run --project eval/PellyCoder.Eval -- --config <pelly.json>            # 50건 전체
dotnet run --project eval/PellyCoder.Eval -- --tasks sql --config <pelly.json> # 일부 (id 접두사·글롭, --kind cs|sql)
```
* 결과 폴더: `eval/results/<yyyyMMdd-HHmmss>/`(`--out`으로 변경, git 추적 제외)
  * `report.md`(KPI 표·실패 유형·작업별 결과), `results.json`, `work/<id>/`(작업별 코퍼스 복사본과 `.pelly/runs` 로그)
* 검증 불변 조건(종료 0 = 검증 통과)은 실행기가 결과를 pelly와 별개로 다시 검증해 판정합니다. **분모는 독립 검증이 `Passed`/`Failed`인 종료 0 실행만**이고, 생략된 실행(Windows가 아닌 환경의 Legacy, 검증기 예외)은 "독립 검증 생략 N건"으로 따로 표시되며 판정된 것이 없으면 "판정 불가"입니다.
* 종료 코드: 0 실행 완료 · 1 검증 불변 조건 위반 · 2 중단 · 4 설정 오류 또는 LLM 서버 연결 불가
* **`sql` 작업에는 SQL Server가 필요합니다.**
  * 연결은 환경 변수 `PELLY_SQL_CONNECTION`(비밀번호가 있는 연결은 환경 변수로만).
  * `--config`의 pelly.json에 허용 목록을 둡니다: `{"sql":{"allowedHosts":["localhost"],"allowedDatabases":["PellyCorpus"]}}`
  * 연결이 없거나 허용 목록에 없으면 `sql` 작업은 건너뜁니다.
  * DB는 실행 전에 `eval/corpus/Sql/deploy.sh`로 코퍼스와 맞춰 둡니다. 독립 검증은 원본 객체가 오류 없이 컴파일된다고 가정합니다(절대 기준).
  * 독립 검증은 pelly의 참조 수집기·실행기를 그대로 재사용하므로 둘의 사각지대(3·4부 이름, 동적 SQL, `#temp`, 별칭과 같은 이름의 테이블)를 공유합니다. 또 절대 기준이라 코퍼스 원본이 오류 없이 컴파일된다고 가정합니다.
* 첫 측정 기록: `docs/m2b-benchmark.md`

## 현재 구성 (M3b, 50건)
C# 30건(Legacy 18, Modern 12) + SQL 20건. 시그니처를 바꾸는 요청(async 전환 등)은 넣지 않습니다(MemberShapeGuard가 막는 의도된 동작). SQL 매개변수 추가는 허용합니다.
모든 작업에 내용 판정(`mustContain`·`mustAdd`·`mustNotContain`) 또는 동작 판정(`tests`·`failingTests`)이 하나 이상 있습니다(`AllTasks_HaveContentCriterion`) — 빌드·컴파일만으로는 의미 없는 수정도 통과하기 때문입니다. `tests`는 원본에서 실패해야 하므로 같은 역할을 합니다.
Modern 12건은 모두 동작 판정을 가집니다(R1). 구성은 `Composition_Is30Cs20Sql_With18Legacy`가 검사합니다.

### Legacy 범주 (`category`)
| 범주 | 건수 | 대상 |
| :--- | :--- | :--- |
| `mvc` (MVC 컨트롤러·서비스) | 8 | Shop.Web `OrderController`, `OrderService` |
| `adonet` (ADO.NET 배치) | 5 | Shop.Batch `SettlementBatch` |
| `dapper` (Dapper 리포지토리) | 5 | Shop.Data `OrderDao` (Dapper 1.50) |

### 작업 목록
| id | 대상 | 지정 방식 | 요청 종류 | 판정 |
| :--- | :--- | :--- | :--- | :--- |
| cs-legacy-001 | Shop.Web `OrderService.Cancel` (CP949) | method | 로직 수정 | 문자열 |
| cs-legacy-002 | Shop.Batch `SettlementBatch.Execute` (CP949, ADO.NET) | line | null 처리 | 문자열 |
| cs-legacy-003 | Shop.Data `OrderDao.SearchOrders` (Dapper 1.50) | method | Dapper 쿼리 변경 | 문자열 |
| cs-legacy-004 | Shop.Web `OrderController.Cancel` (MVC) | method | 검증 추가 | 문자열 |
| cs-legacy-005 | Shop.Web `OrderController.Detail` (MVC) | line | 검증 추가 | 문자열 |
| cs-legacy-006 | Shop.Web `OrderService.Cancel` (CP949) | line | 로직 수정 | 문자열 |
| cs-legacy-007 | Shop.Batch `SettlementBatch.Execute` (CP949, ADO.NET) | method | 쿼리 조건 | 문자열 |
| cs-legacy-008 | Shop.Batch `SettlementBatch.BuildReport` (CP949, 긴 멤버) | method | 로직 수정 | 문자열 |
| cs-legacy-009 | Shop.Data `OrderDao.GetOrder` (Dapper 1.50) | method | Dapper 쿼리 변경 | 문자열 |
| cs-legacy-010 | Shop.Data `OrderDao.UpdateStatus` (Dapper 1.50) | method | Dapper 쿼리 변경 | 문자열 |
| cs-legacy-011 | Shop.Web `OrderService.GetOrder` (CP949) | method | 검증 추가 | 문자열 |
| cs-legacy-012 | Shop.Web `OrderService.Cancel` (CP949) | line | 로그 추가 | 문자열 |
| cs-legacy-013 | Shop.Web `OrderController.Cancel` (MVC) | method | 예외 처리 | 문자열 |
| cs-legacy-014 | Shop.Web `OrderController.Detail` (MVC) | method | 로직 수정 | 문자열 |
| cs-legacy-015 | Shop.Batch `SettlementBatch.Execute` (CP949, ADO.NET) | line | 명령 설정 | 문자열 |
| cs-legacy-016 | Shop.Batch `SettlementBatch.BuildReport` (CP949, 긴 멤버) | method | 정렬 변경 | 문자열 |
| cs-legacy-017 | Shop.Data `OrderDao.AddHistory` (Dapper 1.50) | method | null 처리 | 문자열 |
| cs-legacy-018 | Shop.Data `OrderDao.SearchOrders` (Dapper 1.50) | line | Dapper 쿼리 변경 | 문자열 |
| cs-modern-001 | Shop.Api `OrderService.CancelAsync` | method | 로직 수정 | 동작(+ failingTests) |
| cs-modern-002 | Shop.Api `OrderService.CalculateRefundAsync` | method | 로직 수정(반올림) | 동작 |
| cs-modern-003 | Shop.Api `OrderRepository.GetItemsAsync` | method | Dapper 쿼리 변경(정렬) | 동작 |
| cs-modern-004 | Shop.Api `OrderRepository.SearchAsync` | line | null·공백 처리 | 동작 |
| cs-modern-005 | Shop.Api `OrdersController.Get` | method | 검증 추가(경계값) | 동작 |
| cs-modern-006 | Shop.Api `OrdersController.Search` | method | 검증 추가(경계값) | 동작 |
| cs-modern-007 | Shop.Api `OrderService.SearchAsync` | method | null 처리 | 동작 |
| cs-modern-008 | Shop.Jobs `DailySettlementJob.Run` | method | 검증 추가(경계값) | 동작 |
| cs-modern-009 | Shop.Api `OrdersController.Cancel` | line | 예외 처리 | 동작 |
| cs-modern-010 | Shop.Api `OrderService.CancelAsync` | method | 검증 추가(경계값) | 동작 |
| cs-modern-011 | Shop.Api `OrderRepository.SearchAsync` | method | Dapper 쿼리 매개변수 | 동작 |
| cs-modern-012 | Shop.Api `OrderService.SearchAsync` | method | 조건 결합 검증 | 동작 |
| sql-001 | `usp_CancelOrder` (SSMS 스크립트 형태) | 파일 | 조건 추가 | 문자열 |
| sql-002 | `usp_SearchOrders` | 파일 | 매개변수·조건 추가 | 문자열 |
| sql-003 | `fn_OrderTotal` (함수) | 파일 | 조건 추가 | 문자열 |
| sql-004 | `Settlement_All.sql`의 `usp_SettleMonthly` (한 파일 여러 객체) | object | 컬럼 추가 | 문자열 |
| sql-005 | `usp_SearchOrdersDynamic` (동적 SQL) | 파일 | 로직 수정 | 문자열 |
| sql-006 | `usp_SearchOrders` | 파일 | 조건 추가 | 문자열 |
| sql-007 | `usp_BuildSettlement` (#temp 테이블) | 파일 | 조건 추가 | 문자열 |
| sql-008 | `usp_GetOrderDetail` (결과 집합 3개) | 파일 | 컬럼 추가 | 문자열 |
| sql-009 | `usp_SearchOrders` | 파일 | 컬럼 추가 | 문자열 |
| sql-010 | `Settlement_All.sql`의 `usp_SettleMonthly` | object | JOIN 변경 | 문자열 |
| sql-011 | `vw_ShippedOrders` (뷰) | 파일 | 컬럼 추가 | 문자열 |
| sql-012 | `Settlement_All.sql`의 `usp_SettleDaily` | object | 조건 추가 | 문자열 |
| sql-013 | `usp_GetOrderDetail` (결과 집합 3개) | 파일 | 컬럼 추가 | 문자열 |
| sql-014 | `Settlement_All.sql`의 `usp_SettleMonthly` | object | 매개변수·조건 추가 | 문자열 |
| sql-015 | `usp_SearchOrdersDynamic` (동적 SQL) | 파일 | 매개변수 추가(sp_executesql) | 문자열 |
| sql-016 | `vw_ShippedOrders` (뷰) | 파일 | JOIN 변경 | 문자열 |
| sql-017 | `Order_Batch.sql`의 `usp_ShipOrder` (TRY/CATCH, 트랜잭션) | object | 검증 추가 | 문자열 |
| sql-018 | `Order_Batch.sql`의 `usp_ExpirePendingOrders` (커서) | object | 이력 추가 | 문자열 |
| sql-019 | `fn_CustomerOrders` (인라인 테이블 함수) | 파일 | 조건 추가 | 문자열 |
| sql-020 | `usp_SetSellerCommission` | 파일 | 검증 추가 | 문자열 |

M3b에서 코퍼스에 더한 것: SQL 객체 넷(`Order_Batch.sql`의 `usp_ShipOrder`·`usp_ExpirePendingOrders`, `fn_CustomerOrders`, `usp_SetSellerCommission`, `deploy.sh` 목록에 추가). C# 코퍼스는 바꾸지 않았습니다.
