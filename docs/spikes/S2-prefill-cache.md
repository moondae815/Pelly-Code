# S2. raw 프리필과 접두사 캐시 재사용

## 질문
2단계 생성(THINK → `</think>` 뒤 `<<<<<<< SEARCH` 프리필로 CODE 재요청)에서 Ollama가 앞부분 프롬프트의 KV 캐시를 재사용해 2단계 TTFT가 1초 안에 나오는가? 512토큰 도달 시 스트림을 어떻게 끊어야 하는가? (PRD Q3, 설계 §7.3·§8.2)

## 환경
| 항목 | 값 |
| :--- | :--- |
| 장비 | Apple M5 Pro, 48GB |
| Ollama | 0.40.0 |
| 모델 | `qwen3.6:35b-a3b-coding-nvfp4` (폐쇄망 모델과 다름 — 같은 모델로 재실행 필요) |
| 날짜 | 2026-10-17 |
| 샘플링 | temperature 0.6 / top_p 0.95 / top_k 20, `num_ctx` 8192, `keep_alive` 30m |

## 방법
`spikes/S2-prefill-cache/prefill.cs` (원시 결과 `results.csv`, 출력 예 `sample-*.txt`)
* 기본 프롬프트: 시스템 프롬프트(맨 앞에 실행마다 다른 nonce) + 코퍼스 `Shop.Api/Services/OrderService.cs` 전체 2회 + 요청문 → **약 4,300토큰** (계획의 3,500보다 커서 보수적인 조건).
* 1단계: `basePrompt + "<think>\n"`, stop `["</think>"]`.
  * **A안:** 서버 쪽 `num_predict: 512`로 멈춤.
  * **B안:** `num_predict: 4096`으로 보내고 클라이언트가 512번째 청크에서 스트림을 끊음.
* 1단계가 잘렸으면(`done_reason != stop`) 마지막 줄바꿈 이전까지만 남김.
* 2단계: `basePrompt + "<think>\n" + think + "\n</think>\n\n<<<<<<< SEARCH\n"`, `num_predict: 256`, stop `["<|im_end|>"]`.
* 워밍업 1회 후 안별 5회. TTFT = 요청 전송 → 첫 `response` 청크.

## 측정 결과
`prompt_eval_count`는 **캐시분을 포함한 전체 입력 토큰 수**이고, `prompt_eval_cached_count`가 재사용된 토큰 수다. 새로 처리한 토큰 = 두 값의 차이.

| 안 | 단계 | TTFT 중앙값 | 입력 토큰 | 캐시 재사용 | 새로 처리 | 비고 |
| :--- | :--- | ---: | ---: | ---: | ---: | :--- |
| A | 1단계 THINK | 1,559ms | 4,301 | 5 | 4,296 | 캐시 없음(nonce) — 프롬프트 처리 약 2,800 tok/s |
| A | **2단계 CODE** | **299ms** | 4,811 | 4,297 | **514** | 생성한 think 토큰은 재사용되지 않고 다시 처리됨 |
| B | 2단계 CODE | **2,318ms** | 4,820 | 4,301 | 519 | 프롬프트 처리는 약 270ms인데, 끊긴 1단계 요청을 서버가 정리하는 동안 약 2초 대기 |

원시 TTFT(ms) — A 2단계: 296, 560, 304, 299, 294 / B 2단계: 2296, 2309, 2322, 2458, 2318

* **기본 프롬프트 접두사는 재사용된다**(4,297/4,301). 1단계에서 생성한 think 토큰의 KV는 재사용되지 않지만, 512토큰 처리는 약 0.26초라 영향이 작다.
* **클라이언트 취소(B안)는 다음 요청을 약 2초 늦춘다.** 서버가 끊긴 요청의 생성을 바로 멈추지 않기 때문으로 보인다.
* 1단계 TTFT는 캐시가 없는 4,300토큰 기준 1.56초로, 이 Mac에서는 TTFT < 1초 KPI를 넘는다(폐쇄망 GPU 서버에서 다시 측정해야 함).
* 출력 품질: SEARCH가 원본 줄을 그대로 복사했고, 요청대로 `Shipped` 검사를 추가했다(`sample-*.txt`). A안 1회차는 `num_predict: 256`에서 잘렸다(설계값 2,048이면 문제 없음).

## 결론
* **통과 (A안).** 2단계에서 새로 처리한 토큰 514개 ≤ think 512 + 50, 2단계 TTFT 중앙값 299ms < 1초.
* **THINK 예산은 서버 `num_predict`로 제한한다.** 클라이언트가 스트림을 끊으면 안 된다. 이렇게 하면 think 토큰 수를 서버의 `eval_count`로 정확히 알 수 있어, think 예산을 세려고 로컬 토크나이저를 쓸 필요가 없다.
* 1단계 TTFT(첫 응답)는 입력 크기와 장비에 좌우된다. 시스템 프롬프트·few-shot을 프롬프트 맨 앞에 고정해 두면 실행 사이에도 그 부분이 캐시된다(이번 측정은 nonce로 일부러 막음).

## 설계 반영
| 위치 | 변경 |
| :--- | :--- |
| §8.2 QwenStreamClient [THINK] | "누적 텍스트를 tokenCounter로 측정해 ≥ budget.think이면 취소" → **`num_predict = budget.think`로 요청하고 `done_reason == "length"`이면 잘린 것으로 처리**. 클라이언트 취소는 사용자 Ctrl+C 때만 사용 |
| §8.2 TokenCounter | 용도를 입력 예산 계산으로 한정(think 예산은 서버 `eval_count` 사용) |
| §8.2 OllamaBackend / `LlmChunk` | `prompt_eval_cached_count` 필드 추가. 로그에 "새로 처리한 입력 토큰 = prompt_eval_count − cached" 기록 |
| §8.2 Prompts | 시스템 프롬프트·few-shot을 항상 프롬프트 맨 앞에 두고, 실행마다 바뀌는 내용(대상 코드·요청)은 그 뒤에 둔다(실행 간 캐시 재사용) |
| PRD §2.2 TTFT | 이 Mac에서 4,300토큰 무캐시 1단계 TTFT 1.56초. KPI 판정은 폐쇄망 서버에서(Q9 사양 확인 후 목표 재검토) |
