---
id: d1-modern-cancel-shipped
category: D
scope: [Modern]
status: draft
request: "배송 중(Shipped)인 주문을 취소하려고 하면 InvalidOperationException을 던지도록 수정해줘"
---
- [ ] T1 cs Modern/Shop.Api/Services/OrderService.cs#CancelAsync 배송 중(Shipped) 주문이면 InvalidOperationException을 던진다
- [ ] T2 cs Modern/Shop.Api.Tests/OrderServiceTests.cs#CancelAsync_WhenShipped_CurrentlyCancels 배송 중 취소가 예외를 던지는지 확인하도록 바꾼다
