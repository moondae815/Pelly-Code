---
id: c2-controller-reject-bad-id
category: C
scope: [Legacy]
status: draft
request: "레거시 주문 컨트롤러에서 주문 번호를 받는 액션은 번호가 0 이하이면 서비스를 부르지 말고 400(BadRequest)을 돌려줘"
---
- [ ] T1 cs Legacy/Shop.Web/Controllers/OrderController.cs#Detail id <= 0이면 HttpStatusCodeResult(HttpStatusCode.BadRequest)를 반환한다
- [ ] T2 cs Legacy/Shop.Web/Controllers/OrderController.cs#Cancel id <= 0이면 HttpStatusCodeResult(HttpStatusCode.BadRequest)를 반환한다
