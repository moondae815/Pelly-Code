# M1 실제 모델 E2E 실행 기록

* **날짜:** 2026-10-17
* **환경:** macOS arm64, Ollama 0.40.0, 모델 `qwen3.6:35b-a3b-coding-nvfp4`, 프롬프트 `cs-v1`, `llm.thinkMode = retry`(기본)
* **대상:** `eval/corpus`를 임시 폴더로 복사한 사본(코퍼스 원본은 수정하지 않음). 판정은 `eval/benchmark/README.md` 규칙 —
  대상 멤버가 바뀌어야 하고 `mustAdd`(`OrderStatus.Shipped`)가 원본보다 많아야 하며, 대상 밖 텍스트는 그대로여야 한다.
  빌드 판정은 M1 범위 밖이라 구문 검사(`SyntaxVerifier`)로 대신한다.

## 1. E2E 테스트 (`tests/PellyCoder.Integration.Tests/E2E/CsEndToEndTests.cs`)

```bash
PELLY_TEST_OLLAMA=http://localhost:11434 PELLY_TEST_MODEL=qwen3.6:35b-a3b-coding-nvfp4 \
  dotnet test tests/PellyCoder.Integration.Tests -- --filter-namespace "*E2E*" --fail-skips on
```

| 테스트 | 1회 | 2회 | 3회 | 통과 |
| :--- | :-: | :-: | :-: | :-: |
| `ModernCancelAsync_AddsShippedGuard` | 통과 | 통과 | 통과 | 3/3 |
| `LegacyCp949Cancel_KeepsEncodingAndSyntax` | 통과 | 통과 | 통과 | 3/3 |
| `DryRun_DoesNotModifyCorpusCopy` | 통과 | 통과 | 통과 | 3/3 |

세 번 모두 합계 3 · 실패 0 · 건너뜀 0 (전체 7~8초).

### 실행별 `summary.json` (전부 종료 코드 0, 시도 1회, think 미사용 — `thinkTokens` 0, 매치 `Exact`)

| 실행 | 테스트 | EDIT 초 | TTFT 초 | Preflight/Locate/Slice 초 | 입력 토큰(로컬/서버/캐시) |
| :-: | :--- | -: | -: | :--- | :--- |
| 1 | Modern | 2.45 | 0.50 | 0.06 / 0.07 / 0.03 | 1203 / 1210 / 0 |
| 1 | DryRun | 2.00 | 0.05 | 0.04 / 0.00 / 0.01 | 1203 / 1210 / 1206 |
| 1 | Legacy | 1.91 | 0.46 | 0.04 / 0.00 / 0.00 | 1131 / 1138 / 6 |
| 2 | Modern | 1.94 | 0.06 | 0.06 / 0.05 / 0.03 | |
| 2 | DryRun | 1.93 | 0.05 | 0.03 / 0.00 / 0.01 | |
| 2 | Legacy | 1.44 | 0.05 | 0.03 / 0.00 / 0.00 | |
| 3 | Modern | 1.93 | 0.06 | 0.06 / 0.05 / 0.03 | |
| 3 | DryRun | 1.91 | 0.05 | 0.03 / 0.00 / 0.01 | |
| 3 | Legacy | 1.43 | 0.05 | 0.03 / 0.00 / 0.00 | |

* APPLY·VERIFY·REVIEW는 각각 0.01초 미만이다(DryRun은 APPLY 이후를 거치지 않는다). 시도 횟수는 9건 모두 1회였고 교정 시도는 한 번도 일어나지 않았다.
* 모델은 실행 전부터 메모리에 올라 있었다(콜드 로드 약 25초는 이 표에 없다). 첫 실행의 Modern TTFT 0.50초는 같은 프롬프트의 KV 캐시가 없던 값이고,
  이후 같은 프롬프트는 캐시(1206토큰)가 걸려 TTFT가 0.05초대로 내려간다.
* 설계 §2.2·PRD의 단일 작업 완료 시간 목표(중위 15초 / P90 30초, 빌드 검증 포함)와 비교하면 M1 경로(빌드 검증 제외)는 2~3초라 목표 안이다.
  빌드 검증은 M2에서 더해진다.

## 2. CLI 직접 실행 (Modern 사본)

```bash
dotnet run --project src/PellyCoder.Cli -- cs <복사본>/Shop.Api/Services/OrderService.cs \
  "배송 중(Shipped)인 주문을 취소하려고 하면 InvalidOperationException을 던지도록 수정해줘" \
  --method CancelAsync --yes --model qwen3.6:35b-a3b-coding-nvfp4
```

종료 코드 **0**, 시도 **1**, think 사용 **안 함**(`retry` 모드의 첫 시도), TTFT **0.07초**, 매치 `Exact`, 입력 토큰 1203(로컬)/1210(서버)/1206(캐시).

| 단계 | 초 |
| :--- | -: |
| Preflight | 0.072 |
| Locate | 0.070 |
| Slice | 0.030 |
| Edit | 1.907 |
| Apply | 0.004 |
| Verify | 0.001 |
| Review | 0.005 |

콘솔 출력(진행 줄; diff 패널은 터미널 폭에 맞춰 줄바꿈되므로 아래 unified diff로 대신한다):

```text
Preflight (시도 0) 사전 점검: OrderService.cs
Locate (시도 0) 대상 찾기: CancelAsync
Slice (시도 0) 문맥 구성
Edit (시도 1) 수정 생성 (시도 1/4)
Apply (시도 1) 패치 적용
Verify (시도 1) 구문 검사
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

## 3. M1 완료 기준 점검 (설계 §11)

* Modern 코퍼스에서 `pelly cs`가 실제 모델로 성공: 테스트 3/3, CLI 1건 — 충족.
* M1 구성 요소 단위 테스트: 환경 변수 없이 `dotnet test`: 합계 293 · 실패 0 · 성공 284 · 건너뜀 9(환경 의존 테스트).
* CI 4개 잡: 푸시 후 확인 필요(이 작업에서는 푸시하지 않았다).
