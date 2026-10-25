namespace SameName.Data;

public partial class OrderDao : IOrderDao
{
    public Order GetOrder(int id) => new Order { Id = id };
}
