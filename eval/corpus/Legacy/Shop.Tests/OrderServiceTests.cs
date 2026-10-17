using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shop.Data;
using Shop.Data.Dto;
using Shop.Web.Services;

namespace Shop.Tests
{
    [TestClass]
    public class OrderServiceTests
    {
        // 벤치마크 cs-legacy-001의 "수정 전" 상태: 배송 중(Shipped) 주문도 현재는 취소된다.
        [TestMethod]
        public void Cancel_WhenShipped_CurrentlyCancels()
        {
            var dao = new FakeOrderDao(OrderStatus.Shipped);
            var service = new OrderService(dao);

            service.Cancel(1, "단순 변심");

            Assert.AreEqual(OrderStatus.Cancelled, dao.Order.Status);
        }

        [TestMethod]
        public void Cancel_WhenDelivered_Throws()
        {
            var service = new OrderService(new FakeOrderDao(OrderStatus.Delivered));

            Assert.ThrowsException<InvalidOperationException>(() => service.Cancel(1, "단순 변심"));
        }

        [TestMethod]
        public void Cancel_WithBlockedReason_Throws()
        {
            var service = new OrderService(new FakeOrderDao(OrderStatus.Paid));

            Assert.ThrowsException<ArgumentException>(() => service.Cancel(1, "test"));
        }

        private sealed class FakeOrderDao : IOrderDao
        {
            public FakeOrderDao(OrderStatus status)
            {
                Order = new OrderDto { OrderId = 1, CustomerId = 100, Status = status, TotalAmount = 25000m };
                History = new List<string>();
            }

            public OrderDto Order { get; private set; }
            public List<string> History { get; private set; }

            public OrderDto GetOrder(int orderId)
            {
                return orderId == Order.OrderId ? Order : null;
            }

            public IList<OrderDto> SearchOrders(int? customerId, OrderStatus? status, DateTime? from, DateTime? to, string keyword)
            {
                return new List<OrderDto> { Order };
            }

            public int UpdateStatus(int orderId, OrderStatus status, string reason)
            {
                Order.Status = status;
                Order.CancelReason = reason;
                return 1;
            }

            public void AddHistory(int orderId, string message)
            {
                History.Add(message);
            }
        }
    }
}
