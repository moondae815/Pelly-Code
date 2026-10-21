---
id: a3-rename-getorder
category: A
scope: [Legacy]
status: draft
request: "레거시 주문 DAO의 주문 한 건 조회 메서드 이름을 FindOrder로 바꿔줘. 서비스 쪽 메서드 이름은 그대로 둬"
---
- [ ] T1 cs Legacy/Shop.Data/IOrderDao.cs#GetOrder 메서드 이름을 FindOrder로 바꾼다 [G1]
- [ ] T2 cs Legacy/Shop.Data/OrderDao.cs#GetOrder 메서드 이름을 FindOrder로 바꾼다 [G1]
- [ ] T3 cs Legacy/Shop.Tests/OrderServiceTests.cs#GetOrder 테스트용 가짜 DAO의 메서드 이름을 FindOrder로 바꾼다 [G1]
- [ ] T4 cs Legacy/Shop.Web/Services/OrderService.cs#GetOrder _orderDao.GetOrder 호출을 FindOrder로 바꾼다 [G1]
- [ ] T5 cs Legacy/Shop.Web/Services/OrderService.cs#Cancel _orderDao.GetOrder 호출을 FindOrder로 바꾼다 [G1]
