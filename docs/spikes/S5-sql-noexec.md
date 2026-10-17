# S5. NOEXEC 최소 권한·배치 순서

## 질문
`SET NOEXEC ON`으로 `ALTER PROCEDURE`를 컴파일 검사할 때 필요한 최소 권한은? 어떤 오류가 잡히고 어떤 오류가 빠지는가? 배치 순서와 트랜잭션은 어떻게 해야 하는가? (PRD Q4, 설계 §8.10)

## 환경
| 항목 | 값 |
| :--- | :--- |
| SQL Server | `mcr.microsoft.com/mssql/server:2022-latest` (Docker, Apple Silicon에서 amd64 에뮬레이션) — 컨테이너 기동 문제 없음 |
| DB | `PellyCorpus` (Task 6 코퍼스) |
| 클라이언트 | `Microsoft.Data.SqlClient` 7.1.1 |
| 날짜 | 2026-10-17 |

## 방법
* `spikes/S5-sql-noexec/setup.sql` — 로그인·사용자 4종: ④ 권한 없음(public만) ① `VIEW DEFINITION` ② ①+`SHOWPLAN` ③ ①+`ALTER ON SCHEMA::dbo`.
* `spikes/S5-sql-noexec/noexec.cs` — 사용자별로 두 가지 순서로 실행:
  * `noexec`: `SET NOEXEC ON` 배치 → 대상 `ALTER PROCEDURE dbo.usp_CancelOrder` 단독 배치 → `SET NOEXEC OFF` 배치
  * `tran+noexec`: `BEGIN TRAN` 배치 → 위 순서 → `ROLLBACK`
* 대상 문장 4종: ⓐ 정상 ⓑ 없는 컬럼 ⓒ 없는 테이블 ⓓ 구문 오류. 실행마다 sa로 `OBJECT_DEFINITION`을 비교해 객체가 바뀌지 않았는지 확인.
* 참고: sa로 NOEXEC 없이 실제 `ALTER`(트랜잭션 안, 끝에 롤백).
* 메타데이터 가시성: 사용자별 `OBJECT_ID`, `sys.columns`, `sys.dm_sql_referenced_entities` 조회(`noexec.cs` 마지막 단계). 포트를 `127.0.0.1`로 바꾼 뒤 컨테이너를 다시 만들어 전체를 재실행했다.

## 측정 결과
**컴파일 오류 검출** (원시: `results.csv`) — 4개 사용자 × 2개 순서 모두 같은 결과

| 사례 | NOEXEC 결과 | 실제 ALTER(sa) | 객체 정의 변경 |
| :--- | :--- | :--- | :--- |
| ⓐ 정상 | 오류 없음 | 오류 없음 | 없음 |
| ⓑ 없는 컬럼 | **207** `Invalid column name 'CancelReasonX'` (L14) | 207 | 없음 |
| ⓒ 없는 테이블 | **오류 없음** | **오류 없음** | 없음 |
| ⓓ 구문 오류 | **102** `Incorrect syntax near 'SELEC'` (L8) | 102 | 없음 |

* 줄 번호(`SqlError.LineNumber`)는 **배치 기준**(대상 문장 첫 줄 = 1)이다.
* 권한이 전혀 없는 사용자도 NOEXEC에서 207·102를 받는다. NOEXEC 컴파일은 `ALTER` 권한을 검사하지 않는다.
* `BEGIN TRAN`/`ROLLBACK`을 함께 써도 결과·부작용 차이가 없다.
* ⓒ는 지연 이름 확인(Deferred Name Resolution) 때문에 NOEXEC는 물론 실제 ALTER에서도 잡히지 않는다.

**메타데이터 가시성** (원시: `results.csv` 마지막 표 — `noexec.cs`가 함께 출력)
| 사용자 | `OBJECT_ID('dbo.Orders')` | `sys.columns`(Orders) | `OBJECT_ID('dbo.usp_CancelOrder')` | `dm_sql_referenced_entities` |
| :--- | :--- | ---: | :--- | ---: |
| ④ 권한 없음 | NULL | 0 | NULL | 0 |
| ① VIEW DEFINITION | 1061578820 | 14 | 1493580359 | 9 |

## 결론
* **최소 권한: DB 수준 `GRANT VIEW DEFINITION`.** `ALTER`·`SHOWPLAN`은 필요 없다. 권한이 없으면 컴파일 검사는 되지만, 객체 존재 검사가 모든 테이블을 "없음"으로 판정하고 스키마 요약이 비게 된다.
* **객체 존재 검사(설계 §8.10 3단계)는 반드시 필요하다.** 없는 테이블은 SQL Server가 어떤 방식으로도 알려주지 않는다.
* **배치 순서 확정:** `SET NOEXEC ON` 배치 → 대상 문장 단독 배치 → `SET NOEXEC OFF` 배치. 트랜잭션은 결과에 영향이 없지만, 안전장치로 남긴다(같은 연결에서 `BEGIN TRAN` → … → `IF @@TRANCOUNT > 0 ROLLBACK`).
* 진단 줄 번호 = `SqlError.LineNumber` + (파일 안 대상 배치의 시작 줄 − 1).

## 설계 반영
| 위치 | 변경 |
| :--- | :--- |
| §8.10 SqlSyntaxVerifier 3번 | 배치 순서 확정(위). 트랜잭션은 유지 |
| §8.10 / PRD FR-07 최소 권한 | "`VIEW DEFINITION` 등" → **"DB 수준 `VIEW DEFINITION`만 필요(`ALTER`·`SHOWPLAN` 불필요)"** |
| §8.10 SqlSyntaxVerifier 2번 | 객체 존재 검사 필수 확인. `#temp` 제외 규칙 유지 |
| §8.13 doctor | SQL 권한 검사: 알려진 테이블 하나의 `OBJECT_ID`가 NULL이면 "VIEW DEFINITION 권한 필요"로 Fail |
| PRD §10 Q4 | 해소 |
