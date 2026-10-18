# M2b 벤치마크 첫 측정 (30건 기준선)

* **날짜:** 2026-10-18
* **환경:** macOS arm64(Darwin 25.6.0), .NET SDK 10.0.302, Ollama 0.40.0, 모델 `qwen3.6:35b-a3b-coding-nvfp4`, `llm.thinkMode = retry`(기본), `budget.think = 512`(기본), 프롬프트 변경 없음
* **SQL:** SQL Server 2022 컨테이너 `pelly-corpus-sql`(localhost,14333). 실행 직전 `eval/corpus/Sql/deploy.sh`로 `PellyCorpus`를 다시 배포해 코퍼스와 맞췄다.
  * 연결은 `PELLY_SQL_CONNECTION`(비밀번호는 기록하지 않음), 허용 목록은 저장소 밖 `--config` 파일: `{"llm":{"model":"qwen3.6:35b-a3b-coding-nvfp4"},"sql":{"allowedHosts":["localhost"],"allowedDatabases":["PellyCorpus"]}}`
* **명령:** `dotnet run --project eval/PellyCoder.Eval -- --config <pelly.json>` (30건 전체, 1회)
* **성공률은 기준선 기록일 뿐 합격 조건이 아니다.** 검증 불변 조건 위반은 0건이다.

## 1. 실행기 보고서 (`report.md` 그대로)

