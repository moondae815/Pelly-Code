# M3a 벤치마크 회귀 확인 (30건, M2b 기준선 대비)

* **날짜:** 2026-10-18
* **환경:** macOS arm64(Darwin 25.6.0), .NET SDK 10.0.302, Ollama 0.40.0, 모델 `qwen3.6:35b-a3b-coding-nvfp4`, `llm.thinkMode = retry`(기본), `budget.think = 512`(기본), 프롬프트 `cs-v2`·`sql-v1`(M2b와 같음)
* **제품:** 브랜치 `m3a-report-doctor` `ad88c11`(DTO 슬라이스·`--test`·리포트·doctor 포함). 벤치마크 작업은 `--test`를 쓰지 않는다.
* **SQL:** 컨테이너 `pelly-corpus-sql`(localhost,14333). 실행 직전 `eval/corpus/Sql/deploy.sh`로 다시 배포했다(이전 사본은 `docker exec -u root pelly-corpus-sql rm -rf /tmp/corpus-sql`로 지운 뒤 — M2b 4절과 같은 환경 문제).
  * 연결은 `PELLY_SQL_CONNECTION`(비밀번호는 기록하지 않음), 허용 목록은 저장소 밖 `--config` 파일: `{"llm":{"model":"qwen3.6:35b-a3b-coding-nvfp4"},"sql":{"allowedHosts":["localhost"],"allowedDatabases":["PellyCorpus"]}}`
* **명령:** `dotnet run --project eval/PellyCoder.Eval -- --config <pelly.json>` (30건 전체, 1회). 결과 폴더 `eval/results/20261018-182116/`.
* **검증 불변 조건 위반 0건.** 성공률 29/30은 M2b 측정값(28/30)보다 높고, 판정 강화 뒤 재판정값(29/30)과 같다.

## 1. KPI (실행기 `report.md` 그대로)

| 지표 항목 | 목표 | 측정값 |
| :--- | :--- | :--- |
| 검증 불변 조건 | 100% | 100.0% (위반 0건 / 독립 검증 20건, 독립 검증 생략 10건) |
| 초기 응답 지연 (TTFT) | < 1.0초 | 중위값 0.27초 / P90 0.59초 (n=30) |
| 추론 예산 제어 (Thinking) | ≤ 512 토큰 | 최대 512 토큰, 잘림 1건 |
| 패치 포맷 파싱 성공률 | > 99.5% | 100.0% (EDIT 31회) |
| 최종 작업 성공률 | ≥ 85% | 전체 96.7% (29/30), cs 94.4% (17/18), sql 100.0% (12/12), legacy 100.0% (10/10) |
| 단일 작업 완료 시간 | 중위값 < 15초 / P90 < 30초 | 중위값 2.16초 / P90 3.82초 (n=30) |

* **대기(Queue):** 실행기 보고서에는 대기 열이 없다(Task 2는 `summary.json`의 `queueSeconds`와 `pelly report`에만 넣었다). 아래 작업별 표의 대기 열은 각 작업의 `work/<id>/**/.pelly/runs/*/summary.json`에서 옮겼다. 30건 모두 0.03~0.04초 — 단독 사용이라 R3의 예상대로 ≈ 0이다.

## 2. 작업별 결과와 M2b 대비

M2b 열은 `docs/m2b-benchmark.md` 1절의 측정값(괄호는 5절 재판정)이다.

