---
id: c3-dao-guard-write-methods
status: draft
scope: [Legacy]
---
# 레거시 주문 DAO에서 데이터를 바꾸는 메서드는 주문 번호가 0 이하이면 ArgumentOutOfRangeException을 던지게 해줘

## 작업
- [ ] T1 cs Legacy/Shop.Data/OrderDao.cs#UpdateStatus orderId <= 0이면 ArgumentOutOfRangeException("orderId")를 던진다
- [ ] T2 cs Legacy/Shop.Data/OrderDao.cs#AddHistory orderId <= 0이면 ArgumentOutOfRangeException("orderId")를 던진다
