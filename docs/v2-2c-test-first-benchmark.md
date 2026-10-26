# V2-2c 테스트 우선 검증 예비 측정

> **모든 수치는 「예비(외부 개발 PC, 사례 승인 전)」입니다.** 사람이 판정 테스트와 apply 사례를 승인하기 전의 값이고, 현장 모델·현장 코드에서 잰 것이 아닙니다. 채택·거절의 근거로 쓰지 마십시오. 설계는 `docs/superpowers/specs/2026-10-26-v2-2c-test-first-benchmark-design.md`입니다.

## 1. 실행 조건

| 항목 | 값 |
| :--- | :--- |
| pelly 커밋 | `ee99421` (브랜치 `v2-2c-benchmark`), pelly 0.9.1 |
| 날짜 | 2026-10-26 |
| 모델 | `qwen3.6:35b` (로컬 Ollama 0.40.0), digest `a7eb95c53bcf96b4bdd008d0fab4a5dac88047d9c1a7a9ab88ed453423fbd87c` |
| 프롬프트·샘플링 | `cs-v2`·`sql-v1`, thinkMode Retry, budget.think 512, think(T 0.6, topP 0.95, topK 20) / code(T 0.1, topP 0.9, topK 20, repeat 1.05) |
| 설정 | `{ "llm": { "model": "qwen3.6:35b" } }` 한 줄 |
| 환경 | macOS(Unix), SQL 연결 없음, t8은 Windows 전용 |
| 종료 코드 | 빌드·A·B·compare·C 모두 0 |

명령(그대로 실행한 것):

```bash
pelly-eval run   --tasks cs-modern --repeat 3                 --out A   # v1 기준선
pelly-eval run   --tasks cs-modern --test-first --repeat 3    --out B   # 테스트 우선
pelly-eval compare --test-first A/summary.json B/summary.json           # 보고
pelly-eval apply --set test-first --repeat 1                  --out C   # apply 사례 8건
```

저장소에 남긴 원본 출력과 검토용 diff: `docs/v2-2c-prelim/`(`A-summary.*`, `B-summary.*`, `compare.txt`, `C-apply-summary.md`, `patches/`). 원본 결과 폴더(이 세션 임시, 사라질 수 있음): `/private/tmp/claude-501/-Users-payletter-git-root-Pelly-Code/3abc0ce1-02eb-4de6-bf81-edc41e404ce3/scratchpad/v22c/{A,B,C}`.

**시간 수치 주의.** 측정 중 일부 구간에 같은 기기에서 병렬 수정 브랜치의 전체 `dotnet test`가 함께 돌았습니다. 시간(특히 초 단위 값과 비율)은 부풀었을 수 있고, 시간 목표 판정에 쓰면 안 됩니다. 성공·실패 수는 영향이 없다고 보지만 확인하지 않았습니다.

## 2. cs-modern 12건 × 3회: v1 대 테스트 우선 (예비)

대상 작업은 12건 전부입니다(compare 보고도 「대상 작업: 테스트 우선 12건」). 아래 성공·시간·위반 수치는 compare의 줄(전체 작업 기준이라고 스스로 표시함)을 그대로 옮기지 않고, 각 회차 `results.json` 행(36행씩)에서 다시 센 값입니다. 이번에는 대상이 전체와 같아 두 방식의 성공 수는 일치합니다.

| 지표 | v1(A) | 테스트 우선(B) | 출처 |
| :--- | ---: | ---: | :--- |
| 성공 행 | 25/36 (회차별 8, 8, 9) | 36/36 (회차별 12, 12, 12) | A·B `run-*/results.json` |
| 평균 성공 작업 수 | 8.33 (표준편차 0.58) | 12.00 (0.00) | `summary.md` |
| 차이 | | +3.67 | `compare.txt` |
| 작업 시간 중위값(36행) | 1.76초 | 6.53초 | `results.json`의 `wallSeconds` |
| 시간 비율(중위값 B/A) | | 3.70배 | 위 두 값 |
| 검증 불변 조건 위반 | 0건 | 0건 | `results.json`의 `invariantViolated` |

* compare 보고의 시간 비율은 3.87배(1.68초 대 6.50초)로, `summary.json`의 중위값을 쓴 값이라 위 3.70배와 조금 다릅니다. 같은 자료의 다른 집계이며, 어느 쪽도 부풀림 가능성(§1)을 벗어나지 않습니다.
* 테스트 우선은 매번 판정 테스트를 두 번(RED, GREEN) 돌리고, 피드백 재시도가 있으면 더 걸립니다. 첫 시도에 통과한 24행은 중위값 6.4초(6.1~9.6초), 재시도한 12행은 12.6~26.2초입니다(`wallSeconds`).
* v1(A) 실행도 독립 판정에 같은 판정 테스트를 썼습니다. B에서 새로 더해진 것은 GREEN 피드백 재시도와 회귀 확인이며, 차이(+3.67)에는 둘의 효과가 섞여 있습니다. 판정 테스트가 독립 판정과 같으므로 「판정 테스트에 맞춘 패치」(하드코딩)는 자동으로 걸러지지 않고, §4 사람 검토가 그 빈틈을 메웁니다.

