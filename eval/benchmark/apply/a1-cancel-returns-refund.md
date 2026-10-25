---
id: a1-cancel-returns-refund
status: draft
scope: [Modern]
---
# 주문 취소가 실제로 환불한 금액을 돌려주게 바꾸고, 취소 API도 204 대신 그 환불액을 담아 200으로 응답하게 해줘

## 작업
- [ ] T1 cs Modern/Shop.Api/Services/IOrderService.cs#CancelAsync 반환 형식을 Task<decimal>로 바꾼다 [G1]
- [ ] T2 cs Modern/Shop.Api/Services/OrderService.cs#CancelAsync 반환 형식을 Task<decimal>로 바꾸고 환불액(없으면 0)을 반환한다 [G1]
- [ ] T3 cs Modern/Shop.Api/Controllers/OrdersController.cs#Cancel CancelAsync가 돌려준 환불액을 Ok(refund)로 응답한다 [G1]
