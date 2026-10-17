using System.Collections.Generic;
using System.Threading.Tasks;
using Shop.Api.Models;

namespace Shop.Api.Data
{
    public interface IOrderRepository
    {
        Task<Order> GetByIdAsync(int orderId);
        Task<IReadOnlyList<OrderItem>> GetItemsAsync(int orderId);
        Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request);
        Task UpdateStatusAsync(int orderId, OrderStatus status, string reason);
        Task AddHistoryAsync(int orderId, string message);
        Task AddRefundAsync(int orderId, decimal amount);
    }
}
