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
    /// apply 테스트 우선 사례 t5의 수용 기준 테스트 둘. 컨트롤러의 취소 작업(T2)이 AC1의 마지막 연결 작업이자 AC2의 첫 연결 작업이다:
    /// AC1은 서비스(T1)와 T2가 끝나야, AC2는 T2와 조회(T3)가 끝나야 통과한다.
    /// </summary>
    public class OrderRequestValidationTests
    {
        [Fact]
        public async Task Cancel_ReasonOver200_IsBadRequest()
        {
            var repo = new FakeOrderRepository(NewOrder(OrderStatus.Paid));
            var controller = new OrdersController(new OrderService(repo, NullLogger<OrderService>.Instance));

            var result = await controller.Cancel(1, new CancelRequest { Reason = new string('가', 201) });

            Assert.IsType<BadRequestObjectResult>(result);
            Assert.Equal(OrderStatus.Paid, repo.Order.Status);
            Assert.Empty(repo.History);

            // 200자는 그대로 취소된다(경계값).
            var exact = new FakeOrderRepository(NewOrder(OrderStatus.Paid));
            var ok = await new OrdersController(new OrderService(exact, NullLogger<OrderService>.Instance))
                .Cancel(1, new CancelRequest { Reason = new string('가', 200) });
            Assert.IsType<NoContentResult>(ok);
            Assert.Equal(new string('가', 200), exact.Order.CancelReason);
        }

        [Fact]
        public async Task NonPositiveOrderId_IsBadRequest()
        {
            foreach (var id in new[] { 0, -1 })
            {
                var repo = new FakeOrderRepository(NewOrder(OrderStatus.Paid));
                var controller = new OrdersController(new OrderService(repo, NullLogger<OrderService>.Instance));

                Assert.IsType<BadRequestObjectResult>(await controller.Cancel(id, new CancelRequest { Reason = "단순 변심" }));
                var get = Assert.IsType<BadRequestObjectResult>(await controller.Get(id));
                Assert.Equal("주문 번호가 올바르지 않습니다.", get.Value);
                Assert.Empty(repo.Searches);
            }

            // 양수 번호는 그대로 조회된다.
            var found = new FakeOrderRepository(NewOrder(OrderStatus.Paid));
            var result = await new OrdersController(new OrderService(found, NullLogger<OrderService>.Instance)).Get(1);
            Assert.IsType<OkObjectResult>(result);
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
            public List<OrderSearchRequest> Searches { get; } = new List<OrderSearchRequest>();

            public Task<Order> GetByIdAsync(int orderId) => Task.FromResult(orderId == Order.OrderId ? Order : null);
            public Task<IReadOnlyList<OrderItem>> GetItemsAsync(int orderId) => Task.FromResult<IReadOnlyList<OrderItem>>(Order.Items.ToList());

            public Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request)
            {
                Searches.Add(request);
                return Task.FromResult<IReadOnlyList<Order>>(new[] { Order });
            }

            public Task UpdateStatusAsync(int orderId, OrderStatus status, string reason) { Order.Status = status; Order.CancelReason = reason; return Task.CompletedTask; }
            public Task AddHistoryAsync(int orderId, string message) { History.Add(message); return Task.CompletedTask; }
            public Task AddRefundAsync(int orderId, decimal amount) => Task.CompletedTask;
        }
    }
}
