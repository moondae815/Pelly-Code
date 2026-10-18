# Modern 판정 테스트

벤치마크 Modern 작업의 동작 판정용 테스트입니다(`judge.tests`). 작업 정의는 `eval/benchmark/tasks/*.yaml`에 있습니다.
판정 프로젝트는 둘이고, 이 문서가 두 프로젝트의 규칙을 함께 적습니다.

| 프로젝트 | 대상 | 방식 |
| :--- | :--- | :--- |
| `Shop.Api.JudgeTests` | `OrderService`, `OrdersController` | `Shop.Api` 프로젝트 참조. 가짜 저장소·서비스(`JudgeFakes.cs`) |
| `Shop.Data.JudgeTests` | `OrderRepository`, `Shop.Jobs`의 `DailySettlementJob` | 코퍼스 소스를 직접 컴파일. 같은 이름의 가짜 Dapper·`SqlConnection`(`FakeSql.cs`)에 묶여 DB 없이 SQL·매개변수를 기록 |

## 규칙
* 이름은 `<작업 id 밑줄 표기>_<기대 동작>`입니다(예: `cs_modern_003_GiftItemsAfterRegularItems`).
* 테스트 하나가 작업 하나를 판정합니다. **원본 코퍼스에서는 실패하고, 올바르게 수정한 뒤에만 통과**해야 합니다.
  * `JudgeTestDefinitionTests`가 원본에서 실패하는지 검사합니다.
  * 그래서 확인할 경우를 한 `[Fact]`에 모읍니다. 원본에서도 통과하는 경우(과잉 수정 검사 등)를 따로 두면 원본에서 실패하지 않습니다.
  * `[Theory]`는 쓰지 않습니다. TRX 이름에 인자가 붙어 작업 정의의 이름과 맞지 않습니다.
* **요청을 옳게 했을 때만 통과**해야 합니다. 과하게 막는 수정도 걸러야 하므로 경계값과 「그대로여야 하는 경우」를 같은 테스트에 넣습니다(예: 200자 초과는 예외, 정확히 200자는 취소됨).
* 다른 방식의 올바른 수정도 통과해야 합니다. 예: 정렬은 SQL `ORDER BY`로 해도 C# `OrderBy`로 해도 됩니다. 가짜 DB(`FakeOrderBy`)가 SQL의 `ORDER BY`를 행에 적용합니다.
* `Shop.Api.JudgeTests` 가짜: `JudgeFakes.cs`(`FakeOrderRepository`, `FakeOrderService`, `FakeLogger<T>`, 주문 견본 `JudgeOrders`).
* `Shop.Data.JudgeTests` 가짜: `FakeSql.cs`(`FakeDatabase`, Dapper 확장 메서드, `DynamicParameters`), `FakeOrderBy.cs`.
  * 한계: 수정본이 가짜에 없는 Dapper API를 쓰면 판정 프로젝트 빌드가 실패합니다(판정 실패). SQL 안에서 값을 바꾸는 수정(예: 수수료율 검사를 `CASE`로 SQL에 넣음)은 가짜 DB가 계산하지 않아 실패로 봅니다. 해석하지 못하는 `ORDER BY` 식도 실패입니다.
* 솔루션(`PellyCoder.sln`, 코퍼스 `Modern.sln`)에 넣지 않습니다. 원본에서 실패하는 테스트라 CI를 깨뜨립니다.

## 빌드
* 저장소 빌드 설정(중앙 패키지 관리 등)을 받지 않도록 프로젝트 폴더마다 `Directory.Build.props`·`Directory.Packages.props`를 둡니다. 코퍼스와 같은 방식입니다.
* 대상 코퍼스는 MSBuild 속성 `JudgeTargetRoot`로 정합니다. 기본값은 저장소의 `eval/corpus/Modern`입니다.
* 실행기는 이 폴더를 `work/<id>/judges/Modern/`으로 복사한 뒤 작업 복사본을 가리켜 빌드합니다.

```bash
dotnet build eval/benchmark/judges/Modern/Shop.Api.JudgeTests -p:JudgeTargetRoot=<코퍼스 복사본>/Modern
dotnet build eval/benchmark/judges/Modern/Shop.Data.JudgeTests -p:JudgeTargetRoot=<코퍼스 복사본>/Modern
```

* 저장소의 코퍼스로 직접 빌드하면 `eval/corpus` 아래에 `bin`/`obj`가 생깁니다. 확인은 복사본에서 하십시오.

## 현재 테스트
| 테스트 | 작업 | 확인하는 것 |
| :--- | :--- | :--- |
| `OrderServiceJudgeTests.cs_modern_001_ShippedThrowsAndOthersStillCancel` | cs-modern-001 | Shipped 취소는 `InvalidOperationException`, 상태·환불 변경 없음. Pending·Paid는 그대로 취소됨 |
| `OrderServiceJudgeTests.cs_modern_002_RefundRoundsToNearestTen` | cs-modern-002 | 환불액 10원 단위 반올림(…7 → 올림, …3 → 내림). 중간값 5원은 쓰지 않음 |
| `DataAccessJudgeTests.cs_modern_003_GiftItemsAfterRegularItems` | cs-modern-003 | 사은품은 일반 상품 뒤, 묶음 안은 `OrderItemId` 순. SQL·C# 정렬 모두 인정 |
| `DataAccessJudgeTests.cs_modern_004_WhitespaceKeywordAddsNoCondition` | cs-modern-004 | 공백뿐인 키워드는 `LIKE` 조건 없음. `" 머그 "`는 그대로 검색 |
| `OrdersControllerJudgeTests.cs_modern_005_NonPositiveIdIsBadRequest` | cs-modern-005 | id 0·-1은 검색 없이 `BadRequest("주문 번호가 올바르지 않습니다.")`. id 1은 200 |
| `OrdersControllerJudgeTests.cs_modern_006_PageSizeOver100IsBadRequest` | cs-modern-006 | pageSize 101은 검색 없이 400. 100·20은 200 |
| `OrderServiceJudgeTests.cs_modern_007_ShortKeywordBecomesNull` | cs-modern-007 | 공백을 뺀 키워드가 2자 미만이면 `null`. 2자 이상은 공백만 뺀 값 |
| `DataAccessJudgeTests.cs_modern_008_InvalidCommissionRateUsesDefault` | cs-modern-008 | 수수료율 -0.1·1.5는 기본 12%. 0·1·0.05는 그대로, NULL은 기본값 |
| `OrdersControllerJudgeTests.cs_modern_009_ArgumentExceptionIsBadRequestWithMessage` | cs-modern-009 | `ArgumentException`(파생 포함)은 `BadRequest(메시지)`. 204·404·409·사유 없음 400은 그대로 |
| `OrderServiceJudgeTests.cs_modern_010_ReasonOver200ThrowsWithoutTruncating` | cs-modern-010 | 201자는 `ArgumentException`, 상태 변경 없음. 200자는 그대로 취소·저장 |
| `DataAccessJudgeTests.cs_modern_011_PageBelowOneReadsFirstPage` | cs-modern-011 | page 0·-3은 `Skip` 0. page 3·크기 10은 `Skip` 20, `Take`는 그대로 |
| `OrderServiceJudgeTests.cs_modern_012_SearchWithoutAnyConditionThrows` | cs-modern-012 | 조건이 하나도 없으면 `ArgumentException`, 저장소 호출 없음. 조건 하나(고객·상태·From·To·키워드)면 검색 |
