namespace Shop.Data.Dto
{
    /// <summary>주문 상태 (DB 값과 같음)</summary>
    public enum OrderStatus
    {
        Pending = 0,
        Paid = 1,
        Shipped = 2,
        Delivered = 3,
        Cancelled = 9
    }
}
