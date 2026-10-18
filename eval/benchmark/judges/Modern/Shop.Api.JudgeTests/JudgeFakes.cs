using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shop.Api.Data;
using Shop.Api.Models;
using Shop.Api.Services;

namespace Shop.Api.JudgeTests
{
    /// <summary>주문 한 건을 들고 있는 메모리 저장소. 코퍼스 Shop.Api.Tests의 FakeOrderRepository와 같은 방식이다.</summary>
    public sealed class FakeOrderRepository : IOrderRepository
    {
        public FakeOrderRepository(Order order) => Order = order;
        public Order Order { get; }
        public List<string> History { get; } = new List<string>();
        public decimal RefundRequested { get; private set; }
        public int StatusUpdates { get; private set; }

        public Task<Order> GetByIdAsync(int orderId) => Task.FromResult(orderId == Order.OrderId ? Order : null);
        public Task<IReadOnlyList<OrderItem>> GetItemsAsync(int orderId) => Task.FromResult<IReadOnlyList<OrderItem>>(Order.Items.ToList());
        public List<OrderSearchRequest> Searches { get; } = new List<OrderSearchRequest>();

        public Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request)
        {
            Searches.Add(request);
            return Task.FromResult<IReadOnlyList<Order>>(new[] { Order });
        }
        public Task UpdateStatusAsync(int orderId, OrderStatus status, string reason) { StatusUpdates++; Order.Status = status; Order.CancelReason = reason; return Task.CompletedTask; }
        public Task AddHistoryAsync(int orderId, string message) { History.Add(message); return Task.CompletedTask; }
        public Task AddRefundAsync(int orderId, decimal amount) { RefundRequested = amount; return Task.CompletedTask; }
    }

    /// <summary>
    /// 컨트롤러 판정용 주문 서비스. 검색은 주문 한 건(<see cref="Order"/>)을 돌려주고, 취소는 <see cref="CancelThrows"/>가 있으면 그것을 던진다.
    /// </summary>
    public sealed class FakeOrderService : IOrderService
    {
        public Order Order { get; set; } = JudgeOrders.New(OrderStatus.Paid);
        public Exception CancelThrows { get; set; }
        public List<OrderSearchRequest> Searches { get; } = new List<OrderSearchRequest>();
        public List<(int OrderId, string Reason)> Cancels { get; } = new List<(int, string)>();

        public Task CancelAsync(int orderId, string reason)
        {
            Cancels.Add((orderId, reason));
            if (CancelThrows != null) throw CancelThrows;
            return Task.CompletedTask;
        }

        public Task<decimal> CalculateRefundAsync(int orderId) => Task.FromResult(0m);

        public Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request)
        {
            Searches.Add(request);
            return Task.FromResult<IReadOnlyList<Order>>(new[] { Order });
        }
    }

    /// <summary>남긴 로그를 모아 두는 로거. 판정 테스트가 로그 수준·문구를 확인할 때 쓴다.</summary>
    public sealed class FakeLogger<T> : ILogger<T>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = new List<(LogLevel, string)>();

        public IDisposable BeginScope<TState>(TState state) => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }

    /// <summary>판정 테스트가 같이 쓰는 주문 견본.</summary>
    public static class JudgeOrders
    {
        /// <summary>상품 2 x 10,000 + 1 x 5,000, 배송비 3,000(코퍼스 테스트의 NewOrder와 같은 주문).</summary>
        public static Order New(OrderStatus status) => new Order
        {
            OrderId = 1,
            CustomerId = 100,
            Status = status,
            TotalAmount = 28000m,
            ShippingFee = 3000m,
            OrderedAt = new DateTime(2026, 10, 1, 10, 0, 0),
            PaidAt = status == OrderStatus.Pending ? (DateTime?)null : new DateTime(2026, 10, 1, 10, 5, 0),
            ShippedAt = status == OrderStatus.Shipped ? new DateTime(2026, 10, 2, 9, 0, 0) : (DateTime?)null,
            Items = new List<OrderItem>
            {
                new OrderItem { OrderItemId = 1, OrderId = 1, ProductId = 10, ProductName = "머그컵", Quantity = 2, UnitPrice = 10000m },
                new OrderItem { OrderItemId = 2, OrderId = 1, ProductId = 11, ProductName = "컵받침", Quantity = 1, UnitPrice = 5000m },
            }
        };

        /// <summary>결제된 무료 배송 주문 한 건. 상품 하나의 단가가 그대로 환불액이 된다.</summary>
        public static Order PaidSingleItem(decimal unitPrice) => new Order
        {
            OrderId = 1,
            CustomerId = 100,
            Status = OrderStatus.Paid,
            TotalAmount = 100000m,
            ShippingFee = 0m,
            OrderedAt = new DateTime(2026, 10, 1, 10, 0, 0),
            PaidAt = new DateTime(2026, 10, 1, 10, 5, 0),
            Items = new List<OrderItem>
            {
                new OrderItem { OrderItemId = 1, OrderId = 1, ProductId = 10, ProductName = "머그컵", Quantity = 1, UnitPrice = unitPrice },
            }
        };
    }
}
