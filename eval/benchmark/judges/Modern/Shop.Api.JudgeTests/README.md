# Shop.Api 판정 테스트

벤치마크 Modern 작업의 동작 판정용 테스트입니다(`judge.tests`). 작업 정의는 `eval/benchmark/tasks/*.yaml`에 있습니다.

## 규칙
* 테스트 하나가 작업 하나를 판정합니다. **원본 코퍼스에서는 실패하고, 올바르게 수정한 뒤에만 통과**해야 합니다.
  * `JudgeTestDefinitionTests`가 원본에서 실패하는지 검사합니다.
  * 그래서 확인할 경우를 한 `[Fact]`에 모읍니다. 원본에서도 통과하는 경우(과잉 수정 검사 등)를 따로 두면 원본에서 실패하지 않습니다.
  * `[Theory]`는 쓰지 않습니다. TRX 이름에 인자가 붙어 작업 정의의 이름과 맞지 않습니다.
* 가짜 구현은 `JudgeFakes.cs`(`FakeOrderRepository`, `FakeLogger<T>`, 주문 견본 `JudgeOrders`)를 같이 씁니다.
* 솔루션(`PellyCoder.sln`, 코퍼스 `Modern.sln`)에 넣지 않습니다. 원본에서 실패하는 테스트라 CI를 깨뜨립니다.

## 빌드
* 저장소 빌드 설정(중앙 패키지 관리 등)을 받지 않도록 이 폴더에 `Directory.Build.props`·`Directory.Packages.props`를 둡니다. 코퍼스와 같은 방식입니다.
* 대상 코퍼스는 MSBuild 속성 `JudgeTargetRoot`로 정합니다. 기본값은 저장소의 `eval/corpus/Modern`입니다.
* 실행기는 이 폴더를 `work/<id>/judges/Modern/`으로 복사한 뒤 작업 복사본을 가리켜 빌드합니다.

```bash
dotnet build eval/benchmark/judges/Modern/Shop.Api.JudgeTests -p:JudgeTargetRoot=<코퍼스 복사본>/Modern
```

* 저장소의 코퍼스로 직접 빌드하면 `eval/corpus` 아래에 `bin`/`obj`가 생깁니다. 확인은 복사본에서 하십시오.

## 현재 테스트
| 테스트 | 작업 | 확인하는 것 |
| :--- | :--- | :--- |
| `CancelAsync_WhenShipped_ThrowsAndOthersStillCancel` | cs-modern-001 | Shipped 취소는 `InvalidOperationException`, 상태·환불 변경 없음. Pending·Paid는 그대로 취소됨 |
| `CalculateRefundAsync_RoundsToNearestTen` | cs-modern-002 | 환불액 10원 단위 반올림(…7 → 올림, …3 → 내림). 중간값 5원은 쓰지 않음 |
