using SameName.Data;

namespace SameName.Web.Tests;

public class FakeOrderDao : IOrderDao
{
    Order IOrderDao.GetOrder(int id) => new Order { Id = id };
}
