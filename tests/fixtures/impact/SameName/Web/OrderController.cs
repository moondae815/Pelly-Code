using SameName.Data;

namespace SameName.Web;

public class OrderController
{
    private readonly IOrderService _service;

    public OrderController(IOrderService service)
    {
        _service = service;
    }

    public Order Detail(int id)
    {
        return _service.GetOrder(id);
    }
}
