# M4 현장 모델 파일 기준선 (`qwen3.6:35b`, GGUF `Q4_K_M`)

> **대체됨 (2026-10-28):** 현장 질문지 회신에서 서버 모델이 `qwen3.6:35b-a3b-q4_K_M`(ID `07d35212591f`)으로 확인됐다. 이 문서의 `qwen3.6:35b`와 다른 파일이다. 현장과 비교할 외부 기준은 [m4-field-model-baseline.md](m4-field-model-baseline.md)다. 이 문서는 기록으로 남긴다.

* **날짜:** 2026-10-20
* **용도:**
  * 폐쇄망 서버에 설치된 모델 `qwen3.6:35b`와 **같은 파일**로 잰 외부 기준이다.
  * M3b 기준선([m3b-baseline.md](m3b-baseline.md))은 다른 파일(`qwen3.6:35b-a3b-coding-nvfp4`)로 쟀다. 그래서 현장 결과와 직접 비교할 수 없었다.
* **환경:** macOS arm64(Darwin 25.6.0), .NET SDK 10.0.302, Ollama 0.40.0(실행기 llama.cpp, GPU 100%)
* **코드:** `df112e5`(pelly 0.9.1). 프롬프트 `cs-v2`·`sql-v1`, `llm.thinkMode = retry`, `budget.think = 512`, 샘플링 기본값(M3b와 같다).
* **버전 기록:** [deploy/version-record.json](../deploy/version-record.json)
  * digest `a7eb95c53bcf…`, 형식 `gguf`, 양자화 `Q4_K_M`
  * GGUF SHA-256 `d372de8e934898a59e6ccfabc3368474711384d8f1fd4d22d87a3f0a45400cdc`

## 1. 모델 파일을 구한 방법

Apple 칩 Mac에서 `ollama pull qwen3.6:35b`를 하면 GGUF가 아닌 파일이 내려온다.

* 받은 것은 safetensors `nvfp4` 텐서 1,045개다(Ollama가 Apple 칩용 판을 고른다).
* 기존 `qwen3.6:35b-a3b-coding-nvfp4`와도 다른 파일이다. 레이어 1,057개 중 6개만 겹친다.
* 이 파일로 잴 뻔했다. E2E 1회차를 돌리던 중 `ollama show`의 `quantization nvfp4`를 보고 멈췄고, 그 결과는 버렸다.

그래서 공개 저장소의 GGUF 매니페스트를 직접 받아 로컬 Ollama 저장소에 넣었다.

* 매니페스트: `registry.ollama.ai/v2/library/qwen3.6/manifests/35b`. 요청 헤더는 `Accept: application/vnd.docker.distribution.manifest.v2+json`이다.
  * 이 매니페스트는 `"runner": "llamacpp"`, `"format": "gguf"`다.
  * 원본 태그는 `qwen3.6-source:35b-a3b-mtp-q4_K_M-20260824`다.
* 레이어 다섯 개를 받았다: 모델 21.7GB, 비전 프로젝터 0.9GB, 라이선스, 파라미터, 설정.
  * 각각 받은 뒤 SHA-256이 digest와 같은지 확인했고, `~/.ollama/models/blobs/sha256-<hex>`에 넣었다.
* 매니페스트는 `manifests/registry.ollama.ai/library/qwen3.6/35b`로 저장했다. 그 뒤 `ollama list`의 ID가 `a7eb95c53bcf`가 됐다(매니페스트 SHA-256 앞 12자).
* **현장 대조:** 서버의 `ollama list`에서 `qwen3.6:35b`의 ID가 `a7eb95c53bcf`면 이 측정과 같은 파일이다.
  * 다르면 다른 시점에 받은 파일이다. 그 경우 이 기준은 참고치로만 쓴다.
  * 질문지 1-5·1-6(`docs/m4-field-questionnaire.md`)의 답으로도 확인된다.

남는 차이는 실행 환경 하나다: Mac(Metal)과 현장 GPU 서버. 같은 파일이라도 백엔드가 다르면 출력이 조금 다를 수 있다.

## 2. 벤치마크 (50건 × 3회, 두 번)

