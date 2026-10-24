# S6 작업 분해 프로브 결과

설계: [docs/superpowers/specs/2026-10-21-s6-planning-probe-design.md](superpowers/specs/2026-10-21-s6-planning-probe-design.md)
계획: [docs/superpowers/plans/2026-10-21-s6-planning-probe.md](superpowers/plans/2026-10-21-s6-planning-probe.md)

## 1. 결론

**판정: 부분(Partial).** 나은 arm은 B(think 켬)이고, 60반복 중 45반복(75%)이 「초안으로 쓸 만함」이었다. 설계 §7에 따라 PRD는 그대로 두고, 잘 된 범주만 초안 후보로 기록한다.

| 범주 | 내용 | arm B | 초안 후보 |
| :--- | :--- | ---: | :--- |
| B | C# + SQL 동시 변경 | 15/15 (100%) | 후보 |
| D | 한 곳만 바꾸면 되는 요청(과분해 함정) | 15/15 (100%) | 후보 |
| C | 같은 파일 여러 메서드 | 10/15 (67%) | 후보(조건부: 사례 c1 1/5) |
| A | 시그니처 변경과 호출부·구현체 전파 | 5/15 (33%) | 아님 |

**별도 판정(§7): 호출부 찾기는 코드로 한다.** 범주 A에서 arm C(Roslyn 구문 탐색)의 재현율은 1.00으로, 모델(A 0.69, B 0.81)보다 높다. 다만 arm C는 이름만 보므로 a3에서 같은 이름의 다른 메서드(`IOrderService.GetOrder`를 부르는 `OrderController.Detail`)를 금지 대상인데도 끌어왔다. 코드로 할 때는 구문이 아니라 의미 모델(SemanticModel의 심볼 비교)로 찾아야 한다.

## 2. 실행 조건

| 항목 | 값 |
| :--- | :--- |
| 모델 | `qwen3.6:35b` GGUF, digest `a7eb95c53bcf96b4bdd008d0fab4a5dac88047d9c1a7a9ab88ed453423fbd87c` (`/api/tags`로 매 실행 확인) |
| 정답 커밋 | `c2344a6a0bf129624da4d894283aef1f9cca65b7` (사람 승인 2026-10-24, 기록 `cases/APPROVED.json`). 123개 원문 모두 이 커밋을 기록했다 |
| 실행 일시 | 2026-10-24 17:32~17:59 KST (A 1분 35초, B 24분 57초) |
| 반복 | arm A·B 각 12사례 × 5 = 60, arm C 범주 A 3사례 × 1 |
| 입력 | 시스템 프롬프트 + 색인 + 요청 최대 2601 토큰(예산 4096 이하) |
| 평균 소요 | A 1.5초/회, B 24.9초/회 |

B의 원문에 적힌 `promptTokens`(최대 4650)는 2단계 호출의 입력이다. 원래 입력에 생각(최대 2048)을 더한 값이고, 출력 1024를 더해도 num_ctx 8192 안이다.

## 3. 요약 표

### 범주별 「초안으로 쓸 만함」

| 범주 | arm A | arm B |
| :--- | ---: | ---: |
| A | 1/15 (7%) | 5/15 (33%) |
| B | 15/15 (100%) | 15/15 (100%) |
| C | 7/15 (47%) | 10/15 (67%) |
| D | 10/15 (67%) | 15/15 (100%) |
| 전체 | 33/60 (55%) | 45/60 (75%) |

### arm별 지표

| arm | 평균 재현율 | 평균 정밀도 | 파싱 실패 | 지어낸 대상이 있는 반복 | 그중 표기만 틀린 반복 | 금지 대상 | 순서 위반 | 종류 오류 | 예시 복사 | think 잘림 | 판정 |
| :--- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | :--- |
| A | 0.86 | 0.71 | 0 | 1 | 1 | 0 | 0 | 0 | 0 | — | 부분 |
| B | 0.88 | 0.94 | 0 | 3 | 0 | 0 | 0 | 0 | 0 | 48/60 | 부분 |
| C | 1.00 | — | 0 | 0 | 0 | 1 | 0 | 0 | 0 | — | (범주 A 전용) |

### 사례별 쓸 만함

