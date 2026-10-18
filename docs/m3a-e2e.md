# M3a 실제 모델 E2E 실행 기록 — DTO 슬라이스·`--test`·리포트/비식별

* **날짜:** 2026-10-18
* **환경:** macOS arm64, .NET SDK 10.0.302, Ollama 0.40.0, 모델 `qwen3.6:35b-a3b-coding-nvfp4`, 프롬프트 `cs-v2`(변경 없음), `llm.thinkMode = retry`, `budget.think = 512`
* **코드:** `5fd5164`(최종 — no-change 가드·테스트 피드백 문장·동작 판정). 3절의 일부 측정은 `32cc9ba`(동작 판정 전)에서 했다.
* **대상:** `eval/corpus/Modern`을 링크 없는 임시 경로(macOS `/var` → `/private/var` 링크를 푼 경로)로 복사하고 상대 경로로 `dotnet restore Modern.sln`한 사본. 사본에는 `global.json`이 없다(테스트 실행기가 csproj에서 xunit v3를 판정). 코퍼스 원본은 수정하지 않는다.

## 1. 테스트 (`tests/PellyCoder.Integration.Tests/E2E/M3aEndToEndTests.cs`)

```bash
PELLY_TEST_OLLAMA=http://localhost:11434 PELLY_TEST_MODEL=qwen3.6:35b-a3b-coding-nvfp4 PELLY_TEST_SQL=<연결 문자열> \
  dotnet test tests/PellyCoder.Integration.Tests -- --filter-namespace "*E2E*" --fail-skips on --minimum-expected-tests 1
```

| 테스트 | 실행 | 단언 |
| :--- | :--- | :--- |
| `Dto_InPrompt_AndEditSucceeds` | cs-modern-002 요청(`CalculateRefundAsync`, 10원 단위 반올림) | 종료 0 · `attempt-1/prompt.txt`에 `[READ-ONLY: dto]`, dto 구간에 `IsGiftItem` · 대상에 `Math.Round` |
| `Test_PassingEdit_RunsTests_Exit0` | 같은 요청 + `--test`. 테스트가 단언하는 28,000·22,000원은 이미 10원 단위라 반올림해도 그대로다 | 종료 0 · `tests/baseline-*.trx`와 `tests/verify-*.trx`가 있고 각각 결과 3건 이상 · "원본 상태 테스트를 실행하지 못해" 알림 없음(테스트를 건너뛰고 통과하는 경로는 실패로 본다) · `failureKind` 없음 |
| `Test_ContradictingEdit_FailsWithTestErrors_Restored` | "배송 중(Shipped)인 주문을 취소하려고 하면 InvalidOperationException을 던지도록 수정해줘" + `--test`. 코퍼스 테스트 `CancelAsync_WhenShipped_CurrentlyCancels`가 수정 전 동작(배송 중 주문도 취소됨)을 고정하므로 요청과 모순된다 | 종료 1 · 테스트가 실제로 돌았음(위와 같음) · `failureKind ∈ {TestErrors, NoChange}` · `errorCodes ∋ TEST` · 파일 바이트가 원본과 같음 |
| `Report_AndSanitize_OverRealRuns` | 한 작업 공간에서 실제 실행 2건(통과 수정, 모순 `--test` 수정) 뒤 빌드된 CLI를 자식 프로세스로 `pelly report`, `pelly report --sanitize --out <폴더>` | report 종료 0 · `실행 2건`·`최종 작업 성공률`·실패 유형 · json·md 각 1개 · 두 파일 어디에도 `OrderService`·`Shop.Api`·멤버 이름·모델 이름·작업 공간/임시/홈 경로·`/private/`·`/var/`·`/Users/`·`.pelly`·`.cs`·RunId가 없음 · md에 `\| 실행 수 \| 2 \|`, 실패 유형, `TEST` |

