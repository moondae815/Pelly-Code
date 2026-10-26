# 반복 벤치마크 요약

* 실행: 20261026-153544 · pelly 0.9.1 · 모델 `qwen3.6:35b` (http://localhost:11434, Ollama 0.40.0) · thinkMode Retry · Unix
* 스위트: builtin
* 선택: --tasks cs-modern --kind * --repeat 3
* 조건: 모델 `qwen3.6:35b` · 프롬프트 `cs-v2`·`sql-v1` · thinkMode Retry · budget.think 512 · sampling think (T 0.6, topP 0.95, topK 20, repeat 1, presence 0) / code (T 0.1, topP 0.9, topK 20, repeat 1.05, presence 0)

작업 12건 × 3회

## 전체

* 평균 성공 작업 수: 8.33 (표본 표준편차 0.58; 회차별 8, 8, 9)
* 작업 시간 중위값(전 회차): 1.68초
* 검증 불변 조건 위반: 회차별 0, 0, 0건

## 회차별

| 회차 | 폴더 | 작업 | 실행 | 성공 | 불변 조건 위반 | 판정 실패(Judge) | 판정 도구 오류(JudgeTooling) | 시간 중위값(초) |
| ---: | :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | run-1 | 12 | 12 | 8 | 0 | 4 | 0 | 1.87 |
| 2 | run-2 | 12 | 12 | 8 | 0 | 4 | 0 | 1.42 |
| 3 | run-3 | 12 | 12 | 9 | 0 | 3 | 0 | 1.66 |

## 범주별 성공 작업 수

| 범주 | 작업 | 평균 | 표준편차 | 최소 | 최대 | 회차별 |
| :--- | ---: | ---: | ---: | ---: | ---: | :--- |
| cs-modern | 12 | 8.33 | 0.58 | 8 | 9 | 8, 8, 9 |

## 불안정·실패 작업

* 불안정(1건): cs-modern-003 1/3
* 모두 실패(3건): cs-modern-002, cs-modern-007, cs-modern-008

## 실패 유형 분포 (전 회차 합계)

| 실패 유형 | 건수 | 작업 |
| :--- | ---: | :--- |
| Failed(Judge) | 11 | cs-modern-002, cs-modern-003, cs-modern-007, cs-modern-008 |

## 작업별

| id | 범주 | 성공 | 회차 1 | 회차 2 | 회차 3 |
| :--- | :--- | :--- | :--- | :--- | :--- |
| cs-modern-001 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-002 | cs-modern | 0/3 | Failed(Judge) | Failed(Judge) | Failed(Judge) |
| cs-modern-003 | cs-modern | 1/3 | Failed(Judge) | Failed(Judge) | Passed |
| cs-modern-004 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-005 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-006 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-007 | cs-modern | 0/3 | Failed(Judge) | Failed(Judge) | Failed(Judge) |
| cs-modern-008 | cs-modern | 0/3 | Failed(Judge) | Failed(Judge) | Failed(Judge) |
| cs-modern-009 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-010 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-011 | cs-modern | 3/3 | Passed | Passed | Passed |
| cs-modern-012 | cs-modern | 3/3 | Passed | Passed | Passed |
