---
id: t5
status: draft
scope: [Modern]
---
# 주문 API의 잘못된 요청은 400으로 돌려줘. 200자를 넘는 취소 사유와 0 이하의 주문 번호가 대상이야

## 수용 기준
- AC1: 200자를 넘는 취소 사유는 잘리지 않고, 취소 API가 400(BadRequest)으로 거절한다
  test: OrderRequestValidationTests.Cancel_ReasonOver200_IsBadRequest
- AC2: 0 이하의 주문 번호는 취소와 조회 모두 400(BadRequest)이다
  test: OrderRequestValidationTests.NonPositiveOrderId_IsBadRequest

## 작업
- [ ] T1 cs Modern/Shop.Api/Services/OrderService.cs#CancelAsync 앞뒤 공백을 뺀 취소 사유가 MaxReasonLength(200자)를 넘으면 잘라내지 말고 ArgumentException("취소 사유는 200자를 넘을 수 없습니다.")을 던진다 (AC1)
- [ ] T2 cs Modern/Shop.Api/Controllers/OrdersController.cs#Cancel CancelAsync가 던진 ArgumentException(ArgumentOutOfRangeException 포함)을 잡아 BadRequest(예외 메시지)로 응답한다 (AC1, AC2)
- [ ] T3 cs Modern/Shop.Api/Controllers/OrdersController.cs#Get id가 0 이하이면 조회하지 않고 BadRequest("주문 번호가 올바르지 않습니다.")를 반환한다 (AC2)
