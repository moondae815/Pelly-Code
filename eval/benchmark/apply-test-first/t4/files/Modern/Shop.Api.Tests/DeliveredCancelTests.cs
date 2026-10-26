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
    /// apply 테스트 우선 사례 t4의 수용 기준 테스트. 거절 자체만 확인하므로 원본에서도 이미 통과한다: 메시지 변경을 검증하지 못하는
    /// 테스트를 고른 경우다(RED 확인 경고, 벤치마크는 --yes라 기록하고 계속한다).
    /// </summary>
    public class DeliveredCancelTests
    {
        [Fact]
        public async Task CancelAsync_WhenDelivered_Throws()
        {
            var repo = new FakeOrderRepository(NewOrder(OrderStatus.Delivered));
            var service = new OrderService(repo, NullLogger<OrderService>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(1, "단순 변심"));

            Assert.Equal(OrderStatus.Delivered, repo.Order.Status);
            Assert.Empty(repo.History);
        }

        private static Order NewOrder(OrderStatus status) => new Order
        {
            OrderId = 1,
            CustomerId = 100,
            Status = status,
            TotalAmount = 28000m,
            ShippingFee = 3000m,
            OrderedAt = new DateTime(2026, 10, 1, 10, 0, 0),
            PaidAt = new DateTime(2026, 10, 1, 10, 5, 0),
            ShippedAt = new DateTime(2026, 10, 2, 9, 0, 0),
            DeliveredAt = new DateTime(2026, 10, 3, 15, 0, 0),
            Items = new List<OrderItem>
            {
                new OrderItem { OrderItemId = 1, OrderId = 1, ProductId = 10, ProductName = "머그컵", Quantity = 2, UnitPrice = 10000m },
            }
        };

        private sealed class FakeOrderRepository : IOrderRepository
        {
            public FakeOrderRepository(Order order) => Order = order;
            public Order Order { get; }
            public List<string> History { get; } = new List<string>();

            public Task<Order> GetByIdAsync(int orderId) => Task.FromResult(orderId == Order.OrderId ? Order : null);
            public Task<IReadOnlyList<OrderItem>> GetItemsAsync(int orderId) => Task.FromResult<IReadOnlyList<OrderItem>>(Order.Items.ToList());
            public Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request) => Task.FromResult<IReadOnlyList<Order>>(new[] { Order });
            public Task UpdateStatusAsync(int orderId, OrderStatus status, string reason) { Order.Status = status; Order.CancelReason = reason; return Task.CompletedTask; }
            public Task AddHistoryAsync(int orderId, string message) { History.Add(message); return Task.CompletedTask; }
            public Task AddRefundAsync(int orderId, decimal amount) => Task.CompletedTask;
        }
    }
}