| id | M3a 판정 | 종료 | 시도 | 독립 검증 | 시간(초) | TTFT(초) | 대기(초) | M2b | 차이 |
| :--- | :--- | ---: | ---: | :--- | ---: | ---: | ---: | :--- | :--- |
| cs-legacy-001 | Passed | 0 | 1 | Skipped | 2.48 | 0.94 | 0.04 | Passed | — |
| cs-legacy-002 | Passed | 0 | 1 | Skipped | 1.24 | 0.60 | 0.03 | Passed | — |
| cs-legacy-003 | Passed | 0 | 1 | Skipped | 2.32 | 0.37 | 0.03 | Passed | — |
| cs-legacy-004 | Passed | 0 | 1 | Skipped | 2.13 | 0.26 | 0.03 | Passed | — |
| cs-legacy-005 | Passed | 0 | 1 | Skipped | 1.82 | 0.15 | 0.03 | Passed | — |
| cs-legacy-006 | Passed | 0 | 1 | Skipped | 1.48 | 0.31 | 0.03 | Passed | — |
| cs-legacy-007 | Passed | 0 | 1 | Skipped | 2.21 | 0.52 | 0.03 | Passed | — |
| cs-legacy-008 | Passed | 0 | 1 | Skipped | 3.82 | 0.89 | 0.03 | Passed | — |
| cs-legacy-009 | Passed | 0 | 1 | Skipped | 2.80 | 0.22 | 0.03 | Passed (Passed) | — |
| cs-legacy-010 | Passed | 0 | 1 | Skipped | 0.96 | 0.20 | 0.03 | Failed (Failed) | **개선** |
| cs-modern-001 | **Failed** | 0 | 1 | Passed | 2.84 | 0.01 | 0.04 | Passed | **회귀** |
| cs-modern-002 | Passed | 0 | 1 | Passed | 2.24 | 0.59 | 0.03 | Passed | — |
| cs-modern-003 | Passed | 0 | 1 | Passed | 2.52 | 0.29 | 0.03 | Passed | — |
| cs-modern-004 | Passed | 0 | 1 | Passed | 1.27 | 0.32 | 0.03 | Passed | — |
| cs-modern-005 | Passed | 0 | 1 | Passed | 3.60 | 0.30 | 0.03 | Passed | — |
| cs-modern-006 | Passed | 0 | 1 | Passed | 1.77 | 0.26 | 0.03 | Passed | — |
| cs-modern-007 | Passed | 0 | 1 | Passed | 2.16 | 0.32 | 0.03 | Passed | — |
| cs-modern-008 | Passed | 0 | 1 | Passed | 2.13 | 0.58 | 0.03 | Failed (Passed) | 정의 수정 반영 |
| sql-001 | Passed | 0 | 1 | Passed | 2.09 | 0.05 | 0.03 | Passed (Passed) | — |
| sql-002 | Passed | 0 | 1 | Passed | 5.61 | 0.24 | 0.03 | Passed | — |
| sql-003 | Passed | 0 | 1 | Passed | 1.30 | 0.29 | 0.03 | Passed | — |
| sql-004 | Passed | 0 | 1 | Passed | 4.11 | 0.01 | 0.04 | Passed | — |
| sql-005 | Passed | 0 | 1 | Passed | 0.93 | 0.17 | 0.03 | Passed | — |
| sql-006 | Passed | 0 | 1 | Passed | 0.93 | 0.24 | 0.03 | Passed (Passed) | — |
| sql-007 | Passed | 0 | 1 | Passed | 2.85 | 0.25 | 0.03 | Passed | — |
| sql-008 | Passed | 0 | 1 | Passed | 2.21 | 0.28 | 0.03 | Passed | — |
| sql-009 | Passed | 0 | 1 | Passed | 1.29 | 0.24 | 0.03 | Passed | — |
| sql-010 | Passed | 0 | 1 | Passed | 1.01 | 0.22 | 0.03 | Passed | — |
| sql-011 | Passed | 0 | 2 | Passed | 10.52 | 0.27 | 0.03 | Passed (시도 2) | — (M2b와 같이 1차 `SQL46010` → 2차 통과) |
| sql-012 | Passed | 0 | 1 | Passed | 2.41 | 0.30 | 0.03 | Passed | — |

## 3. 차이 분석

| 작업 | 차이 | 분류 | 근거 |
| :--- | :--- | :--- | :--- |
| cs-modern-001 | Passed → Failed (`mustAdd 'OrderStatus.Shipped'` 늘지 않음) | **모델(DTO 슬라이스가 바꾼 행동) + 판정 정의** | 모델은 요청대로 `if (order.Status == OrderStatus.Shipped) throw new InvalidOperationException(…)`을 넣었다. 그런데 같은 블록에서 환불 조건 `Paid \|\| Shipped`를 `Paid`로 줄였다(가드 뒤라 Shipped 분기는 죽은 코드 — 동작은 같다). 그래서 `OrderStatus.Shipped` 개수가 순증하지 않았다. M3a 프롬프트에는 새 `[READ-ONLY: dto]` 구간(`enum OrderStatus: Pending, Paid, Shipped, Delivered, Cancelled`, `class Order: …`)이 실린다. 같은 요청을 쓰는 M1 E2E `ModernCancelAsync_AddsShippedGuard`를 DTO 도입 직전 커밋(`eaedae0`)과 지금(`ad88c11`)에서 4회씩 돌렸더니 4/4 → 2/4 통과였고, 실패는 모두 같은 모양이었다. 빌드·독립 검증은 통과했다. |
| cs-legacy-010 | Failed → Passed | 모델 | M2b에서 두 번 모두 쓰이지 않는 익명 매개변수만 더했던 작업이 이번에는 WHERE에 상태 9 조건을 넣었다(현재 정의 `mustAdd: ["9"]`). Legacy 프로젝트는 레거시 DTO도 슬라이스에 실린다(R5). 1회 측정이라 원인을 DTO로 확정하지 않는다. |
| cs-modern-008 | Failed → Passed | 정의 | M2b 측정 직후 바꾼 정의(`mustAdd: ["CommissionRate"]`)가 처음부터 적용된 결과다. |

* 성공률이 기준선보다 낮지 않으므로 PRD §8.2 기준의 회귀는 아니다.
* **cs-modern-001 처리(결정):** 판정 정의는 그대로 둔다. 문자열 개수 판정이 올바른 수정(가드 추가 + 죽은 환불 분기 정리)을 거부하는 사례이고, 벤치마크 판정을 테스트로 옮기는 일은 M3b에서 한다. 같은 원인으로 실패하던 M1 E2E `ModernCancelAsync_AddsShippedGuard`는 개수 대신 동작으로 판정하도록 바꿨다 — 수정한 사본에서 수정 전 동작을 고정한 `CancelAsync_WhenShipped_CurrentlyCancels`가 이제 `InvalidOperationException`으로 실패해야 한다(`docs/m3a-e2e.md`).
* 이 측정은 no-change 가드(`NoChange`)와 TESTS FAILED 피드백 문장 추가(`1d40397`) **전** 코드(`ad88c11`)로 했다. 두 변경은 대상이 원본과 같은 시도와 `--test` 실행에만 작용하는데, 이번 30건은 `--test`를 쓰지 않고 모두 대상을 바꿨다(독립 검증·내용 판정 기준).
* 이 측정을 이유로 제품 코드나 작업 정의를 바꾸지 않았다.
