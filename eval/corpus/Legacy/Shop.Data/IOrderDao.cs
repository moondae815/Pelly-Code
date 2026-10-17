using System;
using System.Collections.Generic;
using Shop.Data.Dto;

namespace Shop.Data
{
    public interface IOrderDao
    {
        OrderDto GetOrder(int orderId);
        IList<OrderDto> SearchOrders(int? customerId, OrderStatus? status, DateTime? from, DateTime? to, string keyword);
        int UpdateStatus(int orderId, OrderStatus status, string reason);
        void AddHistory(int orderId, string message);
    }
}
