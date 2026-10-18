using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Shop.Api.Controllers;
using Shop.Api.Models;
using Xunit;

namespace Shop.Api.JudgeTests
{
    /// <summary>벤치마크 작업의 동작 판정(OrdersController). 규칙은 <see cref="OrderServiceJudgeTests"/>와 같다.</summary>
    public class OrdersControllerJudgeTests
    {
        private static int? StatusOf(IActionResult result) => (result as IStatusCodeActionResult)?.StatusCode;

        // cs-modern-005: id가 0 이하이면 검색하지 않고 BadRequest("주문 번호가 올바르지 않습니다."). 양수 id는 그대로 조회된다.
        [Fact]
        public async Task cs_modern_005_NonPositiveIdIsBadRequest()
        {
            foreach (var id in new[] { 0, -1 })
            {
                var service = new FakeOrderService();
                var result = await new OrdersController(service).Get(id);

                var bad = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Equal("주문 번호가 올바르지 않습니다.", bad.Value);
                Assert.Empty(service.Searches);
            }

            var found = new FakeOrderService();
            var ok = await new OrdersController(found).Get(1);
            Assert.Equal(200, StatusOf(ok));
        }

        // cs-modern-006: pageSize가 100을 넘으면 검색하지 않고 400. 100 이하는 그대로 검색한다(경계값).
        [Fact]
        public async Task cs_modern_006_PageSizeOver100IsBadRequest()
        {
            var tooBig = new FakeOrderService();
            var rejected = await new OrdersController(tooBig).Search(null, null, null, null, null, 1, 101);
            Assert.Equal(400, StatusOf(rejected.Result));
            Assert.Empty(tooBig.Searches);

            foreach (var pageSize in new[] { 100, 20 })
            {
                var service = new FakeOrderService();
                var accepted = await new OrdersController(service).Search(null, null, null, null, null, 1, pageSize);
                Assert.Equal(200, StatusOf(accepted.Result));
                Assert.Single(service.Searches);
            }
        }

        // cs-modern-009: 서비스의 ArgumentException(파생 포함)은 BadRequest(예외 메시지). 다른 결과(204·404·409·사유 없음 400)는 그대로다.
        [Fact]
        public async Task cs_modern_009_ArgumentExceptionIsBadRequestWithMessage()
        {
            foreach (var thrown in new ArgumentException[]
                     {
                         new ArgumentOutOfRangeException("orderId", "주문 번호가 올바르지 않습니다."),
                         new ArgumentException("취소 사유를 입력해야 합니다.", "reason"),
                     })
            {
                var service = new FakeOrderService { CancelThrows = thrown };
                var result = await new OrdersController(service).Cancel(0, new CancelRequest { Reason = "단순 변심" });

                var bad = Assert.IsType<BadRequestObjectResult>(result);
                Assert.Equal(thrown.Message, bad.Value);
            }

            var others = new (Exception Thrown, int Status)[]
            {
                (null, 204), (new KeyNotFoundException("없음"), 404), (new InvalidOperationException("배송 완료"), 409),
            };
            foreach (var (thrown, status) in others)
            {
                var service = new FakeOrderService { CancelThrows = thrown };
                var result = await new OrdersController(service).Cancel(1, new CancelRequest { Reason = "단순 변심" });
                Assert.Equal(status, StatusOf(result));
            }

            var noReason = new FakeOrderService();
            var missing = await new OrdersController(noReason).Cancel(1, new CancelRequest { Reason = " " });
            Assert.Equal(400, StatusOf(missing));
            Assert.Empty(noReason.Cancels);
        }
    }
}
