# 반복 벤치마크 요약

* 실행: 20261026-153853 · pelly 0.9.1 · 모델 `qwen3.6:35b` (http://localhost:11434, Ollama 0.40.0) · thinkMode Retry · Unix
* 스위트: builtin
* 선택: --tasks cs-modern --kind * --repeat 3 --test-first
* 조건: 모델 `qwen3.6:35b` · 프롬프트 `cs-v2`·`sql-v1` · thinkMode Retry · budget.think 512 · sampling think (T 0.6, topP 0.95, topK 20, repeat 1, presence 0) / code (T 0.1, topP 0.9, topK 20, repeat 1.05, presence 0)

작업 12건 × 3회

## 전체

* 평균 성공 작업 수: 12.00 (표본 표준편차 0.00; 회차별 12, 12, 12)
* 작업 시간 중위값(전 회차): 6.50초
* 검증 불변 조건 위반: 회차별 0, 0, 0건

## 테스트 우선 지표

| 지표 항목 | 측정값 |
| :--- | :--- |
| 수용 기준 충족률 | 100.0% (36/36) |
| RED 확인 경고율 | 0.0% (0/36) |
| 막은 잘못된 패치 | 12건 |
| 피드백으로 고친 비율 | 12/12 |

판정 테스트가 GREEN 판정과 독립 판정에 함께 쓰입니다. 차이에는 GREEN 피드백과 회귀 확인 효과가 섞여 있습니다.

## 회차별

| 회차 | 폴더 | 작업 | 실행 | 성공 | 불변 조건 위반 | 판정 실패(Judge) | 판정 도구 오류(JudgeTooling) | 시간 중위값(초) |
| ---: | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | run-1 | 12 | 12 | 12 | 0 | 0 | 0 | 6.47 |
| 2 | run-2 | 12 | 12 | 12 | 0 | 0 | 0 | 6.50 |
| 3 | run-3 | 12 | 12 | 12 | 0 | 0 | 0 | 6.38 |

## 범주별 성공 작업 수

| 범주 | 작업 | 평균 | 표준편차 | 최소 | 최대 | 회차별 |
| :--- | ---: | ---: | ---: | ---: | ---: | :--- |
| cs-modern | 12 | 12.00 | 0.00 | 12 | 12 | 12, 12, 12 |

## 불안정·실패 작업

* 불안정(0건): 없음
* 모두 실패(0건): 없음

## 실패 유형 분포 (전 회차 합계)

실패 없음

## 작업별

| id | 범주 | 성공 | 회차 1 | 회차 2 | 회차 3 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| cs-modern-001 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-002 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-003 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-004 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-005 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-006 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-007 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-008 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-009 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-010 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-011 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-012 | cs-modern | 3/3 | Passed | Passed | Passed |
