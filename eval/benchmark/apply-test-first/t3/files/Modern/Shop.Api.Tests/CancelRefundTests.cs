using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shop.Api.Data;
using Shop.Api.Models;
using Shop.Api.Services;
using Xunit;

namespace Shop.Api.Tests
{
    /// <summary>
    /// apply 테스트 우선 사례 t3의 수용 기준 테스트. 바뀐 시그니처(Task&lt;decimal&gt;)로 썼으므로 원본에서는 이 파일이 컴파일되지 않는다
    /// (그룹 G1의 컴파일 RED). 그룹이 끝나면 테스트 프로젝트 전체가 다시 빌드되고 다른 테스트가 새로 실패하면 안 된다(엄격 회귀).
    /// </summary>
    public class CancelRefundTests
    {
        [Fact]
        public async Task CancelAsync_ReturnsRequestedRefund()
        {
            foreach (var (status, expected) in new[] { (OrderStatus.Paid, 28000m), (OrderStatus.Pending, 0m) })
            {
                var repo = new FakeOrderRepository(NewOrder(status));
                var service = new OrderService(repo, NullLogger<OrderService>.Instance);

                decimal refund = await service.CancelAsync(1, "단순 변심");

                Assert.Equal(expected, refund);
                Assert.Equal(expected, repo.RefundRequested);
                Assert.Equal(OrderStatus.Cancelled, repo.Order.Status);
            }
        }

        private static Order NewOrder(OrderStatus status) => new Order
        {
            OrderId = 1,
            CustomerId = 100,
            Status = status,
            TotalAmount = 28000m,
            ShippingFee = 3000m,
            OrderedAt = new DateTime(2026, 10, 1, 10, 0, 0),
            PaidAt = status == OrderStatus.Pending ? (DateTime?)null : new DateTime(2026, 10, 1, 10, 5, 0),
            Items = new List<OrderItem>
            {
                new OrderItem { OrderItemId = 1, OrderId = 1, ProductId = 10, ProductName = "머그컵", Quantity = 2, UnitPrice = 10000m },
                new OrderItem { OrderItemId = 2, OrderId = 1, ProductId = 11, ProductName = "컵받침", Quantity = 1, UnitPrice = 5000m },
            }
        };

        private sealed class FakeOrderRepository : IOrderRepository
        {
            public FakeOrderRepository(Order order) => Order = order;
            public Order Order { get; }
            public decimal RefundRequested { get; private set; }

            public Task<Order> GetByIdAsync(int orderId) => Task.FromResult(orderId == Order.OrderId ? Order : null);
            public Task<IReadOnlyList<OrderItem>> GetItemsAsync(int orderId) => Task.FromResult<IReadOnlyList<OrderItem>>(Order.Items.ToList());
            public Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request) => Task.FromResult<IReadOnlyList<Order>>(new[] { Order });
            public Task UpdateStatusAsync(int orderId, OrderStatus status, string reason) { Order.Status = status; Order.CancelReason = reason; return Task.CompletedTask; }
            public Task AddHistoryAsync(int orderId, string message) => Task.CompletedTask;
            public Task AddRefundAsync(int orderId, decimal amount) { RefundRequested = amount; return Task.CompletedTask; }
        }
    }
}
