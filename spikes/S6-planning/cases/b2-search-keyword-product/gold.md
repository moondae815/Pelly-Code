---
id: b2-search-keyword-product
category: B
scope: [Legacy, Sql]
status: approved
request: "주문 검색 키워드로 고객 이름뿐 아니라 상품명도 찾게 해줘. 레거시 DAO 검색과 DB 검색 프로시저 둘 다. 프로시저에는 키워드 매개변수가 아직 없어"
---
- [ ] T1 sql Sql/objects/usp_SearchOrders.sql @Keyword nvarchar(50) = NULL 매개변수를 추가하고 고객 이름 또는 상품명(OrderItems.ProductName) LIKE 검색을 한다
- [ ] T2 cs Legacy/Shop.Data/OrderDao.cs#SearchOrders 키워드 조건에 주문 상품명(OrderItems.ProductName) EXISTS 검색을 더한다
- [ ] T3 sql Sql/objects/usp_SearchOrdersDynamic.sql 같은 키워드 검색을 추가한다
