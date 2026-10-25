---
id: d2-legacy-pending-history
status: draft
scope: [Legacy]
---
# 결제 전(Pending) 주문을 취소한 경우에는 "결제 전 주문 취소" 이력을 하나 더 남기게 해줘

## 작업
- [ ] T1 cs Legacy/Shop.Web/Services/OrderService.cs#Cancel 이전 상태가 Pending이면 AddHistory로 "결제 전 주문 취소" 이력을 하나 더 남긴다
