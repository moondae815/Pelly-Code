using System;

namespace Shop.Data.Dto
{
    /// <summary>주문 조회 결과</summary>
    public class OrderDto
    {
        public int OrderId { get; set; }
        public int CustomerId { get; set; }
        public string CustomerName { get; set; }
        public OrderStatus Status { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal ShippingFee { get; set; }
        public DateTime OrderedAt { get; set; }
        public DateTime? PaidAt { get; set; }
        public DateTime? ShippedAt { get; set; }
        public DateTime? CancelledAt { get; set; }
        public string CancelReason { get; set; }
    }
}