**M1·M2a 테스트 판정 변경 (동작 판정):** CancelAsync Shipped 요청을 쓰는 E2E 6건이 공유 판정 `CsEndToEndTests.AssertShippedOrdersNoLongerCancelledAsync`를 쓴다.
대상은 M1 `ModernCancelAsync_AddsShippedGuard`와 M2a 다섯 건(`BuildEndToEndTests`의 `FirstAttemptMismatch…`, `FirstAttemptBuildError…`, `LineTarget…`, `NoMethodNoLine_RequestIdentifier…`, `NoMethodNoLine_ModelPick…`)이다.

* 수정한 사본(링크 없는 경로)에서 Core `TestRunner`로 코퍼스 테스트 프로젝트를 빌드·실행한다.
* `CancelAsync_WhenShipped_CurrentlyCancels`(수정 전 동작을 고정)가 이제 `InvalidOperationException`으로 **실패**해야 한다.
* 나머지 코퍼스 테스트(`CalculateRefundAsync` 두 건)는 모두 **통과**해야 한다.
* 이전 판정은 M1이 `OrderStatus.Shipped` 개수 증가, M2a가 조건이 `… OrderStatus.Shipped)`로 끝나는지 보는 정규식이었다. 둘 다 올바른 수정을 거부했다(4절).
* 그대로 둔 것: 종료 0, 대상만 바뀜, 구문 오류 없음. M2a는 기존 Delivered 가드가 여전히 던지는지도 정규식으로 본다 — 이 경로는 코퍼스 테스트가 덮지 않는다.

## 2. 최종 3회 실행 (`5fd5164`, E2E 네임스페이스 전체 27건)

| 테스트 | 1회 | 2회 | 3회 | 통과 |
| :--- | :-: | :-: | :-: | :-: |
| `Dto_InPrompt_AndEditSucceeds` | 통과 | 통과 | 통과 | 3/3 |
| `Test_PassingEdit_RunsTests_Exit0` | 통과 | 통과 | 통과 | 3/3 |
| `Test_ContradictingEdit_FailsWithTestErrors_Restored` | 통과 | **실패** | 통과 | 2/3 |
| `Report_AndSanitize_OverRealRuns` | 통과 | 통과 | 통과 | 3/3 |
| (M1) `ModernCancelAsync_AddsShippedGuard`(동작 판정) | 통과 | 통과 | 통과 | 3/3 |
| (M2a) CancelAsync 다섯 건(동작 판정) | 통과 | 통과 | 통과 | 3/3 |
| 그 밖의 E2E 17건(M1 Legacy·DryRun, M2b SQL 등) | 통과 | 통과 | 통과 | 3/3 |

* 합계 27 · 건너뜀 0 (세 번 모두). 실패는 0 · 1 · 0이다.
* 2회차 `Test_ContradictingEdit` 실패는 3절의 의미상 회피다. 3번째 시도가 `throw`를 `_logger.LogInformation("배송 중인 주문을 취소합니다…")`로 바꿨고, 테스트가 통과해 종료 0으로 끝났다. 기대값은 바꾸지 않았다.
* 모순 `--test` 실행은 회차마다 2건이다(그 테스트 1건 + 리포트 테스트 안의 1건). 6건 중 5건이 종료 1·`TestErrors`·시도 4로 끝났다.
* M2a 다섯 건과 M1 한 건, 즉 동작 판정 6건이 18회 모두 통과했다. 판정 안에서 수정 전 동작을 고정한 테스트가 실패했고, 다른 코퍼스 테스트는 통과했다.
* 동작 판정 전 코드(`32cc9ba`)의 3회 실행은 실패 0 · 0 · 1이었다. 1건은 M2a `FirstAttemptMismatch`의 정규식이 올바른 `Shipped || Delivered` 수정을 거부한 것이다(4절).

## 3. 모순 요청에서 모델이 요청을 되돌리는 문제 (결정 a+b)

`--test`에서 수정 전 동작을 고정한 테스트가 실패하면, 모델이 자기 수정을 지워 테스트를 통과시키고 pelly가 **요청을 이행하지 않은 채 종료 0**으로 끝나는 일이 있었다.

