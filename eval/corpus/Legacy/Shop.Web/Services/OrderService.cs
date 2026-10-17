using System;
using System.Collections.Generic;
using System.Diagnostics;
using Shop.Data;
using Shop.Data.Dto;

namespace Shop.Web.Services
{
    /// <summary>
    /// 주문 업무 처리
    /// 작성: 2016-03 / 수정: 2019-11 취소 사유 길이 제한 추가
    /// </summary>
    public class OrderService : IOrderService
    {
        private const int MaxReasonLength = 200;
        private static readonly string[] BlockedWords = { "테스트", "test" };

        private readonly IOrderDao _orderDao;

        public OrderService(IOrderDao orderDao)
        {
            if (orderDao == null)
            {
                throw new ArgumentNullException("orderDao");
            }

            _orderDao = orderDao;
        }

        public OrderDto GetOrder(int orderId)
        {
            return _orderDao.GetOrder(orderId);
        }

        /// <summary>
        /// 주문을 취소한다.
        /// </summary>
        /// <param name="orderId">주문 번호</param>
        /// <param name="reason">취소 사유</param>
        public void Cancel(int orderId, string reason)
        {
            if (orderId <= 0)
            {
                throw new ArgumentOutOfRangeException("orderId", "주문 번호가 올바르지 않습니다.");
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("취소 사유를 입력하세요.", "reason");
            }

            // 사유 정리: 앞뒤 공백 제거, 길이 제한
            var trimmed = reason.Trim();
            if (trimmed.Length > MaxReasonLength)
            {
                trimmed = trimmed.Substring(0, MaxReasonLength);
            }

            // 운영 데이터에 테스트 사유가 남지 않도록 막는다.
            foreach (var word in BlockedWords)
            {
                if (trimmed.Equals(word, StringComparison.OrdinalIgnoreCase))
                {
                    throw new ArgumentException("사용할 수 없는 취소 사유입니다.", "reason");
                }
            }

            var order = _orderDao.GetOrder(orderId);
            if (order == null)
            {
                throw new KeyNotFoundException("주문을 찾을 수 없습니다. OrderId=" + orderId);
            }

            if (order.Status == OrderStatus.Cancelled)
            {
                // 이미 취소된 주문은 무시
                Trace.TraceInformation("이미 취소된 주문: {0}", orderId);
                return;
            }

            if (order.Status == OrderStatus.Delivered)
            {
                throw new InvalidOperationException("배송 완료된 주문은 취소할 수 없습니다.");
            }

            var previous = order.Status;
            var updated = _orderDao.UpdateStatus(orderId, OrderStatus.Cancelled, trimmed);
            if (updated != 1)
            {
                throw new InvalidOperationException("주문 상태를 변경하지 못했습니다. OrderId=" + orderId);
            }

            _orderDao.AddHistory(orderId, string.Format("주문 취소 ({0} -> Cancelled): {1}", previous, trimmed));

            if (previous == OrderStatus.Paid || previous == OrderStatus.Shipped)
            {
                // 환불은 정산 배치에서 처리한다.
                _orderDao.AddHistory(orderId, "환불 대기 등록");
            }

            Trace.TraceInformation("주문 취소 완료: {0}, 이전 상태 {1}", orderId, previous);
        }
    }
}
