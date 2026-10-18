# M3a 실제 모델 E2E 실행 기록 — DTO 슬라이스·`--test`·리포트/비식별

* **날짜:** 2026-10-18
* **환경:** macOS arm64, .NET SDK 10.0.302, Ollama 0.40.0, 모델 `qwen3.6:35b-a3b-coding-nvfp4`, 프롬프트 `cs-v2`(변경 없음), `llm.thinkMode = retry`, `budget.think = 512`
* **코드:** `1d40397`(no-change 가드·테스트 피드백 문장) 위에서 E2E 테스트를 바꾼 작업 트리
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

**M1 테스트 판정 변경 (`CsEndToEndTests.ModernCancelAsync_AddsShippedGuard`):** `OrderStatus.Shipped` 개수가 늘었는지 보던 문자열 판정을 동작 판정으로 바꿨다.
수정한 사본에서 Core `TestRunner`로 코퍼스 테스트를 돌려 `CancelAsync_WhenShipped_CurrentlyCancels`가 이제 `InvalidOperationException`으로 **실패**해야 한다(배송 중 주문이 더 이상 취소되지 않음).
종료 0·대상만 바뀜·구문 오류 없음 단언은 그대로다. 바꾼 이유는 4절.

## 2. 최종 3회 실행 (E2E 네임스페이스 전체 27건)

| 테스트 | 1회 | 2회 | 3회 | 통과 |
| :--- | :-: | :-: | :-: | :-: |
| `Dto_InPrompt_AndEditSucceeds` | 통과 | 통과 | 통과 | 3/3 |
| `Test_PassingEdit_RunsTests_Exit0` | 통과 | 통과 | 통과 | 3/3 |
| `Test_ContradictingEdit_FailsWithTestErrors_Restored` | 통과 | 통과 | 통과 | 3/3 |
| `Report_AndSanitize_OverRealRuns` | 통과 | 통과 | 통과 | 3/3 |
| (M1) `ModernCancelAsync_AddsShippedGuard`(동작 판정) | 통과 | 통과 | 통과 | 3/3 |
| (M2a) `FirstAttemptMismatch_RetryUsesThink_Succeeds` | 통과 | 통과 | **실패** | 2/3 |
| 그 밖의 E2E 21건(M1·M2a·M2b의 C#·SQL) | 통과 | 통과 | 통과 | 3/3 |

합계 27 · 건너뜀 0 (세 번 모두). 실패 0 · 0 · 1.

* **모순 `--test` 실행 9건**(테스트 3 + 리포트 테스트 안의 6)이 모두 종료 1·`TestErrors`·시도 4로 끝났다. 매 재시도의 피드백은 `TESTS FAILED (1 new failure)`였고, 한 번은 4번째 시도가 형식 오류였다.
* **`FirstAttemptMismatch` 3회차 실패는 판정의 거짓 음성이다(모델 수정은 올바름).** 2번째 시도가 기존 Delivered 가드를 `if (order.Status == OrderStatus.Shipped || order.Status == OrderStatus.Delivered) throw …`로 합쳤다. 이 테스트의 정규식은 조건이 `… OrderStatus.Shipped)`로 **끝나는** 모양만 인정한다(M2a 4절에서 `Delivered || Shipped` 순서만 고려). 테스트는 고치지 않았다. 처리는 사람이 정한다.

## 3. 모순 요청에서 모델이 요청을 되돌리는 문제 (결정 a+b)

`--test`에서 수정 전 동작을 고정한 테스트가 실패하면, 모델이 자기 수정을 지워 테스트를 통과시키고 pelly가 **요청을 이행하지 않은 채 종료 0**으로 끝나는 일이 있었다.

* **(a) no-change 가드:** C#·SQL APPLY는 패치한 대상(C#: 대상 멤버 텍스트, SQL: 대상 배치)을 **원본** 대상과 비교한다. 비교 규칙은 벤치마크 판정과 같다(공백 연속을 한 칸으로 접음). 같으면 쓰기 전에 거부한다: `MatchFailed`, 실패 유형 `NoChange`, 피드백 `NO CHANGE: the edit leaves the target identical to the original. Make the requested change.`. 이전 시도가 아니라 원본과 비교하므로 2번째 시도가 1번째를 되돌리는 경우도 잡는다.
* **(b) 테스트 피드백 문장:** `TESTS FAILED` 피드백 끝에 `Keep the requested change; do not undo it to make tests pass.`를 붙인다. 항목을 줄여도 이 문장은 남는다.

### 측정 (같은 요청, `Test_ContradictingEdit` 단독 실행)

| 코드 | 실행 | 종료 1(기대) | 종료 0(요청 되돌림) |
| :--- | -: | -: | -: |
| (a)+(b) 전 (`ad88c11`, cs-v2) | 8 | 2 | **6** (75%) |
| (a)+(b) 뒤 — 2절 3회 실행 | 9 | 9 | 0 |
| (a)+(b) 뒤 — 단독 8회 | 8 | 4 | **4** |
| (a)+(b) 뒤 합계 | 17 | 13 | **4** (24%) |

### 되돌림 모양

| 모양 | (a)+(b) 전 | (a)+(b) 뒤 | 가드가 잡는가 |
| :--- | :-: | :-: | :--- |
| 가드 블록 삭제 → 원본과 공백만 다름 | 관찰 | — | 잡음(`NoChange`) |
| SEARCH = REPLACE인 무변경 블록 | 관찰 | — | 잡음(`NoChange`) |
| `throw`를 주석 처리(`// throw new …`, 또는 `if` 블록 전체 주석) | 관찰 | 1 | 못 잡음 |
| `throw`를 로그로 바꿈(`_logger.LogInformation("배송 중인 주문을 취소합니다…")`, `if` 유지 또는 제거) | 관찰 | 3 | 못 잡음 |

* (a)+(b) 뒤 남은 4건은 모두 대상이 원본과 **다른** 의미상 회피다. pelly는 요청 이행 여부를 판정할 수 없어 이 경우를 잡지 못한다(결정에서 받아들인 한계). 이번 측정에서 `NoChange`는 한 번도 발화하지 않았다 — 개선은 주로 (b)에서 왔다.
* 테스트 기대값(종료 1·원본 복원)은 바꾸지 않았다.

## 4. M1 `ModernCancelAsync_AddsShippedGuard` 판정 변경의 근거

DTO 슬라이스가 들어온 뒤 모델이 Shipped 가드를 넣으면서 환불 조건 `Paid || Shipped`도 `Paid`로 줄였다(가드 뒤라 Shipped 분기는 죽은 코드 — 동작 같음). 그래서 개수 판정이 실패했다.

| 코드 | 프롬프트 | 통과(개수 판정) |
| :--- | :--- | -: |
| `eaedae0` (DTO 슬라이스 `eb009fc` 직전) | cs-v2 | 4/4 |
| `ad88c11` (DTO 슬라이스 포함) | cs-v2 | 2/4 |

프롬프트 dto 구간에는 `enum OrderStatus: Pending, Paid, Shipped, Delivered, Cancelled`와 `class Order: …`가 실린다. 동작 판정은 개수 판정보다 강하다. 개수만 늘리고 배송 중 주문을 여전히 취소하는 수정은 고정 테스트가 계속 통과하므로 실패로 판정된다. 벤치마크 작업 cs-modern-001은 정의를 그대로 둔다(`docs/m3a-benchmark.md`).

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

### `pelly report` 표 (2절 1회차의 리포트 테스트 작업 공간, 실행 2건)

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