* **(a) no-change 가드:** C#·SQL APPLY는 패치한 대상(C#: 대상 멤버 텍스트, SQL: 대상 배치)을 **원본** 대상과 비교한다. 비교 규칙은 벤치마크 판정과 같다(공백 연속을 한 칸으로 접음). 같으면 쓰기 전에 거부한다: `MatchFailed`, 실패 유형 `NoChange`, 피드백 `NO CHANGE: the edit leaves the target identical to the original. Make the requested change.`. 이전 시도가 아니라 원본과 비교하므로 2번째 시도가 1번째를 되돌리는 경우도 잡는다. 문자열 리터럴 안의 공백도 접어서 비교하므로, 리터럴의 공백만 고친 수정도 `NoChange`로 거부된다(받아들인 한계).
* **(b) 테스트 피드백 문장:** `TESTS FAILED` 피드백 끝에 `Keep the requested change; do not undo it to make tests pass.`를 붙인다. 테스트별 결과 모드와 종료 코드만 보는 모드(R7) 모두 붙이며, 항목을 줄여도 이 문장은 남는다.

### 측정 (같은 요청, 하나는 3회 실행 안, 하나는 `Test_ContradictingEdit` 단독 실행)

| 코드·조건 | 실행 | 종료 1(기대) | 종료 0(요청 되돌림) |
| :--- | -: | -: | -: |
| (a)+(b) 전 (`ad88c11`, cs-v2) — M3a 클래스만 1회(2건) + 단독 6회 | 8 | 2 | **6** (75%) |
| (a)+(b) 뒤 `32cc9ba` — 3회 실행 안(회차당 2건) | 6 | 6 | 0 |
| (a)+(b) 뒤 `32cc9ba` — 단독 8회 | 8 | 4 | **4** |
| (a)+(b) 뒤 `5fd5164` — 2절 3회 실행 안 | 6 | 5 | **1** |
| (a)+(b) 뒤 합계 | 20 | 15 | **5** (25%) |

* 3회 실행 안에서는 12건 중 1건, 단독 실행에서는 8건 중 4건이 되돌림이었다.
* 두 조건의 차이는 다른 E2E 테스트 클래스와 병렬로 도는지 여부다. 이 표본으로는 그 차이가 우연인지 가를 수 없다.
* `32cc9ba`와 `5fd5164` 사이 제품 변경은 `ForTestExitCode` 문장뿐이다. 이 요청은 테스트별 결과 모드라 그 경로를 타지 않는다.

### 되돌림 모양

| 모양 | (a)+(b) 전 | (a)+(b) 뒤 | 가드가 잡는가 |
| :--- | :-: | :-: | :--- |
| 가드 블록 삭제 → 원본과 공백만 다름 | 관찰 | — | 잡음(`NoChange`) |
| SEARCH = REPLACE인 무변경 블록 | 관찰 | — | 잡음(`NoChange`) |
| `throw`를 주석 처리(`// throw new …`, 또는 `if` 블록 전체 주석) | 관찰 | 1 | 못 잡음 |
| `throw`를 로그로 바꿈(`_logger.LogInformation("배송 중인 주문을 취소합니다…")`, `if` 유지 또는 제거) | 관찰 | 4 | 못 잡음 |

* (a)+(b) 뒤 남은 5건은 모두 대상이 원본과 **다른** 의미상 회피다. 가장 흔한 모양은 `throw`를 로그로 바꾸는 것이다(4/5). pelly는 요청 이행 여부를 판정할 수 없어 이 경우를 잡지 못한다(결정에서 받아들인 한계). 이번 측정에서 `NoChange`는 한 번도 발화하지 않았다 — 개선은 주로 (b)에서 왔다.
* 테스트 기대값(종료 1·원본 복원)은 바꾸지 않았다.

## 4. M1·M2a 판정을 동작으로 바꾼 근거

