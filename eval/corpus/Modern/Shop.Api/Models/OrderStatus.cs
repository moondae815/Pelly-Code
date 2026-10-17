namespace Shop.Api.Models
{
    /// <summary>주문 상태</summary>
    public enum OrderStatus
    {
        Pending = 0,
        Paid = 1,
        Shipped = 2,
        Delivered = 3,
        Cancelled = 9
    }
}