| 사례 | 범주 | A | B | C |
| :--- | :---: | ---: | ---: | ---: |
| a1-cancel-returns-refund | A | 1/5 | 5/5 | 1/1 |
| a2-update-status-returns-rows | A | 0/5 | 0/5 | 1/1 |
| a3-rename-getorder | A | 0/5 | 0/5 | 0/1 |
| b1-block-cancel-shipped | B | 5/5 | 5/5 | |
| b2-search-keyword-product | B | 5/5 | 5/5 | |
| b3-search-exclude-cancelled | B | 5/5 | 5/5 | |
| c1-repository-command-timeout | C | 5/5 | 1/5 | |
| c2-controller-reject-bad-id | C | 0/5 | 4/5 | |
| c3-dao-guard-write-methods | C | 2/5 | 5/5 | |
| d1-modern-cancel-shipped | D | 1/5 | 5/5 | |
| d2-legacy-pending-history | D | 4/5 | 5/5 | |
| d3-sql-order-detail-cancel-columns | D | 5/5 | 5/5 | |

## 4. 판정 근거 (§7 표 그대로)

| 조건 | arm B 값 | 충족 |
| :--- | :--- | :--- |
| 전제 기각: ≥ 80% 그리고 모든 범주 ≥ 60% 그리고 지어낸 대상 0 | 75%, 범주 A 33%, 지어낸 대상 3 | 아니오(세 조건 모두 미달) |
| 부분: 50~80% | 75% | **예** |
| 전제 확인: < 50% 또는 지어낸 대상이 반복의 10% 초과 | 75%, 3/60 = 5% | 아니오 |

arm A도 55%, 지어낸 대상 1/60으로 「부분」이다.

표기 실수 판정(모델 경로의 백틱, `#` 앞 공백 같은 것을 지어낸 대상으로 칠지)은 결과를 보기 전에 계획대로 「지어낸 대상」으로 고정했다. 해당 반복은 arm A의 1건뿐이고 판정에 영향이 없다.

## 5. 실패 유형

쓸 만하지 않은 반복의 원인이다. 한 반복에 원인이 여럿일 수 있다.

| arm | 누락만 | 범위 밖 작업(D) | 누락 + 지어냄 | 그룹 표시 누락 | 지어냄 + 범위 밖 |
| :--- | ---: | ---: | ---: | ---: | ---: |
| A (27) | 22 | 4 | 0 | 0 | 1 |
| B (15) | 11 | 0 | 3 | 1 | 0 |

순서 위반, 금지 대상, 예시 복사, 파싱 실패는 두 arm 모두 0이다. 실패의 대부분은 「대상을 덜 찾는 것」이다. 엉뚱한 것을 만들어 내는 실패는 적다.

### 범주별 실패 원문 예시

**범주 A** (a3, arm B 1회): 정답 5곳 중 2곳만 냈다. 테스트의 가짜 DAO, `OrderService.GetOrder`, `OrderService.Cancel`의 호출부를 놓쳤다.

```
- [ ] T1 cs Legacy/Shop.Data/IOrderDao.cs#GetOrder rename to FindOrder [G1]
- [ ] T2 cs Legacy/Shop.Data/OrderDao.cs#GetOrder rename to FindOrder [G1]
```

a2(arm B)는 4회가 테스트 가짜 저장소(숨은 구현체)를 빠뜨렸고(재현율 0.75), 1회는 그것까지 찾았지만 그 줄에 `[G1]`을 달지 않아 탈락했다.

**범주 B:** 실패 없음(A·B 모두 15/15).

**범주 C** (c2, arm A 2회): 같은 컨트롤러의 두 액션 중 하나만 냈다. arm A는 c2 5회 모두 `Detail`을 놓쳤다.

```
- [ ] T1 cs Legacy/Shop.Web/Controllers/OrderController.cs#Cancel add validation for id <= 0 to return BadRequest [G1]
```

c1(arm B)에서는 5회 중 3회가 메서드 6곳 대신 생성자 한 곳을 골랐다. 연결 단위로 시간 제한을 거는 설계다.

```
- [ ] T1 cs Modern/Shop.Api/Data/OrderRepository.cs#OrderRepository set CommandTimeout to 30 on the database context/connection for all DB calls [G1]
```

**범주 D** (d1, arm A 1회): 서비스 한 메서드만 바꾸면 되는데 인터페이스 주석과 테스트까지 작업으로 나눴다. 인터페이스 줄이 범위 밖이다.

```
- [ ] T1 cs Modern/Shop.Api/Services/IOrderService.cs#CancelAsync add a comment or check logic for shipped status validation requirement [G1]
- [ ] T2 cs Modern/Shop.Api/Services/OrderService.cs#CancelAsync add check for OrderStatus.Shipped and throw InvalidOperationException [G1]
- [ ] T3 cs Modern/Shop.Api.Tests/OrderServiceTests.cs#CancelAsync_WhenShipped_CurrentlyCancels update test to expect InvalidOperationException
```

