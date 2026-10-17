# M2a 실제 모델 E2E 실행 기록 — 재시도·think·빌드 피드백·자동 탐색

* **날짜:** 2026-10-18
* **환경:** macOS arm64, .NET SDK 10.0.302, Ollama 0.40.0, 모델 `qwen3.6:35b-a3b-coding-nvfp4`, 프롬프트 `cs-v2`, `llm.thinkMode = retry`(기본), `budget.think = 512`(기본)
* **대상:** `eval/corpus/Modern`을 임시 폴더로 복사하고 `dotnet restore`한 사본(코퍼스 원본은 수정하지 않음). Modern은 SDK 프로젝트라 매 실행이 실제 `dotnet build`로 검증된다.
* **왜:** M1 E2E는 9건 모두 첫 시도에 성공해서 재시도·THINK·피드백 경로가 실제 모델로 한 번도 돌지 않았다. 이번 E2E는 테스트 데코레이터(`CorruptingBackend`)로 첫 코드 응답을 망가뜨려 재시도를 **강제**한다. 재시도 테스트가 1회 시도로 끝나면 데코레이터가 안 걸린 것이므로 실패로 본다.

## 1. 테스트 (`tests/PellyCoder.Integration.Tests/E2E/BuildEndToEndTests.cs`)

```bash
PELLY_TEST_OLLAMA=http://localhost:11434 PELLY_TEST_MODEL=qwen3.6:35b-a3b-coding-nvfp4 \
  dotnet test tests/PellyCoder.Integration.Tests -- --filter-namespace "*E2E*" --fail-skips on --minimum-expected-tests 1
```

`--minimum-expected-tests 1`은 필터가 아무 테스트도 고르지 못했을 때(이름 변경 등) 0건 "통과"를 막는다.

| 테스트 | 강제하는 것 | 단언 |
| :--- | :--- | :--- |
| `FirstAttemptMismatch_RetryUsesThink_Succeeds` | 첫 코드 응답의 SEARCH 첫 줄을 파일에 없는 줄로 바꿈 | 종료 0 · 시도 2 · 요청 순서 `code → think → code` · 2번째 시도만 `think.txt` · 2번째 프롬프트에 `SEARCH NOT FOUND` · `thinkTokens > 0` |
| `FirstAttemptBuildError_FeedbackFixes_Succeeds` | 첫 REPLACE 블록 마지막 줄 바로 앞에 같은 들여쓰기로 `undefinedVar++;` 삽입(R7 1안). 그 자리가 CancelAsync 본문 안의 문장이 아니면(식 중간·추가한 도우미·메서드 밖) 메서드의 마지막 `}` 앞에 넣는다(R7 대체안 — `BuildErrorInjectionTests`가 오프라인으로 검사) | 종료 0 · 시도 ≥ 2 · `errorCodes ∋ CS0103` · 2번째 프롬프트에 `BUILD FAILED`와 `undefinedVar` · 최종 파일에 `undefinedVar` 없음 |
| `LineTarget_Works` | `--line`(CancelAsync 본문 `if (order.Status == OrderStatus.Cancelled)` 줄) | 종료 0 |
| `NoMethodNoLine_RequestIdentifier_Works` | 요청문에 `` `CancelAsync` `` | 종료 0 · LOCATE 모델 선택 요청 없음 |
| `NoMethodNoLine_ModelPick_Works` | 식별자 없는 한국어 요청("주문을 취소할 때 배송 중인 주문이면 예외를 던져서 취소하지 못하게 해줘") | 종료 0 · 모델 선택 요청 정확히 1개 · `대상: OrderService.CancelAsync(int, string) (모델이 선택)` 메시지 |

다섯 테스트 모두 결과 파일에 대해 같은 판정을 더 한다: CancelAsync만 바뀌고 나머지 텍스트는 그대로, 구문 오류 없음,
`== / is / or OrderStatus.Shipped`로 끝나는 조건 바로 아래에서 `InvalidOperationException`을 던지고, 기존 Delivered 가드도 여전히 던진다
(단독이든 `Delivered || Shipped`로 합쳤든). 이 판정을 고른 이유는 4절에 있다.