같은 파일로 두 번 돌렸다.

* 1차: 태그 `qwen3.6:35b-gguf`
* 2차: 현장과 같은 이름 `qwen3.6:35b`

두 번 모두 실행 전에 SQL 코퍼스를 다시 배포했다. 실행 전후 DB 객체 스냅숏을 비교했고, 두 번 모두 바뀐 것이 없었다.

```bash
dotnet run --project eval/PellyCoder.Eval -- --config <pelly.json: llm.model=qwen3.6:35b, sql 허용 목록> --repeat 3 --out <폴더>
```

| 지표 | 목표 | 1차 회차 1·2·3 | 2차 회차 1·2·3 |
| :--- | :--- | :--- | :--- |
| 검증 불변 조건 | 100% | 100%(위반 0 / 독립 검증 32, 생략 18) ×3 | 같음 |
| TTFT 중위 / P90 | < 1.0초 | 0.39/0.68 · 0.35/0.42 · 0.34/0.61초 | 0.53/0.82 · 0.50/0.67 · 0.46/0.68초 |
| Thinking | ≤ 512 토큰 | 최대 512, 잘림 1건(sql-018, 2차 시도에서 통과) ×3 | 같음 |
| 패치 포맷 파싱 성공률 | > 99.5% | 100.0%(EDIT 51회) ×3 | 같음 |
| 최종 작업 성공률 | ≥ 85% | 92.0%(46/50) ×3 | 같음 |
| 작업 시간 중위 / P90 | < 15초 / < 30초 | 1.84/4.62 · 1.56/3.88 · 1.63/4.06초 | 1.94/4.23 · 1.66/4.30 · 1.67/4.23초 |

* 평균 성공 **46.00**(표본 표준편차 0, 여섯 회차 모두 46), 작업 시간 중위값 1.67초(1차)·**1.75초**(2차)
* 범주 평균: cs-legacy **18.00** · cs-modern **8.00** · sql **20.00**
* 2차 요약을 [m4-gguf-baseline.summary.json](m4-gguf-baseline.summary.json)으로 커밋했다(`formatVersion` 2, 모델 digest 포함).
* 독립 검증 생략 18건은 모두 Legacy다(macOS라 패치 적용·모양 검사까지만 판정).

### 실패 작업 (여섯 회차 모두 같은 4건, 모두 같은 수정)

| id | 원인 | 수정본 | M3b(`nvfp4`) |
| :--- | :--- | :--- | :--- |
| cs-modern-002 | 모델 실수(새로 생김) | `Math.Round(refund, -1, MidpointRounding.AwayFromZero)`. 빌드는 통과한다. 하지만 .NET `decimal`은 음수 자릿수를 받지 않아 실행 때 `ArgumentOutOfRangeException`이 난다. | 3/3 통과(`Math.Round(refund / 10m, …) * 10m`) |
| cs-modern-003 | 모델 실수 | `ORDER BY IsGiftItem DESC, OrderItemId`. 사은품이 앞에 온다. 요청과 반대 방향이다. | 2/3(같은 실수 1회) |
| cs-modern-007 | 모델 실수(같은 모양) | 길이 검사를 기존 `IsNullOrWhiteSpace` 블록 안에 넣었다. 그래서 공백뿐인 키워드가 null이 되지 않는다. | 0/3 |
| cs-modern-008 | 모델 실수(같은 모양) | `CommissionRate.Value > 0`이라 수수료율 0도 기본값으로 바뀐다. | 0/3 |

* cs-modern-002는 **빌드 검증으로는 못 잡는 실행 시 오류**다. `--test`를 쓰고, 그 경로를 덮는 테스트가 있어야 잡힌다. 현장에서도 diff를 읽어야 하는 이유가 하나 더 생겼다.
* 판정 도구 오류(`JudgeTooling`)와 판정 산물로 분류한 실패는 없다.

### M3b 기준선과 비교 (`compare`)

| 범주 | M3b `nvfp4` | GGUF `Q4_K_M` |
| :--- | ---: | ---: |
| cs-legacy | 18.00 | 18.00 |
| cs-modern | 9.67 | 8.00 |
| sql | 20.00 | 20.00 |
| 전체 | 47.67 | 46.00 |
| 시간 중위 | 1.90초 | 1.67~1.75초 |

