# S1. chat template·think 태그

## 질문
`Qwen3.6-35B-A3B`를 Ollama `raw: true`로 호출할 때 쓸 chat template과 think 태그 문자열은 무엇인가? (PRD Q1, 설계 D10·§8.2)

## 환경
| 항목 | 값 |
| :--- | :--- |
| 장비 | Apple M5 Pro, 48GB |
| Ollama | 0.40.0 |
| 모델 | `qwen3.6:35b-a3b-coding-nvfp4` (format `safetensors`, family `qwen3_5_moe`, 35.1B, `nvfp4`) — **폐쇄망 모델과 다름. 같은 모델을 받으면 재실행** |
| 날짜 | 2026-10-17 |

## 방법
1. `ollama show <model> --template` / `--modelfile`, `POST /api/show`
2. `spikes/S1-template/probe.cs`: 같은 system·user 메시지를 ① `/api/chat`(내장 렌더러, `think: true`) ② `/api/generate` raw ChatML + `<think>\n` 프리필 ③ raw + 빈 think(`<think>\n\n</think>\n\n`)로 보내 `prompt_eval_count`, 응답 필드, 출력 비교. 캐시 영향을 없애려고 system 메시지에 실행마다 다른 nonce를 넣음.
3. (probe.cs ④) raw 모드에서 생각이 끝날 때 `</think>`가 응답 텍스트에 나오는지 `num_predict: 3000`으로 확인.
4. (probe.cs ⑤, `stop-check.sh`) raw 모드에서 `stop` 옵션이 지켜지는지 확인 — `</think>`, `\n</think>`, `</think>\n`, 일반 문자열 `IsNullOrEmpty` 각각.

## 측정 결과
**Modelfile**
```text
TEMPLATE {{ .Prompt }}
RENDERER qwen3.5
PARSER qwen3.5
CAPABILITY completion / vision / thinking  (show API: tools 포함)
PARAMETER temperature 0.6, top_k 20, top_p 0.95, min_p 0, presence_penalty 0, repeat_penalty 1
```
→ Go 템플릿이 아니라 **Ollama 내장 렌더러(`qwen3.5`)**가 프롬프트를 만든다. `/api/show`의 `template`은 `{{ .Prompt }}`뿐이라 템플릿 비교로는 검증할 수 없다(설계 §8.13 doctor의 "템플릿 토큰 비교" 방식은 이 모델에서 쓸 수 없음).

**프롬프트 비교** (원시 출력: `spikes/S1-template/probe-output.txt`)

| 요청 | prompt_eval_count | 생각 텍스트 위치 | 비고 |
| :--- | ---: | :--- | :--- |
| `/api/chat`, think=true | 50 | `message.thinking` 필드 | 렌더러 사용 |
| `/api/generate` raw, ChatML + `<think>\n` | **50** | `response` 필드에 그대로 | 렌더러 결과와 토큰 수 동일 |
| raw, 빈 think 프리필 | 52 | — | `\n</think>\n\n` 추가분 = 2토큰 → `</think>`는 단일 특수 토큰 |

* raw 모드에서 생각이 끝나면 `response`에 `</think>\n\n<답>`이 그대로 나온다(probe ④: index 1090, eval 295). 종료 토큰 `<|im_end|>`는 응답에 포함되지 않고 `done_reason: stop`으로 끝난다.
* **raw 모드에서 `stop` 옵션이 지켜지지 않는다** (`stop-check-output.txt`, probe ⑤). 4가지 stop 문자열 모두 응답에 그대로 나오고 생성은 답을 끝까지 쓴 뒤 종료 토큰에서 멈췄다(`done_reason: stop`은 종료 토큰 때문). 이 모델은 `safetensors`/`nvfp4` 엔진으로 서빙되며, GGUF 엔진에서는 다를 수 있다(폐쇄망 모델로 재확인). 반면 `num_predict` 제한은 S2·S3에서 정확히 지켜졌다.
* 응답 마지막 줄에 **`prompt_eval_cached_count`** 필드가 있다(S2에서 캐시 재사용 측정에 사용).
* 첫 요청 TTFT 25.7초(모델 로드), 이후 44~162ms — ②③은 ①과 같은 nonce를 써서 프롬프트 대부분이 캐시된 상태의 값이다.
* **생각 길이:** 덧셈 메서드 393토큰(1차 실행). "메서드 이름 하나만" 질문(시스템에 "Think briefly", temperature 0)도 nonce만 다른 실행 사이에 생각 길이가 295~1,139토큰으로 크게 달랐다(probe ④⑤, `stop-check-output.txt`). → 512토큰 예산에서 잘림이 흔할 것으로 보임(S3에서 30/30 확인).

## 결론
* raw ChatML + `<think>\n` 프리필은 내장 렌더러와 같은 프롬프트로 판단한다(토큰 수 일치). 설계 §8.2의 기본 템플릿을 그대로 쓴다.
* 생각 텍스트는 raw 모드에서 `response`로 오므로 `thinking` 필드 처리는 필요 없다(알 수 없는 필드 무시 규칙 유지).
* `</think>`·`<|im_end|>`는 특수 토큰이다. **`stop` 옵션에는 의존하지 않는다.** 생성은 `num_predict`(서버 제한)와 종료 토큰 `<|im_end|>`로만 끝난다고 보고, `</think>` 이후 텍스트는 클라이언트가 잘라 버린다(설계 D23).

## 설계 반영
| 위치 | 변경 |
| :--- | :--- |
| §8.2 `template.json` | 확정값: `docs/spikes/S1-template.json` (설계 초안과 같음 + `endOfTurn` 추가) |
| §8.2 OllamaBackend | `thinking` 필드는 raw 모드에서 오지 않음(무시 규칙 유지). `prompt_eval_cached_count`를 `LlmChunk`에 추가해 로그에 기록 |
| §8.13 doctor | `/api/show` 템플릿 문자열 비교 대신: ① `RENDERER`/`PARSER` 이름 표시 ② 짧은 고정 프롬프트를 `/api/chat`과 raw로 보내 `prompt_eval_count`가 같은지 비교 |
| §8.2 QwenStreamClient | `stop`은 보내되 동작을 가정하지 않음. THINK 응답은 첫 `</think>` 앞까지만 사용(D23) |
| §8.1 sampling 기본값 | Modelfile 기본값(temperature 0.6, top_p 0.95, top_k 20, presence_penalty 0, repeat_penalty 1)과 설계 think 기본값이 일치. 최종값은 S3에서 |
