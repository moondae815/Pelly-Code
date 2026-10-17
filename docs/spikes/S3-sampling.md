# S3. think/code 샘플링 기본값

## 질문
THINK 단계와 CODE 단계에 쓸 샘플링 기본값은 무엇인가? (PRD Q2, 설계 §8.1 `sampling`)

## 환경
| 항목 | 값 |
| :--- | :--- |
| 장비 | Apple M5 Pro, 48GB |
| Ollama | 0.40.0 |
| 모델 | `qwen3.6:35b-a3b-coding-nvfp4` (폐쇄망 모델과 다름) |
| 날짜 | 2026-10-17 |

## 방법
1. 모델 카드 확인: <https://huggingface.co/Qwen/Qwen3.6-35B-A3B>
2. `spikes/S3-sampling/sampling.cs` — 작업 10건(벤치마크 C# 5건 + 같은 코퍼스의 추가 요청 5건 `x-001~005`). 컨텍스트는 대상 멤버 원문만(`[EDITABLE: target]`), 시스템 프롬프트는 영어.
   * THINK: 3개 설정 × 10건, `num_predict: 512`, stop `</think>`. 지표: 512 도달(잘림), 반복(같은 줄 3회 이상).
   * CODE: A 설정의 think를 프리필하고 2개 설정 × 10건, `num_predict: 2048`. 지표: 블록 파싱, SEARCH 정확 일치.
3. 보조 측정 `nothink.cs` — think 512 후 CODE vs **빈 think**(`<think>\n\n</think>\n\n`) CODE, 10건씩. 지표: 파싱, 정확 일치, 요청 반영(작업별 키워드가 REPLACE에 있는지), 소요 시간.

## 측정 결과
**모델 카드 권장값 (인용)**
| 모드 | 값 |
| :--- | :--- |
| Thinking, 일반 | temperature=1.0, top_p=0.95, top_k=20, min_p=0.0, presence_penalty=1.5, repetition_penalty=1.0 |
| Thinking, 정밀 코딩 | temperature=0.6, top_p=0.95, top_k=20, min_p=0.0, presence_penalty=0.0, repetition_penalty=1.0 |
| Instruct (non-thinking) | temperature=0.7, top_p=0.80, top_k=20, min_p=0.0, presence_penalty=1.5, repetition_penalty=1.0 |

권장 출력 길이 32,768토큰, 컨텍스트 128K 이상 유지를 권장한다. Greedy 디코딩·think 예산에 대한 언급은 없다. 라이선스는 Apache-2.0. Ollama Modelfile 기본값은 "정밀 코딩" 값과 같다.

**THINK 단계** (원시: `think.csv`)
| 설정 | 값 | 512 도달 | 반복 감지 |
| :--- | :--- | ---: | ---: |
| A | 모델 카드 정밀 코딩 (0.6 / 0.95 / 20 / presence 0) | 10/10 | 3/10 |
| B | 모델 카드 일반 (1.0 / 0.95 / 20 / presence 1.5) | 10/10 | 3/10 |
| C | 낮은 온도 (0.1 / 0.9 / 20 / presence 0) | 10/10 | 3/10 |

→ **모든 설정에서 생각이 512토큰 안에 끝나지 않았다.** 반복 감지율 차이도 없다(반복 감지는 코드 줄을 여러 번 인용하는 정상 사고도 잡으므로 참고값).

**CODE 단계** (원시: `code.csv`, 출력 `out-*.txt`)
| 설정 | 값 | 파싱 성공 | SEARCH 정확 일치 |
| :--- | :--- | ---: | ---: |
| X | 0.1 / 0.9 / repeat 1.05 | 10/10 | 10/10 |
| Y | 0.2 / 0.95 / repeat 1.0 | 10/10 | 10/10 |

**think 512 vs 빈 think** (원시: `nothink.csv`, 출력 `cmp-*.txt`, CODE는 X 설정)
| 방식 | 파싱 | 정확 일치 | 요청 반영 | 평균 소요 | 범위 |
| :--- | ---: | ---: | ---: | ---: | :--- |
| think 512 → CODE | 10/10 | 10/10 | 10/10 | 9.0초 | 7.5 ~ 12.5초 |
| 빈 think → CODE | 10/10 | 10/10 | 10/10 | **1.7초** | 0.6 ~ 4.1초 |

think 512토큰 생성에만 매번 약 6.5초(약 79 tok/s)가 든다.

## 결론
* THINK 샘플링: 설정 간 차이가 없으므로 **모델 카드 "정밀 코딩" 값(= Ollama 기본값)**을 쓴다. 설계 초안 값과 같다.
* CODE 샘플링: 차이가 없으므로 설계 초안 값(temperature 0.1, top_p 0.9, repeat_penalty 1.05)을 유지한다.
* **설계 재검토가 필요한 발견:** 이 모델은 512토큰 안에 생각을 끝내지 못한다. 그런데 이번 10건에서는 생각을 생략해도 결과가 같았고, 시간은 약 5배 빨랐다. 시도마다 think 512를 하면 교정 3회까지 갈 경우 생각에만 약 26초가 들어 "단일 작업 15초" 목표와 맞지 않는다.
  * **권장안 (사용자 결정 필요):** 첫 시도는 빈 think로 바로 코드를 만들고, 교정 시도(빌드 실패·매칭 실패 뒤)부터 think 512를 켠다. 설정 `llm.thinkMode = off | retry | always`(기본 `retry`)로 두면 폐쇄망에서 재반입 없이 바꿀 수 있다.
  * 한계: 작업이 쉬운 편이고, "요청 반영"은 키워드 검사이지 빌드·테스트 판정이 아니다. M1 이후 벤치마크(빌드 판정 포함)로 다시 비교한다.

## 설계 반영
| 위치 | 변경 |
| :--- | :--- |
| §8.1 `sampling.think` | 그대로 (0.6 / 0.95 / 20 / repeat 1.0 / presence 0) — 출처를 모델 카드 "정밀 코딩"으로 명시 |
| §8.1 `sampling.code` | 그대로 (0.1 / 0.9 / 20 / repeat 1.05) |
| §8.1·§8.2 (사용자 결정 후) | `llm.thinkMode` 추가 검토. `retry`면 시도 1은 빈 think 프리필, 시도 2부터 THINK 단계 실행 |
