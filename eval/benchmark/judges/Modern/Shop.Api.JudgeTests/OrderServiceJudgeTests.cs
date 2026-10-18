using System;
using System.Threading.Tasks;
using Shop.Api.Models;
using Shop.Api.Services;
using Xunit;

namespace Shop.Api.JudgeTests
{
    /// <summary>
    /// 벤치마크 작업의 동작 판정(OrderService). 테스트마다 원본 코퍼스에서는 실패하고, 올바르게 수정한 뒤에만 통과해야 한다
    /// (JudgeTestDefinitionTests가 원본에서 실패하는지 확인한다). 이름은 「작업 id 밑줄 표기_기대 동작」이다.
    /// </summary>
    public class OrderServiceJudgeTests
    {
        // cs-modern-001: 배송 중(Shipped) 주문 취소는 InvalidOperationException. 다른 상태까지 막는 과잉 수정도 실패로 본다.
        [Fact]
        public async Task cs_modern_001_ShippedThrowsAndOthersStillCancel()
        {
            foreach (var status in new[] { OrderStatus.Pending, OrderStatus.Paid })
            {
                var repo = new FakeOrderRepository(JudgeOrders.New(status));
                await new OrderService(repo, new FakeLogger<OrderService>()).CancelAsync(1, "단순 변심");
                Assert.Equal(OrderStatus.Cancelled, repo.Order.Status);
                Assert.Equal(status == OrderStatus.Paid ? 28000m : 0m, repo.RefundRequested);
            }

            var shipped = new FakeOrderRepository(JudgeOrders.New(OrderStatus.Shipped));
            var service = new OrderService(shipped, new FakeLogger<OrderService>());

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(1, "단순 변심"));
            Assert.Equal(OrderStatus.Shipped, shipped.Order.Status);
            Assert.Equal(0, shipped.StatusUpdates);
            Assert.Equal(0m, shipped.RefundRequested);
        }

        // cs-modern-002: 환불액은 10원 미만을 버리지 않고 10원 단위로 반올림한다. 중간값(5원)은 피해 반올림 방식에 기대지 않는다.
        // 한 [Fact]에 모아 둔다: 내려야 맞는 경우(10003)는 원본에서도 통과하므로 따로 두면 원본에서 실패하지 않는다.
        [Fact]
        public async Task cs_modern_002_RefundRoundsToNearestTen()
        {
            var cases = new (decimal UnitPrice, decimal Expected)[] { (10007m, 10010m), (10003m, 10000m), (12346m, 12350m), (12344m, 12340m), (12340m, 12340m) };
            foreach (var (unitPrice, expected) in cases)
            {
                var repo = new FakeOrderRepository(JudgeOrders.PaidSingleItem(unitPrice));
                var service = new OrderService(repo, new FakeLogger<OrderService>());

                var refund = await service.CalculateRefundAsync(1);

                Assert.True(expected == refund, $"단가 {unitPrice}: 기대 {expected}, 실제 {refund}");
            }
        }

        // cs-modern-007: 앞뒤 공백을 뺀 키워드가 2자 미만이면 Keyword는 null. 2자 이상은 그대로(공백만 뺌) 검색한다.
        [Fact]
        public async Task cs_modern_007_ShortKeywordBecomesNull()
        {
            var cases = new (string Keyword, string Expected)[]
            {
                (" a ", null), ("a", null), ("   ", null), (null, null), ("머그", "머그"), (" 머그 ", "머그"), ("ab", "ab"),
            };
            foreach (var (keyword, expected) in cases)
            {
                var repo = new FakeOrderRepository(JudgeOrders.New(OrderStatus.Paid));
                var service = new OrderService(repo, new FakeLogger<OrderService>());

                await service.SearchAsync(new OrderSearchRequest { CustomerId = 100, Keyword = keyword });

                var sent = Assert.Single(repo.Searches);
                Assert.True(expected == sent.Keyword, $"키워드 '{keyword}': 기대 {expected ?? "null"}, 실제 {sent.Keyword ?? "null"}");
            }
        }

        // cs-modern-010: 사유가 200자를 넘으면 잘라 저장하지 않고 ArgumentException(상태 변경 없음). 정확히 200자는 그대로 취소된다.
        [Fact]
        public async Task cs_modern_010_ReasonOver200ThrowsWithoutTruncating()
        {
            var tooLong = new FakeOrderRepository(JudgeOrders.New(OrderStatus.Paid));
            var service = new OrderService(tooLong, new FakeLogger<OrderService>());

            await Assert.ThrowsAnyAsync<ArgumentException>(() => service.CancelAsync(1, new string('가', 201)));
            Assert.Equal(0, tooLong.StatusUpdates);
            Assert.Equal(OrderStatus.Paid, tooLong.Order.Status);

            var exact = new FakeOrderRepository(JudgeOrders.New(OrderStatus.Paid));
            await new OrderService(exact, new FakeLogger<OrderService>()).CancelAsync(1, new string('나', 200));
            Assert.Equal(OrderStatus.Cancelled, exact.Order.Status);
            Assert.Equal(new string('나', 200), exact.Order.CancelReason);
        }

        // cs-modern-012: 조건(고객 번호·상태·기간·키워드)이 하나도 없으면 ArgumentException이고 저장소를 부르지 않는다.
        // 조건이 하나라도 있으면 그대로 검색한다(과잉 차단 검사).
        [Fact]
        public async Task cs_modern_012_SearchWithoutAnyConditionThrows()
        {
            var empty = new FakeOrderRepository(JudgeOrders.New(OrderStatus.Paid));
            await Assert.ThrowsAnyAsync<ArgumentException>(
                () => new OrderService(empty, new FakeLogger<OrderService>()).SearchAsync(new OrderSearchRequest()));
            Assert.Empty(empty.Searches);

            var single = new[]
            {
                new OrderSearchRequest { CustomerId = 100 },
                new OrderSearchRequest { Status = OrderStatus.Paid },
                new OrderSearchRequest { From = new DateTime(2026, 10, 1) },
                new OrderSearchRequest { To = new DateTime(2026, 10, 1) },
                new OrderSearchRequest { Keyword = "머그" },
            };
            foreach (var request in single)
            {
                var repo = new FakeOrderRepository(JudgeOrders.New(OrderStatus.Paid));
                var orders = await new OrderService(repo, new FakeLogger<OrderService>()).SearchAsync(request);
                Assert.Single(repo.Searches);
                Assert.Single(orders);
            }
        }
    }
}
