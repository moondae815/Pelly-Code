using Shop.Data.Dto;

namespace Shop.Web.Services
{
    public interface IOrderService
    {
        OrderDto GetOrder(int orderId);
        void Cancel(int orderId, string reason);
    }
}
