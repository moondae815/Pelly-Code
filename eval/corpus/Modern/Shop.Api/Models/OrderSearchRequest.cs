using System;

namespace Shop.Api.Models
{
    /// <summary>주문 검색 조건</summary>
    public class OrderSearchRequest
    {
        public int? CustomerId { get; set; }
        public OrderStatus? Status { get; set; }
        public DateTime? From { get; set; }
        public DateTime? To { get; set; }
        public string Keyword { get; set; }
        public int Page { get; set; } = 1;
        public int PageSize { get; set; } = 20;
    }
}
