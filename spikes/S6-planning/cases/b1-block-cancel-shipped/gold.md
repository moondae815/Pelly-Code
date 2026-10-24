---
id: b1-block-cancel-shipped
category: B
scope: [Legacy, Sql]
status: approved
request: "배송 중인 주문도 취소할 수 없게 해줘. 레거시 웹의 취소 처리와 DB 취소 프로시저 둘 다 막아야 해"
---
- [ ] T1 sql Sql/objects/usp_CancelOrder.sql 배송 중(Status = 2)인 주문이면 RAISERROR로 오류를 내고 끝낸다
- [ ] T2 cs Legacy/Shop.Web/Services/OrderService.cs#Cancel 배송 중(Shipped) 주문이면 InvalidOperationException을 던진다
- [ ] T3 cs Legacy/Shop.Tests/OrderServiceTests.cs#Cancel_WhenShipped_CurrentlyCancels 배송 중 주문 취소가 예외를 던지는지 확인하도록 바꾼다