손상 대상은 고정 번호가 아니라 요청 내용으로 고른다(R6): 프롬프트가 `<<<<<<< SEARCH\n`으로 끝나는 첫 요청이 첫 코드 요청이다.
THINK 요청은 `stop`에 `</think>`, LOCATE 모델 선택 요청은 프롬프트에 `### MEMBERS`가 있다.

## 2. 최종 3회 실행 (커밋한 코드, 시리즈 k)

| 테스트 | 1회 | 2회 | 3회 | 통과 |
| :--- | :-: | :-: | :-: | :-: |
| `FirstAttemptMismatch_RetryUsesThink_Succeeds` | 통과 | 통과 | 통과 | 3/3 |
| `FirstAttemptBuildError_FeedbackFixes_Succeeds` | 통과 | 통과 | 통과 | 3/3 |
| `LineTarget_Works` | 통과 | 통과 | 통과 | 3/3 |
| `NoMethodNoLine_RequestIdentifier_Works` | 통과 | 통과 | 통과 | 3/3 |
| `NoMethodNoLine_ModelPick_Works` | 통과 | 통과 | 통과 | 3/3 |
| (M1) `ModernCancelAsync_AddsShippedGuard` | 통과 | 통과 | 통과 | 3/3 |
| (M1) `LegacyCp949Cancel_KeepsEncodingAndSyntax` | 통과 | 통과 | 통과 | 3/3 |
| (M1) `DryRun_DoesNotModifyCorpusCopy` | 통과 | 통과 | 통과 | 3/3 |

세 번 모두 합계 8 · 실패 0 · 건너뜀 0 (전체 37~38초).

### 시도 수와 think

| 테스트 | 시도 | think 사용 | thinkTokens | think 잘림 | errorCodes |
| :--- | :-: | :--- | -: | :-: | :--- |
| Mismatch (3회 모두) | 2 | 2번째 시도만 | 512 | 예 | — |
| BuildError (3회 모두) | 2 | 2번째 시도만 | 512 | 예 | `CS0103` |
| Line · Identifier · ModelPick · M1 셋 (각 3회) | 1 | 안 함 | 0 | — | — |

* 재시도 경로는 6/6 실제로 탔다(데코레이터가 매번 정확히 1개 응답을 바꿨다 — `RewrittenCount == 1`).
* **THINK는 6/6 모두 예산 512토큰에서 잘렸다**(`thinkTruncated: true`). 잘린 think는 마지막 줄바꿈까지 남겨 CODE 단계에 넘긴다(설계대로). 4절의 유일한 실제 결함은 이 잘린 think에서 나왔다.
* 재시도 없는 테스트(Line·Identifier·ModelPick)는 첫 시도에 성공했다. 이들은 재시도가 아니라 LOCATE 경로를 보는 테스트라 재시도가 없는 것이 정상이다.

### 단계별 시간 (초, 시리즈 k 3회의 범위)

| 테스트 | Preflight | Locate | Slice | Edit | Apply | Verify | TTFT¹ |
| :--- | -: | -: | -: | -: | -: | -: | -: |
| Mismatch | 0.04 | 0.00 | 0.00~0.02 | 10.2~11.1 | 0.01 | 0.52~0.53 | 6.5 |
| BuildError | 0.09 | 0.00 | 0.02 | 10.4~12.3 | 0.00~0.01 | 1.08~1.10 | 7.4~7.9 |
| Line | 0.05 | 0.00 | 0.01~0.02 | 1.89 | 0.00 | 0.52~0.53 | 0.05 |
| Identifier | 0.06~0.07 | 0.07~0.08 | 0.04~0.05 | 1.9~4.1 | 0.00~0.02 | 0.63~0.64 | 1.9~4.1 |
| ModelPick | 0.05 | 0.15~0.16 | 0.00~0.02 | 1.81~1.83 | 0.00 | 0.52~0.54 | 1.8 |

