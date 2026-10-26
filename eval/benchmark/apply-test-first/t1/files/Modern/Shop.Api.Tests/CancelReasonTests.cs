using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using Shop.Api.Controllers;
using Shop.Api.Data;
using Shop.Api.Models;
using Shop.Api.Services;
using Xunit;

namespace Shop.Api.Tests
{
    /// <summary>
    /// apply 테스트 우선 사례 t1의 수용 기준 테스트. 서비스(금지 사유 예외)와 컨트롤러(400 응답) 두 작업이 모두 끝나야 통과한다.
    /// </summary>
    public class CancelReasonTests
    {
        [Fact]
        public async Task Cancel_BlockedReason_IsBadRequest()
        {
            var blocked = new FakeOrderRepository(NewOrder(OrderStatus.Paid));
            var controller = new OrdersController(new OrderService(blocked, NullLogger<OrderService>.Instance));

            var result = await controller.Cancel(1, new CancelRequest { Reason = " Test " });

            var bad = Assert.IsType<BadRequestObjectResult>(result);
            // ArgumentException(message, paramName)의 Message에는 "(Parameter 'reason')"이 붙으므로 앞부분만 본다.
            Assert.StartsWith("허용되지 않는 취소 사유입니다.", Assert.IsType<string>(bad.Value));
            Assert.Equal(OrderStatus.Paid, blocked.Order.Status);
            Assert.Empty(blocked.History);

            // 다른 사유는 그대로 취소된다(과잉 차단 방지).
            var normal = new FakeOrderRepository(NewOrder(OrderStatus.Paid));
            var ok = await new OrdersController(new OrderService(normal, NullLogger<OrderService>.Instance))
                .Cancel(1, new CancelRequest { Reason = "단순 변심" });
            Assert.IsType<NoContentResult>(ok);
            Assert.Equal(OrderStatus.Cancelled, normal.Order.Status);
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

            public Task<Order> GetByIdAsync(int orderId) => Task.FromResult(orderId == Order.OrderId ? Order : null);
            public Task<IReadOnlyList<OrderItem>> GetItemsAsync(int orderId) => Task.FromResult<IReadOnlyList<OrderItem>>(Order.Items.ToList());
            public Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request) => Task.FromResult<IReadOnlyList<Order>>(new[] { Order });
            public Task UpdateStatusAsync(int orderId, OrderStatus status, string reason) { Order.Status = status; Order.CancelReason = reason; return Task.CompletedTask; }
            public Task AddHistoryAsync(int orderId, string message) { History.Add(message); return Task.CompletedTask; }
            public Task AddRefundAsync(int orderId, decimal amount) => Task.CompletedTask;
        }
    }
}