## 6. 정답 결함과 측정 도구의 한계

**정답 결함: 없음.** 실행 뒤 정답을 바꾸지 않았다.

다음 셋은 정답 결함이 아니라 측정 도구의 한계다. 판정은 바뀌지 않지만, 수치를 읽을 때 알아야 한다.

1. **생성자가 색인에 없다.**
   - 색인은 메서드와 속성만 싣는다. 그래서 c1(arm B)의 `#OrderRepository`(실제 있는 생성자)가 「지어낸 대상」으로 셌다. arm B의 지어낸 대상 3건이 모두 이것이다.
   - 세 반복은 재현율이 0이라 이 판정이 없어도 쓸 만하지 않다.
   - 생성자를 실재로 쳐도 arm B는 지어낸 대상 0, 쓸 만함 75%로 여전히 「부분」이다(80% 미만, 범주 A 33%).
2. **기계 판정 「쓸 만함」은 대상만 본다.**
   - c1 arm B 3번째 반복은 대상 6곳을 모두 맞혀 쓸 만함으로 셌다(c1 arm B의 유일한 쓸 만함). 그런데 요청 문구가 이 저장소에 없는 EF API(`this._context.Database.SetCommandTimeout(30)`)를 지시한다. 저장소는 Dapper를 쓴다.
   - 요청 문구 품질은 설계상 기계로 채점하지 않으므로(§2 비목표), §7의 사람 표본 확인이 이 차이를 잡는 유일한 자리다.
3. **arm B의 생각이 대부분 잘렸다.**
   - 60회 중 48회가 think 예산 2048 토큰에서 잘렸다.
   - 잘린 반복의 쓸 만함은 37/48(77%), 잘리지 않은 반복은 8/12(67%)이다. 표본이 작아 잘림이 결과를 낮췄다고 말할 수 없다.
   - 예산을 늘린 재측정은 이 실험의 범위 밖이다(프롬프트·예산 고정).

## 7. 사람 확인 표본 (§6.3)

arm A와 B의 「쓸 만함」 반복에서 각각 10개를 시드 20261021의 난수로 뽑았다.

판단 기준은 각 작업 줄의 요청 문구가 v1 단일 작업으로 실행 가능한가이다. 원문은 `spikes/S6-planning/raw/<arm>/<사례>/<반복>.json`에 있다(gitignore 대상이라 로컬에만 있음).

| arm | 사례 | 반복 | 실행 가능(예/아니오) | 메모 |
| :--- | :--- | ---: | :---: | :--- |
| A | a1-cancel-returns-refund | 3 | | |
| A | b2-search-keyword-product | 2 | | |
| A | b2-search-keyword-product | 5 | | |
| A | b3-search-exclude-cancelled | 2 | | |
| A | b3-search-exclude-cancelled | 5 | | |
| A | c1-repository-command-timeout | 5 | | |
| A | d2-legacy-pending-history | 1 | | |
| A | d2-legacy-pending-history | 5 | | |
| A | d3-sql-order-detail-cancel-columns | 2 | | |
| A | d3-sql-order-detail-cancel-columns | 3 | | |
| B | a1-cancel-returns-refund | 1 | | |
| B | a1-cancel-returns-refund | 2 | | |
| B | a1-cancel-returns-refund | 4 | | |
| B | b1-block-cancel-shipped | 3 | | |
| B | b2-search-keyword-product | 5 | | |
| B | c3-dao-guard-write-methods | 1 | | |
| B | c3-dao-guard-write-methods | 5 | | |
| B | d2-legacy-pending-history | 3 | | |
| B | d2-legacy-pending-history | 5 | | |
| B | d3-sql-order-detail-cancel-columns | 1 | | |

## 8. 다음 행동

- **PRD:** 그대로 둔다. 「모델이 작업을 분해하지 않는다」는 v2 비목표는 범주 A(전파 변경)에서 측정으로 뒷받침되고, 범주 B·D에서는 뒷받침되지 않는다.
- **초안 후보로 기록:** C#+SQL 동시 변경(B)과 한 곳 변경(D)은 think를 켠 모델 초안이 15/15였다.
- **호출부·구현체 전파:** 모델이 아니라 의미 기반 코드 탐색(`pelly spec check` 확장)으로 한다. 구문 탐색은 이름이 같은 메서드를 혼동한다(a3).
- **사람 확인:** 위 표본 20개의 예/아니오를 채운다. 기계 판정을 뒤집지 않고 함께 보고한다.