### 테스트 우선 지표(B)

| 지표 | 값 | 출처 |
| :--- | :--- | :--- |
| 수용 기준 충족률 | 100.0% (36/36) | `B/summary.md` |
| RED 확인 경고율 | 0.0% (0/36) | `B/summary.md` |
| 막은 잘못된 패치 | 12건 | `B/summary.md`. `results.json`에서 `testFirst.firstAttemptGreen = Failed`인 행 12개와 일치 |
| 피드백으로 고친 비율 | 12/12 | `B/summary.md` |
| 회귀 | 0건 | `testFirst.regressions` 합계 |
| breaks로 뺀 테스트 | 3건 | `testFirst.breaksExcluded` 합계(cs-modern-001이 매 회차 1건) |

「막은 잘못된 패치」는 첫 시도가 판정 테스트 GREEN에 실패했지만 피드백 재시도 끝에 통과한 행입니다. v1(A)에서는 같은 작업이 실패였습니다.

| 작업 | v1 성공 | 테스트 우선 성공 | 시도 횟수(회차 1, 2, 3) |
| :--- | :--- | :--- | :--- |
| cs-modern-002 | 0/3 | 3/3 | 2, 2, 2 |
| cs-modern-003 | 1/3 | 3/3 | 2, 2, 2 |
| cs-modern-007 | 0/3 | 3/3 | 3, 2, 3 |
| cs-modern-008 | 0/3 | 3/3 | 2, 2, 2 |

나머지 8개 작업(001, 004, 005, 006, 009~012)은 양쪽 모두 3/3이고 테스트 우선에서도 첫 시도에 통과했습니다.

## 3. apply 사례 8건 (`--set test-first --repeat 1`) (예비)

요약 제목에 「예비(사람 승인 전)」가 붙었습니다. `apply-test-first/APPROVED.json`이 아직 없습니다.

| 지표 | 값 | 출처 |
| :--- | :--- | :--- |
| 명세 완료율 | 100.0% (6/6건) | `C/apply-summary.md` |
| 그룹 빌드 통과율 | 100.0% (1/1건) | 같은 곳 |
| 독립 빌드 | 통과 6 · 위반 0 · 검증 불가 0 | 같은 곳 |
| 수용 기준 충족률 | 100.0% (6/6건) | 같은 곳 |
| RED 확인 경고율 | 14.3% (1/7건) | 같은 곳. t4의 `AlreadyPassed`로, 의도한 값입니다 |
| 테스트로 검증되지 않은 수용 기준 | 0건 | 같은 곳 |
| breaks로 뺀 테스트 | 1건 | 같은 곳 |
| 테스트 판정 불일치 · 독립 테스트 검증 불가·건너뜀 | 0건 · 0건 | 같은 곳 |
| 건너뜀 | t6 「SQL 연결 없음」, t8 「Windows 전용」 | 같은 곳 |

사례별 기대 대 실제(`expected.json` 대 `apply-results.json`):

| 사례 | AC | 기대 RED/결과 | 실제 RED/결과 | 독립 테스트 | 작업 수 | 시간(초) |
| :--- | :--- | :--- | :--- | :--- | ---: | ---: |
| t1 | AC1 | Red/Met | Red/Met | 통과 | 2 | 7.4 |
| t2 | AC1 | Red/Met | Red/Met | 통과 | 1 | 3.9 |
| t3 | AC1 | RedCompile/Met | RedCompile/Met | 통과 | 2 | 18.6 |
| t4 | AC1 | AlreadyPassed/Met | AlreadyPassed/Met | 통과 | 1 | 3.9 |
| t5 | AC1 | Red/Met | Red/Met | 통과 | 3 | 10.6 |
| t5 | AC2 | Red/Met | Red/Met | 통과 | | |
| t6 | AC1, AC2 | Red/Met, NotChecked/Unverified | 측정 못 함(SQL 연결 없음) | | 3 | |
| t7 | AC1 | Red/Met | Red/Met | 통과 | 1 | 6.5 |
| t8 | AC1 | Red/Met | 측정 못 함(Windows 전용) | | 1 | |

실행한 6건은 모두 기대와 같았습니다. 시간은 §1의 부풀림 가능성을 받습니다.

## 4. 사람 표본 검토 목록

테스트 우선 행 중 `firstAttemptGreen`이 Failed였던 12행은 모델이 판정 테스트의 실패 메시지를 보고 고친 패치입니다. 정상적인 수정일 수도 있고, 테스트가 기대하는 값을 그대로 박은 **하드코딩**일 수도 있습니다. 판정 테스트가 독립 판정에도 쓰이므로 자동으로는 구분되지 않습니다. **사람이 아래 패치의 diff를 열어 하드코딩 여부를 확인해야 합니다.** 나머지 24행(첫 시도 통과)은 표본으로 몇 건만 보아도 됩니다.

