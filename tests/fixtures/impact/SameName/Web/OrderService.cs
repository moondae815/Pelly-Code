using SameName.Data;

namespace SameName.Web;

public class OrderService : IOrderService
{
    private readonly IOrderDao _dao;

    public OrderService(IOrderDao dao)
    {
        _dao = dao;
    }

    public Order GetOrder(int id)
    {
        return _dao.GetOrder(id);
    }

    public void Cancel(int id)
    {
        var order = _dao.GetOrder(id);
        order.Id = -order.Id;
    }
}