¹ TTFT는 `summary.json`의 값이다(THINK를 쓴 시도면 THINK 첫 토큰). `CorruptingBackend`를 거치는 테스트(Mismatch·BuildError·Identifier·ModelPick)는
데코레이터가 응답을 끝까지 모은 뒤 흘려보내므로 TTFT가 사실상 그 요청의 전체 생성 시간이다 — 사용자가 보는 첫 토큰 시간이 아니다.
데코레이터가 없는 Line 테스트와 CLI 실행(3절)의 0.05~0.07초가 실제 값이다.

* **Baseline 빌드와 EDIT의 겹침:** Baseline 빌드(원본 상태 `dotnet build --no-restore`)는 LOCATE 끝에서 시작해 SLICE·EDIT와 병렬로 돈다.
  같은 사본에서 따로 잰 그 빌드 시간은 빌드 서버가 떠 있을 때 0.46~0.55초, `dotnet build-server shutdown` 직후 1.19초다.
  EDIT가 가장 짧을 때도 1.8초라 Baseline은 늘 EDIT 안에 끝나고, Baseline을 기다리는 APPLY는 0.00~0.03초다 — 빌드 검증이 더하는 대기는 수정 후 빌드(VERIFY) 0.5~0.65초뿐이다.
* BuildError의 Verify 1.1초는 두 번(실패한 1번째 + 통과한 2번째)의 빌드 합이다.
* ModelPick의 Locate 0.15초는 모델 선택 요청(최대 32토큰) 한 번이다. Identifier의 0.07초는 모델을 부르지 않는다.
* 재시도 테스트의 Edit 10~12초는 두 시도 + THINK 512토큰 생성이다.

## 3. CLI 직접 실행 (Modern 사본, `--line`)

```bash
dotnet run --project src/PellyCoder.Cli -- cs <복사본>/Shop.Api/Services/OrderService.cs --line 58 \
  "배송 중(Shipped)인 주문을 취소하려고 하면 InvalidOperationException을 던지도록 수정해줘" \
  --yes --model qwen3.6:35b-a3b-coding-nvfp4
```

58번 줄은 CancelAsync 본문의 `if (order.Status == OrderStatus.Cancelled)`다. 종료 코드 **0**, 시도 **1**, think **안 함**, TTFT **0.07초**, 매치 `Exact`, 입력 토큰 1209(로컬)/1216(서버)/1212(캐시).

| 단계 | 초 |
| :--- | -: |
| Preflight | 0.063 |
| Locate | 0.071 |
| Slice | 0.046 |
| Edit | 1.921 |
| Apply | 0.022 |
| Verify (구문 + `dotnet build`) | 0.530 |
| Review | 0.015 |

콘솔 출력(진행 줄; diff 패널은 터미널 폭에서 줄바꿈되므로 아래 unified diff로 대신한다):

```text
Preflight 사전 점검: OrderService.cs
Locate 대상 찾기: 58번 줄
Slice 문맥 구성
Edit (시도 1) 수정 생성 (시도 1/4)
Apply (시도 1) 패치 적용
Verify (시도 1) 구문 검사·빌드
Review (시도 1) 변경 검토
```

```diff
@@ -67,6 +67,11 @@
                 throw new InvalidOperationException("배송 완료된 주문은 취소할 수 없습니다. 반품을 신청하세요.");
             }
 
+            if (order.Status == OrderStatus.Shipped)
+            {
+                throw new InvalidOperationException("배송 중인 주문은 취소할 수 없습니다. 반품을 신청하세요.");
+            }
+
             if (order.Status == OrderStatus.Pending)
             {
                 _logger.LogInformation("결제 전 주문을 취소합니다. OrderId={OrderId}", orderId);
```

## 4. 최종 시리즈 전에 난 실패와 처리

