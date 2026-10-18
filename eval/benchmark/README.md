# 벤치마크 작업 정의

`tasks/*.yaml` 한 파일이 작업 한 건입니다. 실행기 `eval/PellyCoder.Eval`이 이 정의를 읽어 실행하고 판정합니다(`dotnet run --project eval/PellyCoder.Eval -- --help`).
정의 형식은 `tests/PellyCoder.Integration.Tests/Benchmark/BenchmarkDefinitionTests.cs`가 검증합니다.

## 스키마

| 필드 | 형식 | 설명 |
| :--- | :--- | :--- |
| `id` | 문자열 | 파일 이름(확장자 제외)과 같음. 형식 `^(cs-(modern\|legacy)\|sql)-\d{3}$` |
| `kind` | `cs` \| `sql` | 작업 종류 |
| `file` | 문자열 | `eval/` 기준 상대 경로 |
| `method` | 문자열 (선택) | `cs`: 대상 메서드 이름. `method`와 `line` 중 하나만 |
| `line` | 정수 (선택) | `cs`: 대상 멤버 안의 줄 번호(1부터). IDE External Tools 사용을 흉내 냄 |
| `object` | 문자열 (선택) | `sql`: 대상 객체 이름. 파일에 객체가 여러 개면 필수 |
| `request` | 문자열 | 한국어 요청문 (사용자가 입력하는 그대로) |
| `judge.build` | bool | `cs` 전용. 빌드 통과 필요 |
| `judge.sqlCompile` | bool | `sql` 전용. NOEXEC 컴파일 통과 필요 |
| `judge.tests` | 문자열 목록 (선택) | 통과해야 하는 테스트 전체 이름 |
| `judge.mustContain` | 문자열 목록 (선택) | 수정된 대상에 있어야 하는 문자열. **원본 대상에는 없어야 함** |
| `judge.mustAdd` | 문자열 목록 (선택) | 수정된 대상에서 원본보다 **더 많이** 나와야 하는 문자열 (원본에 이미 있는 문자열을 새로 추가했는지 확인) |
| `judge.mustNotContain` | 문자열 목록 (선택) | 수정된 대상에 없어야 하는 문자열. **원본 대상에는 있어야 함** |

## 공통 판정 규칙
* **대상(멤버 또는 SQL 객체 배치)이 원본과 같으면 실패**입니다. 아무것도 고치지 않은 패치가 성공으로 집계되지 않게 하기 위함입니다.
* 대상 범위: `method` 지정이면 그 멤버, `line` 지정이면 그 줄을 감싸는 멤버, `sql`이면 대상 객체 배치.
* **공백 차이뿐인 수정(줄바꿈·들여쓰기만 바뀐 재포맷)도 "원본과 같음"으로 셉니다**(공백 연속을 한 칸으로 접어 비교).
* 정의의 `mustContain`·`mustNotContain`이 원본 대상(실행기와 같은 범위) 기준으로 의미가 있는지는 `BenchmarkDefinitionTests.Judges_RejectUnmodifiedTarget`이, sql 대상이 객체 배치 하나인지는 `SqlTasks_TargetIsAnObjectBatch`가 검사합니다.

## 주의
* 코퍼스의 `*_CurrentlyCancels` 테스트는 **수정 전** 동작을 고정한 것입니다. 해당 벤치마크 작업이 성공하면 이 테스트는 실패하는 것이 정상이므로 `judge.tests`에 넣지 않습니다.
* 레거시 작업(`cs-legacy-*`)의 빌드 판정은 Windows에서만 가능합니다. Mac에서는 "빌드 판정 생략" 모드로 실행합니다(설계 §10.6).

