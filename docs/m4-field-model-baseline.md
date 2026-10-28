# M4 현장 모델 기준선 (`qwen3.6:35b-a3b-q4_K_M`, GGUF `Q4_K_M`)

* **날짜:** 2026-10-28
* **용도:**
  * 폐쇄망 서버에 실제로 설치된 모델로 잰 외부 기준이다. 현장 질문지 회신(2026-10-27, 1-5)으로 확인한 모델이다.
    * 태그 `qwen3.6:35b-a3b-q4_K_M`
    * `ollama list` ID `07d35212591f`
  * 이전 기준선([m4-gguf-baseline.md](m4-gguf-baseline.md), 2026-10-20)은 다른 파일로 쟀다.
    * 태그 `qwen3.6:35b`, ID `a7eb95c53bcf`, GGUF SHA-256 `d372de8e…`
    * 현장이 그 태그를 쓴다고 가정했으나, 회신을 받아 보니 아니었다.
  * 앞으로 현장 결과와 비교할 외부 기준은 이 문서의 값이다.
* **환경:** macOS arm64(Darwin 25.6.0), .NET SDK 10.0.302, Ollama 0.40.0(실행기 llama.cpp, GPU)
* **코드:** `0508c4a`(pelly 0.9.1). 프롬프트 `cs-v2`·`sql-v1`, `llm.thinkMode = retry`, `budget.think = 512`, 샘플링 기본값(이전 기준선과 같다).
* **버전 기록:** [deploy/version-record.json](../deploy/version-record.json)
  * digest `07d35212591f…`
  * GGUF SHA-256 `f5ee307a2982106a6eb82b62b2c00b575c9072145a759ae4660378acda8dcf2d`
* **요약:** [m4-field-model-baseline.summary.json](m4-field-model-baseline.summary.json)(`formatVersion` 2, 모델 digest 포함)

## 1. 모델 파일

공개 저장소의 매니페스트를 직접 받아 로컬 Ollama 저장소에 넣었다. 방법은 이전 기준선 1절과 같다. 그때 Apple 칩 Mac의 `ollama pull`이 GGUF가 아닌 판을 내려줬기 때문에 같은 방법을 썼다.

* **매니페스트:** `registry.ollama.ai/v2/library/qwen3.6/manifests/35b-a3b-q4_K_M`
  * SHA-256 `07d35212591f…`가 현장 `ollama list` ID와 같다.
* **레이어:**
  * 모델 23,938,321,664바이트(`f5ee307a…`)
  * 라이선스, 파라미터, 설정
  * 각각 받은 뒤 SHA-256이 digest와 같은지 확인했다.
* **현장 GGUF SHA-256:** 질문지 1-6은 「모름」이었지만 매니페스트가 같으므로 위 모델 레이어 digest가 곧 그 값이다.
  * 현장에서 확인하려면 서버의 `blobs/sha256-f5ee307a…` 파일을 해시한다(`deploy/server-checklist.md`의 「모델」 행).
* **이전 태그(`35b`)와 다른 점:**

  | | `35b-a3b-q4_K_M` (현장) | `35b` (이전 기준선) |
  | :--- | :--- | :--- |
  | 모델 파일 | 단일 GGUF 23.9 GB, 36.0B, 비전·MTP 텐서 포함 | 21.7 GB |
  | 비전 프로젝터 | 모델 파일 안에 있음 | 별도 파일 |
  | 파라미터의 MTP 설정 | 없음 | `draft_num_predict` 있음 |

### 1.1 Ollama 0.40의 로컬 분할

측정을 시작하고 처음 모델을 불러올 때(10:13:59), Ollama 0.40.0이 받아 둔 단일 GGUF를 로컬에서 다시 나눴다.

* 나눈 결과:
  * 텍스트 모델 23,044,340,320바이트(`3f87bb0b…`)
  * 비전 프로젝터 902,821,664바이트(`43637cc0…`)
  * 매니페스트도 두 변형(원본 `b8cba206…`, 분할본 `b52ca437…`)을 담은 목록으로 바뀌었다.
* 실제로 불러온 파일: 서버 로그의 `llama-server --model`이 분할본 `3f87bb0b…`이다.
  * `pelly-eval record`도 분할본 digest `b52ca437…`를 적었다.
  * 버전 기록에는 현장 서버가 가진 원본 매니페스트 digest `07d35212591f…`로 바꿔 적었다.
  * 벤치마크 요약의 digest는 처음부터 `07d35212591f…`다.
* 현장 서버(Ollama 0.30.10)가 같은 분할을 하는지는 모른다.

같은 가중치인지 텐서 단위로 대조했다. GGUF 헤더를 읽어 텐서마다 데이터 구간의 SHA-256을 비교했다.