DTO 슬라이스가 들어온 뒤 모델이 Shipped 가드를 넣으면서 환불 조건 `Paid || Shipped`도 `Paid`로 줄였다(가드 뒤라 Shipped 분기는 죽은 코드 — 동작 같음). 그래서 개수 판정이 실패했다.

| 코드 | 프롬프트 | 통과(개수 판정) |
| :--- | :--- | -: |
| `eaedae0` (DTO 슬라이스 `eb009fc` 직전) | cs-v2 | 4/4 |
| `ad88c11` (DTO 슬라이스 포함) | cs-v2 | 2/4 |

프롬프트 dto 구간에는 `enum OrderStatus: Pending, Paid, Shipped, Delivered, Cancelled`와 `class Order: …`가 실린다.

M2a 판정은 32cc9ba 3회차에서 `if (order.Status == OrderStatus.Shipped || order.Status == OrderStatus.Delivered) throw …`라는 올바른 수정을 거부했다. 정규식이 `Delivered || Shipped` 순서만 인정했기 때문이다.

동작 판정이 확인하는 것:

* 배송 중 주문을 취소하면 이제 `InvalidOperationException`이 난다(고정 테스트가 그 예외로 실패).
* 환불 계산 두 경우(결제 완료·배송 중)의 금액은 그대로다.
* 조건을 어떤 모양으로 쓰든, 죽은 분기를 정리하든 판정에 영향이 없다.

확인하지 않는 것: Delivered 주문 처리(M2a는 정규식으로 따로 확인), 로그·이력 메시지.

벤치마크 작업 cs-modern-001은 정의를 그대로 둔다(`docs/m3a-benchmark.md`).

## 5. 수동 확인

### `--verbose` (Modern 사본, `--test`)

```text
$ pelly cs Shop.Api/Services/OrderService.cs "환불액의 10원 미만을 버리지 말고 10원 단위로 반올림하도록 바꿔줘" --method CalculateRefundAsync --yes --test --verbose
Preflight 사전 점검: OrderService.cs
Locate 대상 찾기: CalculateRefundAsync
Slice 문맥 구성
Edit (시도 1) 수정 생성 (시도 1/4)
Apply (시도 1) 패치 적용
Verify (시도 1) 구문 검사·빌드·테스트
Review (시도 1) 변경 검토
(diff 생략: Math.Floor → Math.Round(refund / 10m, MidpointRounding.AwayFromZero) * 10m)
실행 요약
│ Preflight │ 0.07 │  Locate │ 0.08 │  Slice │ 0.05 │  Edit │ 1.1 │  Apply │ 1.31 │  Verify │ 1.6 │  Review │ 0.01 │
시도: 1
TTFT: 0.04초, 대기: 0.03초
입력 토큰: local 1801, server 1808, cached 1804
think 토큰: 0
매칭 단계: Exact
오류 코드: -
프롬프트·응답 원문: <작업 공간>/.pelly/runs/20261018-182329-455a
```

종료 0. 단계 표는 원래 세로 표이며 여기서는 한 줄로 줄였다. Apply 1.31초는 원본 상태 빌드·테스트를 기다린 시간이다.

### `--pause-on-exit` (실제 의사 터미널)

```bash
(sleep 45; printf x) | script -q <기록 파일> dotnet src/PellyCoder.Cli/bin/Debug/net10.0/pelly.dll cs Shop.Api/Services/OrderService.cs \
  "주문 상품이 없을 때 남기는 경고 로그에 주문 상태도 함께 남겨줘" --method CalculateRefundAsync --dry-run --yes --pause-on-exit
```

* `script`가 자식에게 의사 터미널을 주므로 표준 입력이 리디렉션으로 보이지 않는다.
* 드라이런은 약 3초 만에 끝났다(`summary.json` 18:23:44). 이어서 diff와 `종료 코드 0. 아무 키나 누르면 창을 닫습니다…`가 나왔다.
* 프로세스는 계속 기다렸고, 45초에 키를 보내자 곧바로 끝났다(`script` 종료 0, +45초).
* macOS에서만 확인했다. Windows 콘솔 키 대기는 확인하지 않았다(Windows 미확인).