* 실행: 20261018-140430 · pelly 0.1.0 · 모델 `qwen3.6:35b-a3b-coding-nvfp4` (http://localhost:11434, Ollama 0.40.0) · thinkMode Retry · Unix
* 선택: --tasks * --kind *

작업 30건 — 실행 30건, 건너뜀 0건

### KPI (PRD §2.2)

| 지표 항목 | 목표 | 측정값 |
| :--- | :--- | :--- |
| 검증 불변 조건 | 100% | 100.0% (위반 0건 / 독립 검증 20건, 독립 검증 생략 10건) |
| 초기 응답 지연 (TTFT) | < 1.0초 | 중위값 0.30초 / P90 0.60초 (n=30) |
| 추론 예산 제어 (Thinking) | ≤ 512 토큰 | 최대 512 토큰, 잘림 1건 |
| 패치 포맷 파싱 성공률 | > 99.5% | 100.0% (EDIT 31회) |
| 최종 작업 성공률 | ≥ 85% | 전체 93.3% (28/30), cs 88.9% (16/18), sql 100.0% (12/12), legacy 90.0% (9/10) |
| 단일 작업 완료 시간 | 중위값 < 15초 / P90 < 30초 | 중위값 2.12초 / P90 3.94초 (n=30) |

### 실패 유형 분포

| 실패 유형 | 건수 | 작업 |
| :--- | ---: | :--- |
| Judge | 2 | cs-legacy-010, cs-modern-008 |

Legacy 빌드 판정 생략: 10건 (설계 §10.6 — Windows가 아니면 패치 적용·모양 검사까지만 판정)

### 작업별 결과

| id | 판정 | 종료 | 시도 | 독립 검증 | 시간(초) | TTFT(초) | 사유 |
| :--- | :--- | ---: | ---: | :--- | ---: | ---: | :--- |
| cs-legacy-001 | Passed | 0 | 1 | Skipped | 1.57 | 0.06 |  |
| cs-legacy-002 | Passed | 0 | 1 | Skipped | 1.21 | 0.61 |  |
| cs-legacy-003 | Passed | 0 | 1 | Skipped | 2.30 | 0.37 |  |
| cs-legacy-004 | Passed | 0 | 1 | Skipped | 2.13 | 0.30 |  |
| cs-legacy-005 | Passed | 0 | 1 | Skipped | 1.82 | 0.18 |  |
| cs-legacy-006 | Passed | 0 | 1 | Skipped | 1.47 | 0.33 |  |
| cs-legacy-007 | Passed | 0 | 1 | Skipped | 2.21 | 0.53 |  |
| cs-legacy-008 | Passed | 0 | 1 | Skipped | 3.94 | 0.90 |  |
| cs-legacy-009 | Passed | 0 | 1 | Skipped | 2.56 | 0.22 |  |
| cs-legacy-010 | Failed | 0 | 1 | Skipped | 0.94 | 0.22 | mustContain 'AND'이(가) 없음 |
| cs-modern-001 | Passed | 0 | 1 | Passed | 2.95 | 0.04 |  |
| cs-modern-002 | Passed | 0 | 1 | Passed | 2.30 | 0.60 |  |
| cs-modern-003 | Passed | 0 | 1 | Passed | 1.28 | 0.30 |  |
| cs-modern-004 | Passed | 0 | 1 | Passed | 1.27 | 0.32 |  |
| cs-modern-005 | Passed | 0 | 1 | Passed | 3.55 | 0.30 |  |
| cs-modern-006 | Passed | 0 | 1 | Passed | 1.72 | 0.24 |  |
| cs-modern-007 | Passed | 0 | 1 | Passed | 2.12 | 0.31 |  |
| cs-modern-008 | Failed | 0 | 1 | Passed | 2.13 | 0.62 | mustAdd 'DefaultCommissionRate'이(가) 늘지 않음 (1 → 1) |
| sql-001 | Passed | 0 | 1 | Passed | 2.08 | 0.06 |  |
| sql-002 | Passed | 0 | 1 | Passed | 5.58 | 0.27 |  |
| sql-003 | Passed | 0 | 1 | Passed | 1.30 | 0.32 |  |
| sql-004 | Passed | 0 | 1 | Passed | 4.05 | 0.06 |  |
| sql-005 | Passed | 0 | 1 | Passed | 0.94 | 0.20 |  |
| sql-006 | Passed | 0 | 1 | Passed | 0.95 | 0.28 |  |
| sql-007 | Passed | 0 | 1 | Passed | 2.86 | 0.29 |  |
| sql-008 | Passed | 0 | 1 | Passed | 2.25 | 0.32 |  |
| sql-009 | Passed | 0 | 1 | Passed | 1.82 | 0.28 |  |
| sql-010 | Passed | 0 | 1 | Passed | 1.01 | 0.26 |  |
| sql-011 | Passed | 0 | 2 | Passed | 12.02 | 0.30 |  |
| sql-012 | Passed | 0 | 1 | Passed | 2.40 | 0.34 |  |

## 2. Legacy 빌드 판정 생략

* Legacy 10건(`cs-legacy-001`~`010`)은 Mac이라 독립 검증이 `Skipped`(빌드 판정 생략)다(설계 §10.6).
  * 판정은 패치 적용·모양 검사·내용 판정(`mustContain` 등)까지만이다.
  * 그래서 "검증 불변 조건"의 분모는 Modern 8 + SQL 12 = 20건이다.
* 전체 판정은 Windows + LLM 환경에서만 가능하다(아직 미실행).

## 3. 실패 원인 분류

| 작업 | 실패 사유 | 분류 | 근거 |
| :--- | :--- | :--- | :--- |
| cs-legacy-010 | `mustContain 'AND'` 없음 | **모델** | 요청은 UPDATE의 WHERE에 "이미 취소된 주문 제외" 조건 추가. 모델은 SQL을 그대로 두고 익명 매개변수 객체에 쓰이지 않는 `IsCancelled = status == OrderStatus.Cancelled`만 더했다. 요청을 이행하지 않은 수정이고 판정이 맞게 거부했다. |
| cs-modern-008 | `mustAdd 'DefaultCommissionRate'` 늘지 않음 (1 → 1) | **정의** | 모델은 기존 삼항식을 `HasValue && Value > 0m && Value <= 1m ? Value : DefaultCommissionRate`로 고쳤다. 기존 `DefaultCommissionRate` 참조를 재사용하는 올바른 수정도 있는데 정의가 새 참조를 요구했다. |

* pelly 결함으로 분류한 실패는 없다.
* **정의 수정 (cs-modern-008):** 측정 뒤 `mustAdd`를 `["CommissionRate"]`로 바꿨다.
  * 범위 검사를 하는 올바른 수정은 어느 모양이든 수수료율을 한 번 더 참조한다: 삼항식 확장, 또는 `if (rate < 0 \|\| rate > 1) rate = DefaultCommissionRate;`.
  * 같은 작업만 다시 실행(`--tasks cs-modern-008`)한 결과 Passed(종료 0, 시도 1, 독립 검증 Passed)였다.
  * 위 1절 표는 수정 전 정의로 잰 원래 실행 그대로다. 수정한 정의를 적용하면 전체 29/30(96.7%), cs 17/18이 된다.
* **판정이 잡지 못하는 의미 차이 (참고):** cs-modern-008의 두 실행 모두 수수료율 0을 잘못된 값으로 취급했다(`> 0m` / `<= 0m`). 요청은 "0보다 작거나"였다. 문자열 판정의 한계로, 지정 테스트가 있는 작업(PRD §8.1)에서 다룬다.

## 4. 기타 관찰

* **sql-011:** 1차 시도가 ScriptDom 파싱 오류(`SQL46010`)로 거부됐다. 2차 시도에서 통과했다(시도 2, 12.0초).
  * 2차 시도의 thinking이 512토큰에서 잘렸다(KPI 표 "잘림 1건").
  * 재시도 경로(파싱 오류 → 피드백 → 수정)가 실제 모델로 한 번 돌았다.
* **작업 선정:** 모든 대상은 원본이 오류 없이 빌드·컴파일된다.
  * Modern: `dotnet build Modern.sln` 경고·오류 0.
  * SQL: `deploy.sh` 배포 성공, 즉 모든 객체 배치가 컴파일됨.
  * 독립 검증은 이 전제 위에서 위반을 센다.
* **`deploy.sh` 재실행 실패(환경, 코퍼스는 읽기 전용이라 고치지 않음):** 같은 컨테이너에 두 번째로 실행하면 첫 단계 `rm -rf /tmp/corpus-sql`이 `Permission denied`로 실패한다.
  * 원인: `docker cp`가 호스트 uid(501)를 보존해, 컨테이너 사용자 `mssql`이 이전 사본을 지우지 못한다.
  * 이번 측정은 `docker exec -u root pelly-corpus-sql rm -rf /tmp/corpus-sql` 뒤 다시 배포했다.
