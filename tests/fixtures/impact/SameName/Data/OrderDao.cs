namespace SameName.Data;

public class OrderDao : IOrderDao
{
    public Order GetOrder(int id) => new Order { Id = id };
}