### `pelly report` 표 (`32cc9ba` 3회 실행 1회차의 리포트 테스트 작업 공간, 실행 2건)

| 지표 | 값 |
| :--- | :--- |
| 검증 불변 조건 | 독립 검증 없음 — 벤치마크 실행기로 측정 |
| 초기 응답 지연(TTFT) | 중위 0.04초 / P90 0.06초 (TTFT 2건) |
| 대기(Queue) | 중위 0.03초 / P90 4.07초 |
| 추론 예산 제어(Thinking) | think 최대 512 토큰, 잘림 1건 |
| 패치 포맷 파싱 성공률 | 100.0% (5/5 시도) |
| 최종 작업 성공률 | 50.0% (1/2건, 종료 코드 0) |
| 단일 작업 완료 시간 | 중위 1.90초 / P90 70.93초 (단계 합) |

이어서 종료 코드(0: 1, 1: 1)·실패 유형(TestErrors: 1)·시도 수(1회: 1, 4회: 1)·단계별 평균 시간 표가 나온다.
대기 P90 4.07초는 실제 서버 대기다. E2E 테스트 클래스가 병렬로 돌며 Ollama 하나를 같이 쓴다.

### 비식별 Markdown (`pelly report --sanitize`, 같은 작업 공간)

```markdown
# Pelly 비식별 리포트

## 요약

| 지표 | 값 |
| --- | --- |
| 실행 수 | 2 |
| 초기 응답 지연(TTFT) | 중위 0.04초 / P90 0.06초 (2건) |
| 대기(Queue) | 중위 0.03초 / P90 4.07초 |
| 추론 예산 제어(Thinking) | think 최대 512 토큰, 잘림 1건 |
| 패치 포맷 파싱 성공률 | 100.0% (5/5 시도) |
| 최종 작업 성공률 | 50.0% (1/2건) |
| 단일 작업 완료 시간 | 중위 1.90초 / P90 70.93초 |

## 종류

| 값 | 건수 |
| --- | --- |
| CSharp | 2 |

## 종료 코드

| 값 | 건수 |
| --- | --- |
| 0 | 1 |
| 1 | 1 |

## 실패 유형

| 값 | 건수 |
| --- | --- |
| TestErrors | 1 |

## 시도 수

| 값 | 건수 |
| --- | --- |
| 1 | 1 |
| 4 | 1 |

## 단계별 평균 시간(초)

| 단계 | 초 |
| --- | --- |
| Preflight | 0.06 |
| Locate | 0.00 |
| Slice | 0.04 |
| Edit | 31.31 |
| Apply | 0.02 |
| Verify | 4.98 |
| Review | 0.01 |

## 실행별

| # | 종류 | 프로젝트 | 종료 코드 | 시도 | TTFT | 대기 | think | 오류 코드 | 실패 유형 | 버전 |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 1 | CSharp | Sdk | 0 | 1 | 0.04 | 0.03 | Retry 0 | - | - | pelly 0.1.0, ollama 0.40.0, prompt cs-v2 |
| 2 | CSharp | Sdk | 1 | 4 | 0.06 | 4.07 | Retry 512 | TEST | TestErrors | pelly 0.1.0, ollama 0.40.0, prompt cs-v2 |
```

이름·경로·모델 이름·RunId는 없다(테스트가 json과 md 모두 검사).

## 6. 시도했다가 되돌린 것

프롬프트 `cs-v3` 시도: 시스템 프롬프트 규칙 7을 보강하고 규칙 8("요청과 테스트가 모순되면 요청이 이긴다")을 넣었다.

* 모순 요청의 되돌림은 14회 중 8회로, cs-v2(8회 중 6회)보다 조금 줄었다.
* 대신 M1 `ModernCancelAsync`(개수 판정)는 0/7로 나빠졌다. 그래서 되돌렸다.
* 프롬프트는 `cs-v2` 그대로다.
