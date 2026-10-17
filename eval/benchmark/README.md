# 벤치마크 작업 정의

`tasks/*.yaml` 한 파일이 작업 한 건입니다. M3의 `PellyCoder.Eval`이 이 정의를 읽어 실행하고 판정합니다.
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
* 정의의 `mustContain`·`mustNotContain`이 원본 기준으로 의미가 있는지는 `BenchmarkDefinitionTests.Judges_RejectUnmodifiedTarget`이 검사합니다.

## 주의
* 코퍼스의 `*_CurrentlyCancels` 테스트는 **수정 전** 동작을 고정한 것입니다. 해당 벤치마크 작업이 성공하면 이 테스트는 실패하는 것이 정상이므로 `judge.tests`에 넣지 않습니다.
* 레거시 작업(`cs-legacy-*`)의 빌드 판정은 Windows에서만 가능합니다. Mac에서는 "빌드 판정 생략" 모드로 실행합니다(설계 §10.6).

## 현재 구성 (M0, 10건)
| id | 대상 | 지정 방식 |
| :--- | :--- | :--- |
| cs-legacy-001 | Shop.Web `OrderService.Cancel` (CP949) | method |
| cs-legacy-002 | Shop.Batch `SettlementBatch.Execute` (CP949, ADO.NET) | line |
| cs-legacy-003 | Shop.Data `OrderDao.SearchOrders` (Dapper 1.50) | method |
| cs-modern-001 | Shop.Api `OrderService.CancelAsync` | method |
| cs-modern-002 | Shop.Api `OrderService.CalculateRefundAsync` | method |
| sql-001 | `usp_CancelOrder` (SSMS 스크립트 형태) | 파일 |
| sql-002 | `usp_SearchOrders` | 파일 |
| sql-003 | `fn_OrderTotal` (함수) | 파일 |
| sql-004 | `Settlement_All.sql`의 `usp_SettleMonthly` (한 파일 여러 객체) | object |
| sql-005 | `usp_SearchOrdersDynamic` (동적 SQL) | 파일 |
