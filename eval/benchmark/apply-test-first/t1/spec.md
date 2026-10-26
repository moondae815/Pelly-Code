---
id: t1
status: draft
scope: [Modern]
---
# 취소 사유가 "test"인 주문 취소는 막고, 취소 API는 그 요청을 400으로 돌려줘

## 수용 기준
- AC1: 취소 사유가 "test"면 주문을 취소하지 않고 취소 API가 400(BadRequest, 서비스의 예외 메시지)으로 응답한다
  test: CancelReasonTests.Cancel_BlockedReason_IsBadRequest

## 작업
- [ ] T1 cs Modern/Shop.Api/Services/OrderService.cs#CancelAsync 앞뒤 공백을 뺀 취소 사유가 "test"(대소문자 무시)면 ArgumentException("허용되지 않는 취소 사유입니다.")을 던진다 (AC1)
- [ ] T2 cs Modern/Shop.Api/Controllers/OrdersController.cs#Cancel CancelAsync가 던진 ArgumentException을 잡아 BadRequest(예외 메시지)로 응답한다 (AC1)
