using SameName.Data;

namespace SameName.Web;

public interface IOrderService
{
    Order GetOrder(int id);
}