* `compare docs/m3b-baseline.summary.json <1차 summary.json>`는 거절(종료 3)이었다. 규칙 1(−1.67)과 규칙 2(cs-modern −1.67)에 걸렸다.
* 이 차이는 튜닝 판정이 아니라 **모델 파일 차이**다. M3b 기준선에는 digest가 없어서(`formatVersion` 1) 판정 불가(4)로 막히지 않았다.
* 앞으로 현장 결과와 비교할 외부 기준은 이 문서의 값이다. `deploy/benchmark-summary.md`와 `deploy/field-results.md`를 이 값으로 바꿨다.

## 3. 실제 모델 E2E (3회)

```bash
PELLY_TEST_MODEL=qwen3.6:35b-gguf … dotnet test tests/PellyCoder.Integration.Tests -- --filter-namespace "*E2E*" --fail-skips on --minimum-expected-tests 1
```

E2E 테스트는 28건이다.

| 회차 | 통과 | 실패 |
| :-: | :-: | :--- |
| 1 | 26 | `Test_ContradictingEdit_FailsWithTestErrors_Restored`, `Report_AndSanitize_OverRealRuns` |
| 2 | 27 | `Report_AndSanitize_OverRealRuns` |
| 3 | 27 | `Report_AndSanitize_OverRealRuns` |

* 실패는 모두 모순 요청에서 종료 0이 나온 것이다(4절의 우회). 나머지 26건은 3/3이다.
  * 26건: DTO 슬라이스, `--test` 통과, M1·M2a Shipped 동작 판정 6건, 빌드·SQL E2E 등
* `Report_AndSanitize`는 3/3 실패했고, 셋 다 모순 요청이 **첫 시도에서** 종료 0이었다(약 6초).
  * 이 테스트는 같은 작업 공간에서 환불 수정을 먼저 한 뒤 모순 요청을 보낸다. 단독 실행(4절)과 조건이 다르다.
  * 첫 시도 수정의 모양은 확인하지 않았다(작업 공간을 보존하지 않음).

## 4. 모순 요청 우회율 (단독 20회)

`Test_ContradictingEdit_FailsWithTestErrors_Restored`를 단독으로 20회 돌렸다. 각 실행의 기록은 `PELLY_TEST_KEEP`으로 보존했다.

| 결과 | 횟수 | 시도 수 |
| :--- | -: | :--- |
| 종료 1, `TestErrors`(기대) | 10 | 모두 4 |
| 종료 0(요청 되돌림) | **9** (45%) | 2번째 5건 · 4번째 4건 |
| 종료 1, `Overlap`(우회 아님) | 1 | 4 |

* 되돌림 9건은 모두 `throw`를 로그(`_logger.LogInformation("배송 중인 주문을 취소합니다…")`)로 바꾼 모양이다. M3a·`nvfp4` 재측정과 같은 종류다.
* `nvfp4` 단독 재측정은 7/20(35%)이었다. 이번 9/20(45%)과 이 표본으로는 구별되지 않는다. 받아들인 한계도 그대로다: pelly는 요청 이행 여부를 판정하지 못한다.
* **`Overlap` 1건:**
  * 종료 1이고 파일도 복원됐다. 다만 마지막 실패 유형이 `Overlap`이라 테스트의 단언(`TestErrors` 또는 `NoChange`)에 걸렸다.
  * 원인: 4번째 응답에서 모델이 패치 블록 뒤에 영어 추론 문단("Wait, that does nothing…")을 쏟아냈다. 그 안에 SEARCH 블록이 하나 더 들어 있었다.
  * 보존한 응답 70개 중 1개다. `nvfp4` 재측정의 69개에는 없었다. 벤치마크의 패치 포맷 파싱은 100%였다.

## 5. 측정하지 않은 것

* 현장 GPU 서버에서의 시간·TTFT. Mac 값은 참고치다.
* Windows에서 실제 모델을 부르는 경로. CI에는 Ollama가 없다.
* `Report_AndSanitize`의 첫 시도 우회 모양.