최종 3회(시리즈 k) 전에 같은 명령을 13회 더 돌렸다. 실패는 모두 아래 넷 중 하나다. 기대값을 낮춘 것은 없다 — 판정을 바꾼 것은
사람이 읽어 **올바른 수정**인데 판정이 떨어뜨린 경우뿐이고, 바꾼 판정은 실제 결함 1건을 계속 떨어뜨린다.

| 시리즈·회 | 실패한 테스트 | 원인 | 처리 |
| :--- | :--- | :--- | :--- |
| 첫 실행 | ModelPick | 테스트의 기대 문자열이 틀림(표시가 `CancelAsync(int, string)`이 아니라 `OrderService.CancelAsync(int, string)`) | 기대 문자열 정정 |
| r2 | ModelPick | **판정 오탐.** 모델이 Shipped 가드를 넣고, 이제 도달할 수 없는 환불 조건의 `\|\| order.Status == OrderStatus.Shipped`를 지웠다(동작 보존). M1 벤치마크 규칙 "`OrderStatus.Shipped` 개수 증가"가 이것을 떨어뜨림 | 개수 대신 "Shipped 조건 아래 throw" 정규식으로 |
| f2 | Mismatch | **실제 결함.** 2번째 시도가 Delivered 가드를 Shipped 가드로 **바꿔치기**했다(배송 완료 주문을 취소할 수 있게 됨). 빌드는 통과하므로 pelly는 성공(종료 0)으로 끝냈다 | 테스트가 옳게 실패. 아래 재생 실험 |
| g1 | Mismatch | `dotnet restore` 경합: 같은 사본이 `/var/...`와 `/private/var/...` 두 경로로 잡혀 Shop.Api를 동시에 두 번 복원, `project.assets.json already exists` | 복원에 `--disable-parallel`(추정 원인 — 재현은 못 함) |
| h3 | Mismatch | **판정 오탐.** 모델이 `Delivered \|\| Shipped`로 기존 가드에 합쳤다(올바름). 그 사이 판정이 "`InvalidOperationException` 개수 증가"였는데 개수가 그대로라 떨어뜨림 | 개수 판정을 버리고 "Shipped에서 throw **그리고** Delivered에서 여전히 throw"로 |

바꾼 판정을 시리즈 f·g·h의 최종 출력(원본에 그대로 적용되는 53건)에 다시 적용하면 52건 통과, f2 한 건만 실패한다. 원본 파일 자체는 Shipped 판정에서 실패한다.

### f2 재생 실험 (프롬프트 리소스로 고칠 수 있는가)

f2의 2번째 시도 프롬프트를 실제 모델에 그대로 다시 보냈다(샘플링 동일, raw 모드).

| 조건 | 올바른 수정 |
| :--- | -: |
| THINK를 매번 새로 생성 | 10/10 |
| f2에서 잘린 think 원문을 그대로 붙임 | 2/5, 4/10 |
| + 시스템 규칙 8 "REPLACE replaces the whole SEARCH text …" | 1/10 |
| + 시스템 규칙 8 "To add code, keep the SEARCH lines unchanged …" | 0/10 |
| + 시스템 규칙 8 "Never delete existing statements unless …" | 0/10 |

결함은 **512토큰에서 잘린 특정 think**에서만 재현되고, 규칙 문장을 더하면 셋 다 오히려 나빠졌다. 그래서 프롬프트 리소스는 바꾸지 않았다(`cs-v2` 그대로).
잘린 think(6/6 잘림)가 지렛대로 보이지만 THINK 예산은 Global Constraints가 정한 기본값이라 이 작업에서는 건드리지 않았다.

### 함께 본 것: SEARCH NOT FOUND 피드백의 "Nearest original lines"

Mismatch 테스트는 SEARCH **첫 줄**을 망가뜨리는데, 가장 가까운 줄 찾기(`SearchReplacePatcher.Nearest`)는 첫 줄 하나로만 찾으므로 피드백이 엉뚱한 곳(`trimmedReason` 부근)을 가리켰다.
모델은 매번 [EDITABLE]에서 다시 복사해 2번째 SEARCH를 맞혔지만(f2 포함), 실제로 모델이 첫 줄을 틀리는 경우에도 피드백이 같은 식으로 빗나간다.

