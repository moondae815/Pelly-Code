using SameName.Data;

namespace SameName.Web.Tests;

public class FakeOrderDao : IOrderDao
{
    public Order GetOrder(int id) => new Order { Id = id };
}
