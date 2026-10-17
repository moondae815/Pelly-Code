using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Linq;
using System.Text;
using Dapper;
using Shop.Data.Dto;

namespace Shop.Data
{
    /// <summary>
    /// 주문 데이터 접근 (Dapper)
    /// </summary>
    public class OrderDao : IOrderDao
    {
        private readonly string _connectionString;

        public OrderDao(string connectionString)
        {
            if (string.IsNullOrEmpty(connectionString))
            {
                throw new ArgumentNullException("connectionString");
            }

            _connectionString = connectionString;
        }

        public OrderDto GetOrder(int orderId)
        {
            using (IDbConnection conn = new SqlConnection(_connectionString))
            {
                return conn.Query<OrderDto>(
                    @"SELECT o.OrderId, o.CustomerId, c.Name AS CustomerName, o.Status, o.TotalAmount, o.ShippingFee,
                             o.OrderedAt, o.PaidAt, o.ShippedAt, o.CancelledAt, o.CancelReason
                      FROM dbo.Orders o
                      JOIN dbo.Customers c ON c.CustomerId = o.CustomerId
                      WHERE o.OrderId = @OrderId",
                    new { OrderId = orderId }).FirstOrDefault();
            }
        }

        public IList<OrderDto> SearchOrders(int? customerId, OrderStatus? status, DateTime? from, DateTime? to, string keyword)
        {
            var sql = new StringBuilder();
            sql.AppendLine("SELECT o.OrderId, o.CustomerId, c.Name AS CustomerName, o.Status, o.TotalAmount, o.ShippingFee, o.OrderedAt");
            sql.AppendLine("FROM dbo.Orders o");
            sql.AppendLine("JOIN dbo.Customers c ON c.CustomerId = o.CustomerId");
            sql.AppendLine("WHERE 1 = 1");

            var param = new DynamicParameters();

            if (customerId.HasValue)
            {
                sql.AppendLine("  AND o.CustomerId = @CustomerId");
                param.Add("CustomerId", customerId.Value);
            }

            if (status.HasValue)
            {
                sql.AppendLine("  AND o.Status = @Status");
                param.Add("Status", (int)status.Value);
            }

            if (from.HasValue)
            {
                sql.AppendLine("  AND o.OrderedAt >= @From");
                param.Add("From", from.Value);
            }

            if (to.HasValue)
            {
                sql.AppendLine("  AND o.OrderedAt < @To");
                param.Add("To", to.Value);
            }

            if (!string.IsNullOrEmpty(keyword))
            {
                // 고객 이름으로만 검색한다.
                sql.AppendLine("  AND c.Name LIKE @Keyword");
                param.Add("Keyword", "%" + keyword.Trim() + "%");
            }

            sql.AppendLine("ORDER BY o.OrderedAt DESC");

            using (IDbConnection conn = new SqlConnection(_connectionString))
            {
                return conn.Query<OrderDto>(sql.ToString(), param).ToList();
            }
        }

        public int UpdateStatus(int orderId, OrderStatus status, string reason)
        {
            using (IDbConnection conn = new SqlConnection(_connectionString))
            {
                return conn.Execute(
                    @"UPDATE dbo.Orders
                      SET Status = @Status,
                          CancelledAt = CASE WHEN @Status = 9 THEN GETDATE() ELSE CancelledAt END,
                          CancelReason = CASE WHEN @Status = 9 THEN @Reason ELSE CancelReason END
                      WHERE OrderId = @OrderId",
                    new { OrderId = orderId, Status = (int)status, Reason = reason });
            }
        }

        public void AddHistory(int orderId, string message)
        {
            using (IDbConnection conn = new SqlConnection(_connectionString))
            {
                conn.Execute(
                    "INSERT INTO dbo.OrderHistories (OrderId, Message, CreatedAt) VALUES (@OrderId, @Message, GETDATE())",
                    new { OrderId = orderId, Message = message });
            }
        }
    }
}
