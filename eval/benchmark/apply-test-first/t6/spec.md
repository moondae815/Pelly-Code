---
id: t6
status: draft
scope: [Modern, Sql]
---
# 이미 취소된 주문을 다시 취소하면 조용히 넘기지 말고 오류를 내줘. API 서비스와 DB 취소 프로시저 둘 다. 그리고 주문 상세 프로시저에 취소 시각과 취소 사유 열을 더해줘

## 수용 기준
- AC1: 이미 취소된 주문을 다시 취소하면 오류가 나고 이력·환불이 남지 않는다
  test: RecancelTests.CancelAsync_WhenAlreadyCancelled_Throws
- AC2: 주문 상세의 주문 결과에 취소 시각과 취소 사유가 나온다
  test: RecancelTests.GetOrderDetail_ReturnsCancelColumns

## 작업
- [ ] T1 sql Sql/objects/usp_CancelOrder.sql 이미 취소된 주문(@Status = 9)이면 RETURN 대신 RAISERROR(N'이미 취소된 주문입니다.', 16, 1)로 오류를 내고 끝낸다 (AC1)
- [ ] T2 cs Modern/Shop.Api/Services/OrderService.cs#CancelAsync 이미 취소된 주문이면 로그만 남기고 끝내지 말고 InvalidOperationException("이미 취소된 주문입니다.")을 던진다 (AC1)
- [ ] T3 sql Sql/objects/usp_GetOrderDetail.sql 첫 번째 결과 집합에 o.CancelledAt, o.CancelReason 열을 추가한다 (AC2)