## 5. THINK 예산 측정 (512 · 1024 · 1536)

**왜:** 3절까지의 실행에서 THINK가 6/6 예산 512토큰에서 잘렸고, 유일한 실제 결함(f2)이 잘린 think에서 나왔다. 예산을 늘릴지 정하기 전에 쟀다.

**방법:** 채집한 2번째 시도 프롬프트 두 개를 실제 모델에 raw 모드로 다시 보냈다 — f2의 SEARCH 불일치 프롬프트, 그리고 시리즈 k 1회의 BUILD FAILED 프롬프트(파일 상태는 1번째 시도의 손상된 응답을 적용한 것).
값마다 10회, **THINK는 매번 새로 생성**(잘린 계획을 재사용하지 않음)했고 CODE 예산 2048, `num_ctx` 8192, 샘플링은 기본값이다. 1536이면 입력 4096 + 1536 + 2048 = 7680 ≤ 8192이다.
판정은 E2E의 결과 판정과 같다: 응답이 원본에 적용되고, `== / is / or OrderStatus.Shipped` 아래에서 throw, Delivered 가드도 여전히 throw, `undefinedVar` 없음.
잘림은 `</think>` 없이 `done_reason = length`로 끝난 경우다. 시간은 THINK + CODE 서버 `total_duration`의 합이다.

| 예산 | 경우 | 잘림 | THINK 토큰(중앙값) | 올바른 수정 | THINK+CODE 초(중앙값) |
| -: | :--- | -: | -: | -: | -: |
| 512 | SEARCH 불일치(f2) | 10/10 | 512 | 10/10 | 8.9 |
| 512 | 빌드 오류 | 10/10 | 512 | 10/10 | 7.1 |
| 1024 | SEARCH 불일치(f2) | 10/10 | 1024 | 10/10 | 15.6 |
| 1024 | 빌드 오류 | 10/10 | 1024 | 10/10 | 13.7 |
| 1536 | SEARCH 불일치(f2) | 8/10 | 1536 | 10/10 | 22.3 |
| 1536 | 빌드 오류 | 3/10 | 1337 | 10/10 | 17.9 |

**결정: 512 유지.** 결정 규칙(통제자 판정)은 "두 경우 모두 잘림 50% 미만이고 올바른 수정 ≥ 9/10인 가장 작은 값"이다.
1536도 SEARCH 불일치에서 8/10이 잘려 규칙을 만족하는 값이 없다.
올바른 수정률은 세 값 모두 10/10이라 예산을 늘려 얻는 것은 없고, 시간만 2~2.5배가 된다(재시도 1회당 +7~13초).
f2 결함은 새 THINK에서는 다시 나오지 않았고(512에서 20/20 올바름) 4절처럼 그 특정 잘린 think를 붙였을 때만 재현된다. 드문 꼬리이지 예산 512의 체계적 결함이 아니다.
이 모델은 2048 미만에서 생각을 끝내지 못하는 경우가 많다 — 설계 S3의 "30/30이 512 초과"와 같은 방향이다.

### 리뷰 수정 뒤 재실행 (시리즈 m, 예산 512 그대로)

R7 대체안·Shipped 판정 강화 뒤 같은 명령(`--minimum-expected-tests 1` 포함)을 3회: 세 번 모두 합계 11(E2E 8 + 오프라인 삽입 검사 3) · 실패 0 · 건너뜀 0.
BuildError 3회는 모두 1안 자리(REPLACE 마지막 줄 앞)가 본문 안이라 대체안을 타지 않았다 — 대체안은 `BuildErrorInjectionTests`(식 중간 · 추가 도우미 두 경우)가 덮는다.
재시도 테스트 6회 모두 시도 2, THINK 잘림, BuildError는 `CS0103`.
