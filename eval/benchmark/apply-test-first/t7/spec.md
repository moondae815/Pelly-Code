---
id: t7
status: draft
scope: [Modern]
---
# 주문 검색 기간을 최대 92일로 제한해줘. 넘으면 ArgumentException을 던지면 돼

## 수용 기준
- AC1: 검색 기간(From~To)이 92일을 넘으면 저장소를 조회하지 않고 ArgumentException이 나며, 92일까지는 그대로 검색한다
  test: OrderSearchRangeTests.SearchAsync_RangeOver92Days_Throws

## 작업
- [ ] T1 cs Modern/Shop.Api/Services/OrderService.cs#SearchAsync 기간을 채운 뒤 From~To가 92일을 넘으면 ArgumentException("검색 기간은 최대 92일입니다.")을 던진다 (AC1)