## 실행
```bash
export PELLY_SQL_CONNECTION="Server=localhost,14333;Database=PellyCorpus;User Id=sa;Password=<sa 비밀번호>;TrustServerCertificate=True"
dotnet run --project eval/PellyCoder.Eval -- --config <pelly.json>            # 30건 전체
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

## 현재 구성 (M2, 30건)
C# 18건(Legacy 10, Modern 8) + SQL 12건. 시그니처를 바꾸는 요청(async 전환 등)은 넣지 않습니다(MemberShapeGuard가 막는 의도된 동작). SQL 매개변수 추가는 허용합니다.
모든 작업에 내용 판정(`mustContain`·`mustAdd`·`mustNotContain`)이 하나 이상 있습니다(`AllTasks_HaveContentCriterion`) — 빌드·컴파일만으로는 의미 없는 수정도 통과하기 때문입니다.

| id | 대상 | 지정 방식 | 요청 종류 |
| :--- | :--- | :--- | :--- |
| cs-legacy-001 | Shop.Web `OrderService.Cancel` (CP949) | method | 로직 수정 |
| cs-legacy-002 | Shop.Batch `SettlementBatch.Execute` (CP949, ADO.NET) | line | null 처리 |
| cs-legacy-003 | Shop.Data `OrderDao.SearchOrders` (Dapper 1.50) | method | Dapper 쿼리 변경 |
| cs-legacy-004 | Shop.Web `OrderController.Cancel` (MVC) | method | 로직 수정 |
| cs-legacy-005 | Shop.Web `OrderController.Detail` (MVC) | line | 로직 수정 |
| cs-legacy-006 | Shop.Web `OrderService.Cancel` (CP949) | line | 로직 수정 |
| cs-legacy-007 | Shop.Batch `SettlementBatch.Execute` (CP949, ADO.NET) | method | 쿼리 조건 |
| cs-legacy-008 | Shop.Batch `SettlementBatch.BuildReport` (CP949, 긴 멤버) | method | 로직 수정 |
| cs-legacy-009 | Shop.Data `OrderDao.GetOrder` (Dapper 1.50) | method | Dapper 쿼리 변경 |
| cs-legacy-010 | Shop.Data `OrderDao.UpdateStatus` (Dapper 1.50) | method | Dapper 쿼리 변경 |
| cs-modern-001 | Shop.Api `OrderService.CancelAsync` | method | 로직 수정 |
| cs-modern-002 | Shop.Api `OrderService.CalculateRefundAsync` | method | 로직 수정 |
| cs-modern-003 | Shop.Api `OrderRepository.GetItemsAsync` | method | Dapper 쿼리 변경 |
| cs-modern-004 | Shop.Api `OrderRepository.SearchAsync` | line | null 처리 |
| cs-modern-005 | Shop.Api `OrdersController.Get` | method | 로직 수정 |
| cs-modern-006 | Shop.Api `OrdersController.Search` | method | 로직 수정 |
| cs-modern-007 | Shop.Api `OrderService.SearchAsync` | method | null 처리 |
| cs-modern-008 | Shop.Jobs `DailySettlementJob.Run` | method | 로직 수정 |
| sql-001 | `usp_CancelOrder` (SSMS 스크립트 형태) | 파일 | 조건 추가 |
| sql-002 | `usp_SearchOrders` | 파일 | 매개변수·조건 추가 |
| sql-003 | `fn_OrderTotal` (함수) | 파일 | 조건 추가 |
| sql-004 | `Settlement_All.sql`의 `usp_SettleMonthly` (한 파일 여러 객체) | object | 컬럼 추가 |
| sql-005 | `usp_SearchOrdersDynamic` (동적 SQL) | 파일 | 로직 수정 |
| sql-006 | `usp_SearchOrders` | 파일 | 조건 추가 |
| sql-007 | `usp_BuildSettlement` (#temp 테이블) | 파일 | 조건 추가 |
| sql-008 | `usp_GetOrderDetail` (결과 집합 3개) | 파일 | 컬럼 추가 |
| sql-009 | `usp_SearchOrders` | 파일 | 컬럼 추가 |
| sql-010 | `Settlement_All.sql`의 `usp_SettleMonthly` | object | JOIN 변경 |
| sql-011 | `vw_ShippedOrders` (뷰) | 파일 | 컬럼 추가 |
| sql-012 | `Settlement_All.sql`의 `usp_SettleDaily` | object | 조건 추가 |
