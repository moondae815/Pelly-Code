using System;
using System.Collections.Generic;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Shop.Data;
using Shop.Data.Dto;
using Shop.Web.Services;

namespace Shop.Tests
{
    /// <summary>
    /// apply 테스트 우선 사례 t8의 수용 기준 테스트(MSTest, .NET Framework 4.8). 레거시 테스트는 사례 폴더 pelly.json의
    /// test.command({trx})로 돌아 테스트별 결과를 낸다.
    /// </summary>
    [TestClass]
    public class PendingCancelTests
    {
        [TestMethod]
        public void Cancel_WhenPending_AddsPendingHistory()
        {
            var pending = new HistoryDao(OrderStatus.Pending);
            new OrderService(pending).Cancel(1, "단순 변심");
            CollectionAssert.Contains(pending.History, "결제 전 주문 취소");

            // 결제된 주문에는 남기지 않는다(과잉 수정 방지).
            var paid = new HistoryDao(OrderStatus.Paid);
            new OrderService(paid).Cancel(1, "단순 변심");
            CollectionAssert.DoesNotContain(paid.History, "결제 전 주문 취소");
        }

        private sealed class HistoryDao : IOrderDao
        {
            public HistoryDao(OrderStatus status)
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
