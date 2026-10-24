---
id: a2-update-status-returns-rows
category: A
scope: [Modern]
status: approved
request: "주문 상태 변경이 실제로 바뀐 행 수를 돌려주게 하고, 주문을 취소할 때 바뀐 행이 없으면 InvalidOperationException을 던지게 해줘"
---
- [ ] T1 cs Modern/Shop.Api/Data/IOrderRepository.cs#UpdateStatusAsync 반환 형식을 Task<int>로 바꾼다 [G1]
- [ ] T2 cs Modern/Shop.Api/Data/OrderRepository.cs#UpdateStatusAsync ExecuteAsync가 돌려준 행 수를 반환한다 [G1]
- [ ] T3 cs Modern/Shop.Api.Tests/OrderServiceTests.cs#UpdateStatusAsync 테스트용 가짜 저장소도 Task<int>를 반환한다(1) [G1]
- [ ] T4 cs Modern/Shop.Api/Services/OrderService.cs#CancelAsync UpdateStatusAsync 결과가 0이면 InvalidOperationException을 던진다 [G1]
