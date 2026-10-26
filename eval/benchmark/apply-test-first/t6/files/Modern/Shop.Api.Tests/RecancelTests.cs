using System;
using System.Collections.Generic;
using System.Data;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using Shop.Api.Data;
using Shop.Api.Models;
using Shop.Api.Services;
using Xunit;

namespace Shop.Api.Tests
{
    /// <summary>
    /// apply 테스트 우선 사례 t6의 수용 기준 테스트. AC1은 SQL 작업과 C# 작업이 함께 참조하지만 테스트로 판정하는 것은 C# 작업뿐이다.
    /// AC2는 SQL 작업만 참조하므로 테스트로 검증되지 않는다(spec check 경고 TestUnverified): 그 테스트는 SQL 서버가 있어야 돌아
    /// 평소에는 건너뛴다.
    /// </summary>
    public class RecancelTests
    {
        [Fact]
        public async Task CancelAsync_WhenAlreadyCancelled_Throws()
        {
            var repo = new FakeOrderRepository(NewOrder(OrderStatus.Cancelled));
            var service = new OrderService(repo, NullLogger<OrderService>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.CancelAsync(1, "단순 변심"));

            Assert.Equal(0, repo.StatusUpdates);
            Assert.Empty(repo.History);
            Assert.Equal(0m, repo.RefundRequested);
        }

        [Fact(Skip = "SQL 서버가 있어야 실행합니다 (환경 변수 SHOP_DB)")]
        public void GetOrderDetail_ReturnsCancelColumns()
        {
            using (var conn = new SqlConnection(Environment.GetEnvironmentVariable("SHOP_DB")))
            using (var grid = conn.QueryMultiple("dbo.usp_GetOrderDetail", new { OrderId = 1 }, commandType: CommandType.StoredProcedure))
            {
                var order = (IDictionary<string, object>)grid.Read().First();
                Assert.Contains("CancelledAt", order.Keys);
                Assert.Contains("CancelReason", order.Keys);
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
            PaidAt = new DateTime(2026, 10, 1, 10, 5, 0),
            CancelledAt = new DateTime(2026, 10, 1, 11, 0, 0),
            CancelReason = "단순 변심",
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
            public decimal RefundRequested { get; private set; }
            public int StatusUpdates { get; private set; }

            public Task<Order> GetByIdAsync(int orderId) => Task.FromResult(orderId == Order.OrderId ? Order : null);
            public Task<IReadOnlyList<OrderItem>> GetItemsAsync(int orderId) => Task.FromResult<IReadOnlyList<OrderItem>>(Order.Items.ToList());
            public Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request) => Task.FromResult<IReadOnlyList<Order>>(new[] { Order });
            public Task UpdateStatusAsync(int orderId, OrderStatus status, string reason) { StatusUpdates++; Order.Status = status; Order.CancelReason = reason; return Task.CompletedTask; }
            public Task AddHistoryAsync(int orderId, string message) { History.Add(message); return Task.CompletedTask; }
            public Task AddRefundAsync(int orderId, decimal amount) { RefundRequested = amount; return Task.CompletedTask; }
        }
    }
}