| 원본 텐서 1,194개 | 결과 |
| :--- | :--- |
| 텍스트 모델(`token_embd`, `blk.0`–`blk.39`, `output` 등) | **모두 같다.** 703개는 이름까지 같다. 이름만 바뀐 텐서(`ssm_dt` → `ssm_dt.bias` 등)도 내용과 모양이 같다 |
| 비전 인코더 `v.*` 164개 | 다르다. q·k·v를 하나의 `attn_qkv`로 합쳤다. pelly는 이미지를 보내지 않는다 |
| MTP 머리 `mtp.*` 7개(정규화 가중치) | `blk.40.*`로 옮기면서 값이 바뀌었다. 이번 실행에서 초안 예측은 쓰이지 않았다(서버 로그 `draft: 0.000 MiB`) |

따라서 이 측정에서 답을 만든 가중치는 현장 파일의 텍스트 모델 가중치와 바이트 단위로 같다.

현장과 남는 차이는 실행 환경이다.
* Ollama 버전: 0.40.0 ↔ 0.30.10
* GPU: Mac Metal ↔ L40S CUDA

## 2. 벤치마크 (50건 × 3회)

실행 전에 SQL 코퍼스(`PellyCorpus`, `PellyCorpusProd`)를 다시 배포했다.

```bash
dotnet run --project eval/PellyCoder.Eval -- --config <pelly.json: llm.model=qwen3.6:35b-a3b-q4_K_M, sql 허용 목록> --repeat 3 --out <폴더>
```

| 지표 | 목표 | 회차 1 | 회차 2 | 회차 3 |
| :--- | :--- | :--- | :--- | :--- |
| 검증 불변 조건 | 100% | 100%(위반 0 / 독립 검증 32, 생략 18) | 같음 | 같음 |
| TTFT 중위 / P90 | < 1.0초 | 0.51/0.88초 | 0.50/0.60초 | 0.43/0.62초 |
| Thinking | ≤ 512 토큰 | 최대 512, 잘림 1건(sql-018, 통과) | 같음 | 같음 |
| 패치 포맷 파싱 성공률 | > 99.5% | 100.0%(EDIT 51회) | 같음 | 같음 |
| 최종 작업 성공률 | ≥ 85% | 94.0%(47/50) | 94.0%(47/50) | 94.0%(47/50) |
| 작업 시간 중위 / P90 | < 15초 / < 30초 | 2.42/5.91초 | 2.15/5.66초 | 2.11/5.31초 |

* 평균 성공 **47.00**(표본 표준편차 0), 작업 시간 중위값 **2.27초**(전 회차)
* 범주 평균: cs-legacy **18.00** · cs-modern **9.00** · sql **20.00**
* 독립 검증 생략 18건은 모두 Legacy다(macOS라 패치 적용·모양 검사까지만 판정).
* 판정 도구 오류(`JudgeTooling`)는 0건이다.

### 실패 작업 (세 회차 모두 같은 3건, 모두 같은 수정)

| id | 수정본 | 이전 기준선(`35b`) |
| :--- | :--- | :--- |
| cs-modern-002 | `Math.Round(refund, -1, MidpointRounding.AwayFromZero)`. 빌드는 통과하고, 실행할 때 `decimal` 음수 자릿수로 예외가 난다 | 같은 실수 0/3 |
| cs-modern-007 | 길이 검사를 기존 `IsNullOrWhiteSpace` 블록 안에 넣었다. 공백뿐인 키워드가 null이 되지 않는다 | 같은 모양 0/3 |
| cs-modern-008 | `CommissionRate.Value > 0 && … <= 1`이라 수수료율 0도 기본값으로 바뀐다 | 같은 모양 0/3 |

* 이전 기준선에서 3/3 실패한 cs-modern-003(정렬 방향)은 이번에 3/3 통과했다. 평균이 46.00에서 47.00이 된 차이는 이 한 건이다.

### 이전 기준선과 비교

| 범주 | `35b` (2026-10-20) | `35b-a3b-q4_K_M` (이번) |
| :--- | ---: | ---: |
| cs-legacy | 18.00 | 18.00 |
| cs-modern | 8.00 | 9.00 |
| sql | 20.00 | 20.00 |
| 전체 | 46.00 | 47.00 |
| 시간 중위 | 1.67~1.75초 | 2.27초 |
| TTFT 중위 | 0.46~0.53초 | 0.43~0.51초 |

* `compare docs/m4-gguf-baseline.summary.json <이번 summary.json>`은 「판정하지 않음」이다(모델 digest가 `a7eb95c53bcf ≠ 07d35212591f`). 튜닝 판정이 아니라 모델 파일이 다르므로 맞는 결과다.
* 시간 중위값이 0.5초쯤 늘었다.
  * 이번 파일은 MTP 초안 예측 설정이 없다(1절 표).
  * 이전 태그는 파라미터에 `draft_num_predict`가 있었다.
  * 이 차이가 원인인지는 따로 재지 않았다. 목표(< 15초)와는 거리가 멀다.

