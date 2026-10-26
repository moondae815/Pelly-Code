---
id: t4
status: draft
scope: [Modern]
---
# 배송 완료된 주문을 취소하려 할 때 나는 예외 메시지 끝에 주문 번호를 붙여줘

## 수용 기준
- AC1: 배송 완료 주문 취소는 계속 InvalidOperationException으로 거절되고, 메시지에 주문 번호가 들어간다
  test: DeliveredCancelTests.CancelAsync_WhenDelivered_Throws

## 작업
- [ ] T1 cs Modern/Shop.Api/Services/OrderService.cs#CancelAsync 배송 완료(Delivered) 주문의 InvalidOperationException 메시지 끝에 " OrderId={orderId}"를 붙인다 (AC1)
