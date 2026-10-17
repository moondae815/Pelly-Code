using System;
using System.Collections.Generic;

namespace Shop.Api.Models
{
    /// <summary>주문</summary>
    public class Order
    {
        public int OrderId { get; set; }
        public int CustomerId { get; set; }
        public OrderStatus Status { get; set; }
        public decimal TotalAmount { get; set; }
        public decimal ShippingFee { get; set; }
        public decimal DiscountAmount { get; set; }
        public decimal PointUsed { get; set; }
        public DateTime OrderedAt { get; set; }
        public DateTime? PaidAt { get; set; }
        public DateTime? ShippedAt { get; set; }
        public DateTime? DeliveredAt { get; set; }
        public DateTime? CancelledAt { get; set; }
        public string CancelReason { get; set; }
        public List<OrderItem> Items { get; set; } = new List<OrderItem>();
    }

    /// <summary>주문 상품</summary>
    public class OrderItem
    {
        public int OrderItemId { get; set; }
        public int OrderId { get; set; }
        public int ProductId { get; set; }
        public string ProductName { get; set; }
        public int Quantity { get; set; }
        public decimal UnitPrice { get; set; }
        public bool IsGiftItem { get; set; }
        public bool IsRefundable { get; set; } = true;
    }
}
