using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Shop.Api.Data;
using Shop.Api.Models;
using Shop.Api.Services;
using Xunit;

namespace Shop.Services.Tests
{
    /// <summary>
    /// apply 테스트 우선 사례 t7의 수용 기준 테스트. 이 프로젝트는 OrderService.cs를 소스로 컴파일하므로, pelly는 이 테스트를
    /// 「지정 테스트를 선언한 프로젝트」로만 찾는다.
    /// </summary>
    public class OrderSearchRangeTests
    {
        [Fact]
        public async Task SearchAsync_RangeOver92Days_Throws()
        {
            var repo = new FakeOrderRepository();
            var service = new OrderService(repo, NullLogger<OrderService>.Instance);

            // 2026-01-01 ~ 2026-04-04는 93일.
            await Assert.ThrowsAsync<ArgumentException>(() => service.SearchAsync(
                new OrderSearchRequest { From = new DateTime(2026, 1, 1), To = new DateTime(2026, 4, 4) }));
            Assert.Empty(repo.Searches);

            // 92일까지는 그대로 검색한다(경계값).
            await service.SearchAsync(new OrderSearchRequest { From = new DateTime(2026, 1, 1), To = new DateTime(2026, 4, 3) });
            Assert.Single(repo.Searches);
        }

        private sealed class FakeOrderRepository : IOrderRepository
        {
            public List<OrderSearchRequest> Searches { get; } = new List<OrderSearchRequest>();

            public Task<Order> GetByIdAsync(int orderId) => Task.FromResult<Order>(null);
            public Task<IReadOnlyList<OrderItem>> GetItemsAsync(int orderId) => Task.FromResult<IReadOnlyList<OrderItem>>(new List<OrderItem>());

            public Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request)
            {
                Searches.Add(request);
                return Task.FromResult<IReadOnlyList<Order>>(new List<Order>());
            }

            public Task UpdateStatusAsync(int orderId, OrderStatus status, string reason) => Task.CompletedTask;
            public Task AddHistoryAsync(int orderId, string message) => Task.CompletedTask;
            public Task AddRefundAsync(int orderId, decimal amount) => Task.CompletedTask;
        }
    }
}
