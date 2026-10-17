# S4. .NET Qwen 토크나이저

## 질문
`Microsoft.ML.Tokenizers`로 Qwen 토크나이저를 로드해 입력 예산을 서버와 같은 기준으로 셀 수 있는가? (설계 §3.4·§8.2 TokenCounter)

## 환경
| 항목 | 값 |
| :--- | :--- |
| 장비 / Ollama | Apple M5 Pro 48GB / 0.40.0 |
| 서버 모델 | `qwen3.6:35b-a3b-coding-nvfp4` |
| 토크나이저 파일 | `Qwen/Qwen3.6-35B-A3B` (Hugging Face) — 라이선스 Apache-2.0 |
| 라이브러리 | `Microsoft.ML.Tokenizers` **2.0.1** (MIT) — 1.0.x에는 `BpeOptions`가 없음 |
| 날짜 | 2026-10-17 |

파일 SHA-256 (파일은 커밋하지 않음, `.gitignore`):
| 파일 | 크기 | SHA-256 |
| :--- | ---: | :--- |
| `tokenizer.json` | 12.8MB | `5f9e4d4901a92b997e463c1f46055088b6cca5ca61a6522d1b9f64c4bb81cb42` |
| `vocab.json` | 6.7MB | `ce99b4cb2983d118806ce0a8b777a35b093e2000a503ebde25853284c9dfa003` |
| `merges.txt` | 3.4MB | `a9d356d7bdf1ef4949e3e748e95b8e10ad9d4e2e838eddc38a0a7b6b94d1db8d` |

## 방법
* `tokenizer.json` 구조: `BPE`, vocab 248,044, merges 247,587, 정규화 `NFC`, 전처리 = 정규식 `Split`(Isolated) + `ByteLevel`(use_regex false), 특수 토큰 26개(`<|im_start|>`, `<|im_end|>`, `<think>`, `</think>` 포함).
* `spikes/S4-tokenizer/tokenizer.cs`:
  ```csharp
  var options = new BpeOptions("vocab.json", "merges.txt")
  {
      ByteLevel = true,
      SpecialTokens = special,                                   // tokenizer.json added_tokens
      PreTokenizer = new RegexPreTokenizer(new Regex(pattern), special), // tokenizer.json의 Split 정규식
      Normalizer = new NfcNormalizer(),                          // string.Normalize(FormC)
  };
  var tokenizer = BpeTokenizer.Create(options);
  ```
* 샘플 50개(`samples.json`): 코퍼스 C# 멤버 20, SQL 10, 한국어 요청문 10, 템플릿 특수 토큰이 들어간 전체 프롬프트 10.
* 서버 기준: 같은 문자열을 `raw: true`, `num_predict: 1`로 보내 `prompt_eval_count`를 얻음. S2에서 이 값은 캐시분을 포함한 전체 입력 토큰 수로 확인했으므로, 계획의 nonce 삽입·차감은 하지 않았다.

## 측정 결과
(원시: `spikes/S4-tokenizer/results.csv`)

| 범주 | 샘플 | 평균 오차 | 최대 오차 |
| :--- | ---: | ---: | ---: |
| C# 멤버 | 20 | 0.00% | 0.00% |
| SQL | 10 | 0.00% | 0.00% |
| 한국어 요청문 | 10 | 0.00% | 0.00% |
| 템플릿 프롬프트 | 10 | 0.00% | 0.00% |
| **전체** | **50** (서버 기준 13,368토큰) | **0.00%** | **0.00%** |

* 50개 모두 로컬 토큰 수 = 서버 `prompt_eval_count` (예: C# 3,910자 → 944토큰, 템플릿 프롬프트 4,174자 → 1,020토큰, 한국어 46자 → 24토큰).
* 성능: 로드 374ms, 샘플당 계산 중앙값 0.17ms(최대 11.7ms, 첫 호출).
* 대략적인 비율: C# 약 4.4자/토큰, 한국어 요청문 약 2~3자/토큰.

## 결론
* **통과.** 평균 오차 0% ≤ 2% → `QwenTokenCounter`(Microsoft.ML.Tokenizers 2.0.1 `BpeTokenizer`) 채택. `CalibratedEstimator`는 만들지 않는다.
* 서버가 실제로 쓰는 토크나이저와 일치하므로, 입력 예산의 여유분(§3.3 여유 1,536토큰)은 템플릿 오차용이 아니라 출력 여유로만 보면 된다.
* 배포: `vocab.json` + `merges.txt`(합계 약 10MB)를 내장 리소스로 넣는다. 특수 토큰 목록과 전처리 정규식은 `tokenizer.json`에서 뽑아 작은 JSON 리소스로 넣는다(`tokenizer.json` 12.8MB 전체를 넣을 필요 없음).
* 주의: 폐쇄망 모델이 다르면 토크나이저도 다를 수 있다. 같은 모델을 받으면 SHA-256을 비교하고, 다르면 이 측정을 다시 한다. `pelly doctor`에 "짧은 프롬프트 로컬 토큰 수 = 서버 `prompt_eval_count`" 검사를 넣는다.

## 설계 반영
| 위치 | 변경 |
| :--- | :--- |
| §4.1 패키지 | `Microsoft.ML.Tokenizers` **2.0.1** 확정 |
| §8.2 TokenCounter | 1순위 `QwenTokenCounter` 확정(위 구성). `CalibratedEstimator` 삭제 |
| §4 `Llm/Resources/` | `tokenizer.json` 대신 `vocab.json`, `merges.txt`, `tokenizer-meta.json`(특수 토큰·정규식) 내장 |
| §8.13 doctor | 토크나이저 일치 검사 추가(고정 문자열 1개의 로컬 수 vs 서버 `prompt_eval_count`) |
