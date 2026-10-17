using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Shop.Api.Models;

namespace Shop.Api.Data
{
    /// <summary>Dapper 기반 주문 저장소</summary>
    public class OrderRepository : IOrderRepository
    {
        private readonly string _connectionString;

        public OrderRepository(string connectionString)
        {
            _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));
        }

        public async Task<Order> GetByIdAsync(int orderId)
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                const string sql = @"
SELECT OrderId, CustomerId, Status, TotalAmount, ShippingFee, DiscountAmount, PointUsed,
       OrderedAt, PaidAt, ShippedAt, DeliveredAt, CancelledAt, CancelReason
FROM dbo.Orders
WHERE OrderId = @OrderId";

                var order = await conn.QuerySingleOrDefaultAsync<Order>(sql, new { OrderId = orderId });
                if (order != null)
                {
                    order.Items = (await GetItemsAsync(orderId)).ToList();
                }

                return order;
            }
        }

        public async Task<IReadOnlyList<OrderItem>> GetItemsAsync(int orderId)
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                const string sql = @"
SELECT OrderItemId, OrderId, ProductId, ProductName, Quantity, UnitPrice, IsGiftItem, IsRefundable
FROM dbo.OrderItems
WHERE OrderId = @OrderId
ORDER BY OrderItemId";

                var items = await conn.QueryAsync<OrderItem>(sql, new { OrderId = orderId });
                return items.ToList();
            }
        }

        public async Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request)
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                var sql = @"
SELECT o.OrderId, o.CustomerId, o.Status, o.TotalAmount, o.ShippingFee, o.OrderedAt, o.PaidAt, o.ShippedAt
FROM dbo.Orders o
JOIN dbo.Customers c ON c.CustomerId = o.CustomerId
WHERE 1 = 1";

                if (request.CustomerId.HasValue)
                {
                    sql += " AND o.CustomerId = @CustomerId";
                }

                if (request.Status.HasValue)
                {
                    sql += " AND o.Status = @Status";
                }

                if (request.From.HasValue)
                {
                    sql += " AND o.OrderedAt >= @From";
                }

                if (request.To.HasValue)
                {
                    sql += " AND o.OrderedAt < @To";
                }

                if (!string.IsNullOrEmpty(request.Keyword))
                {
                    sql += " AND (c.Name LIKE @Keyword OR EXISTS (SELECT 1 FROM dbo.OrderItems i WHERE i.OrderId = o.OrderId AND i.ProductName LIKE @Keyword))";
                }

                sql += " ORDER BY o.OrderedAt DESC OFFSET @Skip ROWS FETCH NEXT @Take ROWS ONLY";

                var orders = await conn.QueryAsync<Order>(sql, new
                {
                    request.CustomerId,
                    Status = (int?)request.Status,
                    request.From,
                    request.To,
                    Keyword = "%" + request.Keyword + "%",
                    Skip = (request.Page - 1) * request.PageSize,
                    Take = request.PageSize
                });

                return orders.ToList();
            }
        }

        public async Task UpdateStatusAsync(int orderId, OrderStatus status, string reason)
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                const string sql = @"
UPDATE dbo.Orders
SET Status = @Status,
    CancelledAt = CASE WHEN @Status = 9 THEN SYSDATETIME() ELSE CancelledAt END,
    CancelReason = CASE WHEN @Status = 9 THEN @Reason ELSE CancelReason END
WHERE OrderId = @OrderId";

                await conn.ExecuteAsync(sql, new { OrderId = orderId, Status = (int)status, Reason = reason });
            }
        }

        public async Task AddHistoryAsync(int orderId, string message)
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.ExecuteAsync(
                    "INSERT INTO dbo.OrderHistories (OrderId, Message, CreatedAt) VALUES (@OrderId, @Message, SYSDATETIME())",
                    new { OrderId = orderId, Message = message });
            }
        }

        public async Task AddRefundAsync(int orderId, decimal amount)
        {
            using (var conn = new SqlConnection(_connectionString))
            {
                await conn.ExecuteAsync(
                    "INSERT INTO dbo.Refunds (OrderId, Amount, RequestedAt) VALUES (@OrderId, @Amount, SYSDATETIME())",
                    new { OrderId = orderId, Amount = amount });
            }
        }
    }
}
