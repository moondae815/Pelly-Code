---
id: t8
status: draft
scope: [Legacy]
---
# 결제 전(Pending) 주문을 취소한 경우에는 "결제 전 주문 취소" 이력을 하나 더 남기게 해줘

## 수용 기준
- AC1: 결제 전 주문을 취소하면 "결제 전 주문 취소" 이력이 남고, 결제된 주문에는 남지 않는다
  test: PendingCancelTests.Cancel_WhenPending_AddsPendingHistory

## 작업
- [ ] T1 cs Legacy/Shop.Web/Services/OrderService.cs#Cancel 이전 상태가 결제 전이면 AddHistory로 "결제 전 주문 취소" 이력을 하나 더 남긴다 (AC1)
