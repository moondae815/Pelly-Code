namespace SameName.Data;

public partial class OrderDao
{
    public Order GetOrder(string code) => new Order { Id = code.Length };

    public partial int Count() => 0;
}
