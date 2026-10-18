using System;
using System.Threading.Tasks;
using Shop.Api.Models;
using Shop.Api.Services;
using Xunit;

namespace Shop.Api.JudgeTests
{
    /// <summary>
    /// 벤치마크 작업의 동작 판정. 테스트마다 원본 코퍼스에서는 실패하고, 올바르게 수정한 뒤에만 통과해야 한다
    /// (JudgeTestDefinitionTests가 원본에서 실패하는지 확인한다).
    /// </summary>
    public class OrderServiceJudgeTests
    {
        // cs-modern-001: 배송 중(Shipped) 주문 취소는 InvalidOperationException. 다른 상태까지 막는 과잉 수정도 실패로 본다.
        [Fact]
        public async Task CancelAsync_WhenShipped_ThrowsAndOthersStillCancel()
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
        public async Task CalculateRefundAsync_RoundsToNearestTen()
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
    }
}
