# M3a 벤치마크 회귀 확인 (30건, M2b 기준선 대비)

* **날짜:** 2026-10-18
* **환경:** macOS arm64(Darwin 25.6.0), .NET SDK 10.0.302, Ollama 0.40.0, 모델 `qwen3.6:35b-a3b-coding-nvfp4`, `llm.thinkMode = retry`(기본), `budget.think = 512`(기본), 프롬프트 `cs-v2`·`sql-v1`(M2b와 같음)
* **제품:** 브랜치 `m3a-report-doctor` `5fd5164` — 최종 코드. DTO 슬라이스·`--test`·리포트·doctor, no-change 가드(`NoChange`), 테스트 피드백 문장이 들어 있다. 벤치마크 작업은 `--test`를 쓰지 않는다.
* **SQL:** 컨테이너 `pelly-corpus-sql`(localhost,14333). 실행 직전 `eval/corpus/Sql/deploy.sh`로 다시 배포했다.
  * 이전 사본은 `docker exec -u root pelly-corpus-sql rm -rf /tmp/corpus-sql`로 먼저 지웠다(M2b 4절과 같은 환경 문제).
  * 연결은 `PELLY_SQL_CONNECTION`(비밀번호는 기록하지 않음).
  * 허용 목록은 저장소 밖 `--config` 파일: `{"llm":{"model":"qwen3.6:35b-a3b-coding-nvfp4"},"sql":{"allowedHosts":["localhost"],"allowedDatabases":["PellyCorpus"]}}`
* **명령:** `dotnet run --project eval/PellyCoder.Eval -- --config <pelly.json>` (30건 전체, 1회). 결과 폴더 `eval/results/20261018-185341/`.
* **결과:** 검증 불변 조건 위반 0건. 성공률 30/30으로 M2b 측정값(28/30)과 판정 강화 뒤 재판정값(29/30)보다 높다.

## 1. KPI (실행기 `report.md` 그대로)

| 지표 항목 | 목표 | 측정값 |
| :--- | :--- | :--- |
| 검증 불변 조건 | 100% | 100.0% (위반 0건 / 독립 검증 20건, 독립 검증 생략 10건) |
| 초기 응답 지연 (TTFT) | < 1.0초 | 중위값 0.29초 / P90 0.52초 (n=30) |
| 추론 예산 제어 (Thinking) | ≤ 512 토큰 | 최대 512 토큰, 잘림 1건 |
| 패치 포맷 파싱 성공률 | > 99.5% | 100.0% (EDIT 31회) |
| 최종 작업 성공률 | ≥ 85% | 전체 100.0% (30/30), cs 100.0% (18/18), sql 100.0% (12/12), legacy 100.0% (10/10) |
| 단일 작업 완료 시간 | 중위값 < 15초 / P90 < 30초 | 중위값 1.99초 / P90 3.83초 (n=30) |

* 실패 없음. Legacy 10건은 빌드 판정 생략이다(설계 §10.6 — Windows가 아니면 패치 적용·모양 검사까지만 판정).
* **대기(Queue):** 실행기 보고서에는 대기 열이 없다. Task 2는 대기를 `summary.json`의 `queueSeconds`와 `pelly report`에만 넣었다.
  * 아래 작업별 표의 대기 열은 각 작업의 `work/<id>/**/.pelly/runs/*/summary.json`에서 옮겼다.
  * 30건 모두 0.02~0.03초다. 단독 사용이라 R3의 예상대로 ≈ 0이다.

## 2. 작업별 결과와 M2b 대비

* "M2b" 열은 `docs/m2b-benchmark.md` 1절의 측정값이고, 괄호는 그 문서 5절의 재판정이다.
* "M3a 첫 측정" 열은 3절의 앞선 실행이다.

