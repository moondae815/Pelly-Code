---
id: b3-search-exclude-cancelled
status: draft
scope: [Modern, Sql]
---
# 주문을 검색할 때 상태를 지정하지 않으면 취소된 주문은 결과에서 빼줘. API의 저장소 검색과 DB 검색 프로시저 둘 다

## 작업
- [ ] T1 sql Sql/objects/usp_SearchOrders.sql @Status가 NULL이면 Status = 9 주문을 제외한다
- [ ] T2 cs Modern/Shop.Api/Data/OrderRepository.cs#SearchAsync Status를 지정하지 않으면 AND o.Status <> 9 조건을 붙인다
- [ ] T3 sql Sql/objects/usp_SearchOrdersDynamic.sql 같은 제외 조건을 추가한다
