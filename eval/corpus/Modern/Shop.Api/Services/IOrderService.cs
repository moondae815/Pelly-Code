using System.Collections.Generic;
using System.Threading.Tasks;
using Shop.Api.Models;

namespace Shop.Api.Services
{
    public interface IOrderService
    {
        Task CancelAsync(int orderId, string reason);
        Task<decimal> CalculateRefundAsync(int orderId);
        Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request);
    }
}