## 3. 실제 모델 E2E (3회)

```bash
PELLY_TEST_OLLAMA=http://localhost:11434 PELLY_TEST_MODEL=qwen3.6:35b-a3b-q4_K_M PELLY_TEST_SQL=… \
  dotnet test tests/PellyCoder.Integration.Tests -- --filter-namespace "*E2E*" \
  --filter-not-class "PellyCoder.Integration.Tests.E2E.PowerShellCompletionTests" --fail-skips on --minimum-expected-tests 1
```

E2E 테스트는 35건이다(이전 기준선 28건 이후 늘었다).

* `PowerShellCompletionTests` 12건은 Windows 전용이라 뺐다. macOS에서는 건너뛰는데, `--fail-skips on`이 그 건너뜀을 실패로 센다.
* 회차마다 SQL 코퍼스를 다시 배포했다.

| 회차 | 통과 | 실패 |
| :-: | :-: | :--- |
| 1 | 33 | `Test_ContradictingEdit_FailsWithTestErrors_Restored`, `Report_AndSanitize_OverRealRuns` |
| 2 | 34 | `Report_AndSanitize_OverRealRuns` |
| 3 | 34 | `Report_AndSanitize_OverRealRuns` |

* 나머지 33건은 3/3이다.
* `Test_ContradictingEdit` 1회 실패는 4절의 우회다.
* **`Report_AndSanitize` 3/3 실패는 모델의 우회가 아니라 테스트 순서 때문이었다** (2026-10-28 조사).
  * 이 테스트는 환불 반올림 수정을 `--test` 없이 먼저 하고, 같은 파일에 모순 요청을 보냈다.
  * 현장 모델은 반올림을 `Math.Round(refund, -1, MidpointRounding.AwayFromZero)`로 쓴다(`--dry-run` 6/6). 빌드는 되지만 실행하면 예외가 난다(자릿수는 0~28).
  * 그래서 모순 요청의 원본 상태에서 `CalculateRefundAsync`를 거치는 테스트 3건이 이미 실패했다. `CancelAsync_WhenShipped_CurrentlyCancels`도 그중 하나다.
  * 모델은 요청대로 `throw`를 넣었지만, `--test`는 새로 실패한 테스트만 센다. 전후 실패가 같은 3건이라 1차 시도에 종료 0이었다.
  * 망가진 반올림을 직접 넣은 사본에서 같은 모양(1차 시도·입력 1,302토큰·오류 코드 없음·종료 0)을 재현했다.
  * 수정: 모순 요청을 깨끗한 원본에서 먼저, 재시도 없이(`maxRetries: 0`) 돌린다. 재시도는 4절 우회가 나오는 곳이고, 이 테스트는 집계할 실패 실행 1건만 있으면 된다. 수정 뒤 단독 5/5 통과.
* 참고: 반올림 요청을 `--test` 없이 쓰면 현장 모델의 깨진 수정이 그대로 승인된다. `--test`를 켜면 재시도로 고쳐진다(`Test_PassingEdit_RunsTests_Exit0` 3/3).

## 4. 모순 요청 우회율 (단독 20회)

`Test_ContradictingEdit_FailsWithTestErrors_Restored`를 단독으로 20회 돌렸다. 각 실행 기록은 `PELLY_TEST_KEEP`으로 보존했다.

| 결과 | 횟수 | 시도 수 |
| :--- | -: | :--- |
| 종료 1, `TestErrors`(기대) | 12 | 모두 4 |
| 종료 1, `NoChange`(기대) | 1 | 4 |
| 종료 0(요청 되돌림) | **7** (35%) | 2번째 4건 · 3번째 2건 · 4번째 1건 |

* 되돌림 7건은 모두 같은 모양이다. `throw`를 로그(`_logger.LogInformation("배송 중인 주문을 취소합니다…")`)로 바꿨다.
* 이전 측정과 비교: `35b` 9/20(45%), `nvfp4` 재측정 7/20(35%). 이 표본 크기로는 셋을 구별할 수 없다.
* 받아들인 한계는 그대로다. pelly는 요청을 이행했는지 판정하지 못한다.
* 이전 기준선에서 1건 나온 `Overlap`(패치 블록 뒤의 추론 문단)은 이번에 없었다.

## 5. 측정하지 않은 것

* 현장 GPU 서버에서의 시간과 TTFT. Mac 값은 참고치다. 현장은 동시 사용자 10명이고 `OLLAMA_NUM_PARALLEL` 값은 아직 회신이 없다.
* Ollama 0.30.10에서의 동작. 1.1절의 분할을 하는지, 같은 출력을 내는지 확인하지 않았다.
* Windows에서 실제 모델을 부르는 경로. CI에는 Ollama가 없다.
* 시간 중위값이 늘어난 원인(MTP 설정 차이인지).
