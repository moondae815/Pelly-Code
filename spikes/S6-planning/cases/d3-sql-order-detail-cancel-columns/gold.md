---
id: d3-sql-order-detail-cancel-columns
category: D
scope: [Sql]
status: approved
request: "주문 상세 프로시저의 주문 결과에 취소 시각과 취소 사유 열을 추가해줘"
---
- [ ] T1 sql Sql/objects/usp_GetOrderDetail.sql 첫 번째 결과 집합에 o.CancelledAt, o.CancelReason 열을 추가한다
