---
id: t2
status: draft
scope: [Modern]
---
# 배송 중(Shipped)인 주문을 취소하려고 하면 InvalidOperationException을 던지도록 수정해줘

## 수용 기준
- AC1: 배송 중인 주문은 취소되지 않고 InvalidOperationException이 나며, 상태·이력·환불은 그대로다
  test: ShippedCancelTests.CancelAsync_WhenShipped_Throws
  breaks: OrderServiceTests.CancelAsync_WhenShipped_CurrentlyCancels

## 작업
- [ ] T1 cs Modern/Shop.Api/Services/OrderService.cs#CancelAsync 배송 중(Shipped) 주문이면 상태를 바꾸기 전에 InvalidOperationException("배송 중인 주문은 취소할 수 없습니다.")을 던진다 (AC1)