| 작업 | 회차 | 상태 | 시도 | 패치 위치 |
| :--- | ---: | :--- | ---: | :--- |
| cs-modern-002 | 1 | Passed | 2 | `docs/v2-2c-prelim/patches/cs-modern-002-run1.diff` (v1: `cs-modern-002-run1-v1.diff`) |
| cs-modern-003 | 1 | Passed | 2 | `docs/v2-2c-prelim/patches/cs-modern-003-run1.diff` (v1: `cs-modern-003-run1-v1.diff`) |
| cs-modern-007 | 1 | Passed | 3 | `docs/v2-2c-prelim/patches/cs-modern-007-run1.diff` (v1: `cs-modern-007-run1-v1.diff`) |
| cs-modern-008 | 1 | Passed | 2 | `docs/v2-2c-prelim/patches/cs-modern-008-run1.diff` (v1: `cs-modern-008-run1-v1.diff`) |
| cs-modern-002 | 2 | Passed | 2 | `docs/v2-2c-prelim/patches/cs-modern-002-run2.diff` (v1: `cs-modern-002-run2-v1.diff`) |
| cs-modern-003 | 2 | Passed | 2 | `docs/v2-2c-prelim/patches/cs-modern-003-run2.diff` (v1: `cs-modern-003-run2-v1.diff`) |
| cs-modern-007 | 2 | Passed | 2 | `docs/v2-2c-prelim/patches/cs-modern-007-run2.diff` (v1: `cs-modern-007-run2-v1.diff`) |
| cs-modern-008 | 2 | Passed | 2 | `docs/v2-2c-prelim/patches/cs-modern-008-run2.diff` (v1: `cs-modern-008-run2-v1.diff`) |
| cs-modern-002 | 3 | Passed | 2 | `docs/v2-2c-prelim/patches/cs-modern-002-run3.diff` (v1: `cs-modern-002-run3-v1.diff`) |
| cs-modern-003 | 3 | Passed | 2 | `docs/v2-2c-prelim/patches/cs-modern-003-run3.diff` (v1: `cs-modern-003-run3-v1.diff`) |
| cs-modern-007 | 3 | Passed | 3 | `docs/v2-2c-prelim/patches/cs-modern-007-run3.diff` (v1: `cs-modern-007-run3-v1.diff`) |
| cs-modern-008 | 3 | Passed | 2 | `docs/v2-2c-prelim/patches/cs-modern-008-run3.diff` (v1: `cs-modern-008-run3-v1.diff`) |

* diff는 코퍼스 프로젝트의 `.cs` 파일만 원본 `eval/corpus/Modern/`과 비교한 것입니다(`JudgeTests/`·`bin/`·`obj/`·`.pelly/` 제외). 같은 작업·회차의 v1(A) 결과 diff가 `-v1.diff`입니다. 작업 복사본 전체는 세션 임시 폴더 `B/run-N/work/<id>/Modern/`에만 있습니다.
* 시도별 기록은 세션 임시 폴더의 각 `Modern/.pelly/runs/` 아래에만 있습니다(저장소에는 없음).
* apply 사례(C)의 작업 복사본은 `C/work/<id>-1/corpus/`(세션 임시)입니다. 6건 모두 독립 테스트를 통과했으나 같은 방식으로 훑을 수 있습니다.
* 검토 결과는 이 표에 사람이 적습니다. 아직 아무도 보지 않았습니다.

## 5. 한계와 남은 일

* **t8은 Windows에서 한 번 돌려야 합니다.** 레거시 `{trx}` 명령 사례라 macOS에서는 측정하지 못했고, 승인 전에 Windows 실행 1회가 필요합니다.
* t6도 SQL 연결이 있는 환경에서 한 번 돌려야 `Unverified` 기대가 실제로 나오는지 압니다.
* **`APPROVED.json`은 사람이 만듭니다.** 승인 전까지 이 문서의 모든 수치는 예비입니다.
* 1회 측정(저장소 `ee99421`, 모델 한 개)이고 apply는 `--repeat 1`입니다. 표준편차는 cs-modern 3회에서만 있습니다.
* 시간 비율은 §1의 병렬 `dotnet test` 때문에 부풀었을 수 있습니다. 이번 값으로 시간 상한을 정하지 않습니다.
* 수용 기준 충족률 100%는 PRD §12.6 목표(≥ 80%)를 넘지만, 판정 테스트가 독립 판정과 같다는 한계(§2)와 §4의 사람 검토가 끝나기 전에는 목표 달성으로 세지 않습니다.

* 이 문서를 만든 커밋에서는 문서만 바뀌었으므로 `dotnet test` 게이트를 돌리지 않았습니다(`dotnet build -warnaserror`만 확인). 측정 자체는 `ee99421`의 빌드로 한 것입니다.
