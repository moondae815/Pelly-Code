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
    public class OrderServiceTests
    {
        // 벤치마크 cs-modern-001의 "수정 전" 상태: 배송 중(Shipped) 주문도 현재는 취소된다.
        [Fact]
        public async Task CancelAsync_WhenShipped_CurrentlyCancels()
        {
            var repo = new FakeOrderRepository(NewOrder(OrderStatus.Shipped));
            var service = new OrderService(repo, NullLogger<OrderService>.Instance);

            await service.CancelAsync(1, "단순 변심");

            Assert.Equal(OrderStatus.Cancelled, repo.Order.Status);
        }

        [Fact]
        public async Task CalculateRefundAsync_PaidOrder_RefundsItemsAndShippingFee()
        {
            var repo = new FakeOrderRepository(NewOrder(OrderStatus.Paid));
            var service = new OrderService(repo, NullLogger<OrderService>.Instance);

            var refund = await service.CalculateRefundAsync(1);

            // 상품 2 x 10,000 + 1 x 5,000 = 25,000, 배송비 3,000 환불
            Assert.Equal(28000m, refund);
        }

        [Fact]
        public async Task CalculateRefundAsync_ShippedOrder_DeductsReturnShippingFee()
        {
            var repo = new FakeOrderRepository(NewOrder(OrderStatus.Shipped));
            var service = new OrderService(repo, NullLogger<OrderService>.Instance);

            var refund = await service.CalculateRefundAsync(1);

            // 배송 시작 후에는 배송비 미환불 + 반품 배송비 3,000 차감
            Assert.Equal(22000m, refund);
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
            ShippedAt = status == OrderStatus.Shipped ? new DateTime(2026, 10, 2, 9, 0, 0) : (DateTime?)null,
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
            public List<string> History { get; } = new List<string>();
            public decimal RefundRequested { get; private set; }

            public Task<Order> GetByIdAsync(int orderId) => Task.FromResult(orderId == Order.OrderId ? Order : null);
            public Task<IReadOnlyList<OrderItem>> GetItemsAsync(int orderId) => Task.FromResult<IReadOnlyList<OrderItem>>(Order.Items.ToList());
            public Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request) => Task.FromResult<IReadOnlyList<Order>>(new[] { Order });
            public Task UpdateStatusAsync(int orderId, OrderStatus status, string reason) { Order.Status = status; Order.CancelReason = reason; return Task.CompletedTask; }
            public Task AddHistoryAsync(int orderId, string message) { History.Add(message); return Task.CompletedTask; }
            public Task AddRefundAsync(int orderId, decimal amount) { RefundRequested = amount; return Task.CompletedTask; }
        }
    }
}