| id | 판정 | 종료 | 시도 | 독립 검증 | 시간(초) | TTFT(초) | 대기(초) | M2b | M3a 첫 측정 | M2b 대비 |
| :--- | :--- | ---: | ---: | :--- | ---: | ---: | ---: | :--- | :--- | :--- |
| cs-legacy-001 | Passed | 0 | 1 | Skipped | 1.99 | 0.45 | 0.03 | Passed | Passed | — |
| cs-legacy-002 | Passed | 0 | 1 | Skipped | 1.25 | 0.60 | 0.03 | Passed | Passed | — |
| cs-legacy-003 | Passed | 0 | 1 | Skipped | 1.93 | 0.38 | 0.03 | Passed | Passed | — |
| cs-legacy-004 | Passed | 0 | 1 | Skipped | 1.66 | 0.27 | 0.03 | Passed | Passed | — |
| cs-legacy-005 | Passed | 0 | 1 | Skipped | 1.81 | 0.15 | 0.03 | Passed | Passed | — |
| cs-legacy-006 | Passed | 0 | 1 | Skipped | 1.48 | 0.31 | 0.03 | Passed | Passed | — |
| cs-legacy-007 | Passed | 0 | 1 | Skipped | 2.21 | 0.52 | 0.03 | Passed | Passed | — |
| cs-legacy-008 | Passed | 0 | 1 | Skipped | 3.83 | 0.89 | 0.03 | Passed | Passed | — |
| cs-legacy-009 | Passed | 0 | 1 | Skipped | 2.80 | 0.22 | 0.03 | Passed (Passed) | Passed | — |
| cs-legacy-010 | Passed | 0 | 1 | Skipped | 0.96 | 0.20 | 0.03 | Failed (Failed) | Passed | **개선** |
| cs-modern-001 | Passed | 0 | 1 | Passed | 2.97 | 0.01 | 0.03 | Passed | Failed | — |
| cs-modern-002 | Passed | 0 | 1 | Passed | 1.71 | 0.03 | 0.03 | Passed | Passed | — |
| cs-modern-003 | Passed | 0 | 1 | Passed | 2.50 | 0.30 | 0.03 | Passed | Passed | — |
| cs-modern-004 | Passed | 0 | 1 | Passed | 1.25 | 0.32 | 0.03 | Passed | Passed | — |
| cs-modern-005 | Passed | 0 | 1 | Passed | 3.63 | 0.31 | 0.03 | Passed | Passed | — |
| cs-modern-006 | Passed | 0 | 1 | Passed | 1.77 | 0.26 | 0.03 | Passed | Passed | — |
| cs-modern-007 | Passed | 0 | 1 | Passed | 2.25 | 0.32 | 0.03 | Passed | Passed | — |
| cs-modern-008 | Passed | 0 | 1 | Passed | 2.13 | 0.60 | 0.03 | Failed (Passed) | Passed | 정의 수정 반영 |
| sql-001 | Passed | 0 | 1 | Passed | 2.45 | 0.41 | 0.03 | Passed (Passed) | Passed | — |
| sql-002 | Passed | 0 | 1 | Passed | 5.77 | 0.37 | 0.03 | Passed | Passed | — |
| sql-003 | Passed | 0 | 1 | Passed | 1.31 | 0.29 | 0.03 | Passed | Passed | — |
| sql-004 | Passed | 0 | 1 | Passed | 4.28 | 0.22 | 0.03 | Passed | Passed | — |
| sql-005 | Passed | 0 | 1 | Passed | 0.94 | 0.17 | 0.03 | Passed | Passed | — |
| sql-006 | Passed | 0 | 1 | Passed | 0.94 | 0.24 | 0.02 | Passed (Passed) | Passed | — |
| sql-007 | Passed | 0 | 1 | Passed | 2.87 | 0.25 | 0.03 | Passed | Passed | — |
| sql-008 | Passed | 0 | 1 | Passed | 2.22 | 0.29 | 0.03 | Passed | Passed | — |
| sql-009 | Passed | 0 | 1 | Passed | 1.82 | 0.24 | 0.02 | Passed | Passed | — |
| sql-010 | Passed | 0 | 1 | Passed | 1.01 | 0.22 | 0.02 | Passed | Passed | — |
| sql-011 | Passed | 0 | 2 | Passed | 10.67 | 0.27 | 0.02 | Passed (시도 2) | Passed | — (M2b와 같이 1차 `SQL46010` → 2차 통과) |
| sql-012 | Passed | 0 | 1 | Passed | 2.40 | 0.30 | 0.02 | Passed | Passed | — |

## 3. 차이 분석

| 작업 | 차이 | 분류 | 근거 |
| :--- | :--- | :--- | :--- |
| cs-legacy-010 | M2b Failed → Passed (두 M3a 실행 모두) | 모델 | M2b에서 두 번 모두 쓰이지 않는 익명 매개변수만 더했던 작업이다. 이번에는 WHERE에 상태 9 조건을 넣었다(현재 정의 `mustAdd: ["9"]`). Legacy 프로젝트는 레거시 DTO도 슬라이스에 실린다(R5). 원인을 DTO로 확정하지는 않았다. |
| cs-modern-008 | M2b Failed → Passed | 정의 | M2b 측정 직후 바꾼 정의(`mustAdd: ["CommissionRate"]`)가 처음부터 적용된 결과다. |
| cs-modern-001 | M2b Passed, M3a 첫 측정 Failed, 이번 Passed | 모델 변동 + 판정 정의 | 아래 참고 |

**cs-modern-001.**

* 첫 측정(`ad88c11`)에서 모델은 요청대로 `if (order.Status == OrderStatus.Shipped) throw new InvalidOperationException(…)`을 넣었다. 동시에 환불 조건 `Paid || Shipped`를 `Paid`로 줄였다. 가드 뒤라 Shipped 분기는 죽은 코드이고 동작은 같다. 그래서 `OrderStatus.Shipped` 개수가 늘지 않아 `mustAdd` 판정이 실패했다(1 → 1). 빌드와 독립 검증은 통과했다.
* 이번 실행에서는 가드만 넣고 환불 조건을 그대로 두어 통과했다.
* M3a 프롬프트에는 새 `[READ-ONLY: dto]` 구간(`enum OrderStatus: Pending, Paid, Shipped, Delivered, Cancelled`, `class Order: …`)이 실린다.
* 같은 요청을 쓰는 M1 E2E를 개수 판정으로 4회씩 돌린 결과, DTO 도입 직전(`eaedae0`) 4/4, `ad88c11` 2/4가 통과했다. 실패는 모두 같은 모양이었다.
* **처리(결정):**
  * 판정 정의는 그대로 둔다. 문자열 개수 판정이 올바른 수정을 거부하는 사례이고, 벤치마크 판정을 테스트로 옮기는 일은 M3b에서 한다.
  * 같은 원인으로 실패하던 E2E는 동작 판정으로 바꿨다(`docs/m3a-e2e.md` 1·4절).

## 4. 앞선 실행 (참고)

* `eval/results/20261018-182116/`, 코드 `ad88c11`(no-change 가드 전), 같은 설정.
* 29/30(cs 17/18), 위반 0건. TTFT 중위 0.27 / P90 0.59초, 완료 시간 중위 2.16 / P90 3.82초, 파싱 100%(EDIT 31회), think 잘림 1건(sql-011), 대기 0.03~0.04초.
* 실패는 cs-modern-001 1건(3절)이다. 나머지 29건의 판정은 이번 실행과 같다.
* 이 실행을 이유로 제품 코드나 작업 정의를 바꾸지 않았다.
