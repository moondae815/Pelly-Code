---
id: c1-repository-command-timeout
category: C
scope: [Modern]
status: approved
request: "API 주문 저장소의 모든 DB 호출에 명령 시간 제한 30초를 걸어줘"
---
- [ ] T1 cs Modern/Shop.Api/Data/OrderRepository.cs#GetByIdAsync Dapper 호출에 commandTimeout: 30을 넘긴다
- [ ] T2 cs Modern/Shop.Api/Data/OrderRepository.cs#GetItemsAsync Dapper 호출에 commandTimeout: 30을 넘긴다
- [ ] T3 cs Modern/Shop.Api/Data/OrderRepository.cs#SearchAsync Dapper 호출에 commandTimeout: 30을 넘긴다
- [ ] T4 cs Modern/Shop.Api/Data/OrderRepository.cs#UpdateStatusAsync Dapper 호출에 commandTimeout: 30을 넘긴다
- [ ] T5 cs Modern/Shop.Api/Data/OrderRepository.cs#AddHistoryAsync Dapper 호출에 commandTimeout: 30을 넘긴다
- [ ] T6 cs Modern/Shop.Api/Data/OrderRepository.cs#AddRefundAsync Dapper 호출에 commandTimeout: 30을 넘긴다
