---
id: t3
status: draft
scope: [Modern]
---
# 주문 취소가 실제로 요청한 환불액을 돌려주게 해줘. 환불이 없으면 0을 돌려주면 돼

## 수용 기준
- AC1: CancelAsync가 요청한 환불액을 돌려준다(결제 완료 주문은 환불액, 결제 전 주문은 0)
  test: CancelRefundTests.CancelAsync_ReturnsRequestedRefund

## 작업
- [ ] T1 cs Modern/Shop.Api/Services/IOrderService.cs#CancelAsync 반환 형식을 Task<decimal>로 바꾼다 [G1]
- [ ] T2 cs Modern/Shop.Api/Services/OrderService.cs#CancelAsync 반환 형식을 Task<decimal>로 바꾸고 요청한 환불액(환불이 없거나 이미 취소된 주문이면 0)을 반환한다 (AC1) [G1]
