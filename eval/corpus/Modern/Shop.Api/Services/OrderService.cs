using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Shop.Api.Data;
using Shop.Api.Models;

namespace Shop.Api.Services
{
    /// <summary>
    /// 주문 처리 서비스 (취소, 환불 계산, 검색)
    /// </summary>
    public class OrderService : IOrderService
    {
        private const int MaxReasonLength = 200;
        private const decimal ReturnShippingFee = 3000m;
        private const decimal FreeShippingThreshold = 30000m;
        private const int MaxPageSize = 100;

        private readonly IOrderRepository _repository;
        private readonly ILogger<OrderService> _logger;

        public OrderService(IOrderRepository repository, ILogger<OrderService> logger)
        {
            _repository = repository ?? throw new ArgumentNullException(nameof(repository));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// 주문을 취소하고, 결제된 주문이면 환불을 요청한다.
        /// </summary>
        public async Task CancelAsync(int orderId, string reason)
        {
            if (orderId <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(orderId), "주문 번호가 올바르지 않습니다.");
            }

            if (string.IsNullOrWhiteSpace(reason))
            {
                throw new ArgumentException("취소 사유를 입력해야 합니다.", nameof(reason));
            }

            var trimmedReason = reason.Trim();
            if (trimmedReason.Length > MaxReasonLength)
            {
                trimmedReason = trimmedReason.Substring(0, MaxReasonLength);
            }

            var order = await _repository.GetByIdAsync(orderId);
            if (order == null)
            {
                throw new KeyNotFoundException($"주문을 찾을 수 없습니다. OrderId={orderId}");
            }

            // 이미 취소된 주문은 다시 처리하지 않는다.
            if (order.Status == OrderStatus.Cancelled)
            {
                _logger.LogInformation("이미 취소된 주문입니다. OrderId={OrderId}", orderId);
                return;
            }

            // 배송 완료된 주문은 반품 절차를 따라야 한다.
            if (order.Status == OrderStatus.Delivered)
            {
                throw new InvalidOperationException("배송 완료된 주문은 취소할 수 없습니다. 반품을 신청하세요.");
            }

            if (order.Status == OrderStatus.Pending)
            {
                _logger.LogInformation("결제 전 주문을 취소합니다. OrderId={OrderId}", orderId);
            }

            // 결제 전 주문은 환불 없이 상태만 바꾼다.
            var refund = 0m;
            if (order.Status == OrderStatus.Paid || order.Status == OrderStatus.Shipped)
            {
                refund = await CalculateRefundAsync(orderId);
            }

            await _repository.UpdateStatusAsync(orderId, OrderStatus.Cancelled, trimmedReason);
            await _repository.AddHistoryAsync(orderId, $"주문 취소: {trimmedReason}");

            if (refund > 0)
            {
                await _repository.AddRefundAsync(orderId, refund);
                await _repository.AddHistoryAsync(orderId, $"환불 요청: {refund:N0}원");
            }

            _logger.LogInformation(
                "주문 취소 완료. OrderId={OrderId}, 이전 상태={Status}, 환불={Refund}",
                orderId,
                order.Status,
                refund);
        }

        /// <summary>
        /// 주문 취소 시 고객에게 돌려줄 현금 환불액을 계산한다.
        /// 사용한 포인트는 포인트로 따로 돌려주므로 현금 환불액에서 뺀다.
        /// </summary>
        public async Task<decimal> CalculateRefundAsync(int orderId)
        {
            var order = await _repository.GetByIdAsync(orderId);
            if (order == null)
            {
                throw new KeyNotFoundException($"주문을 찾을 수 없습니다. OrderId={orderId}");
            }

            switch (order.Status)
            {
                case OrderStatus.Pending:
                    // 결제 전 주문은 환불할 금액이 없다.
                    return 0m;
                case OrderStatus.Cancelled:
                    throw new InvalidOperationException("이미 취소된 주문입니다.");
                case OrderStatus.Delivered:
                    throw new InvalidOperationException("배송 완료된 주문은 반품 환불 절차를 따릅니다.");
            }

            // 주문에 상품 정보가 없으면 저장소에서 다시 읽는다.
            IReadOnlyList<OrderItem> items = order.Items;
            if (items == null || items.Count == 0)
            {
                items = await _repository.GetItemsAsync(orderId);
            }

            if (items == null || items.Count == 0)
            {
                _logger.LogWarning("주문 상품이 없습니다. OrderId={OrderId}", orderId);
                return 0m;
            }

            // 사은품과 환불 불가 상품은 환불 대상에서 뺀다.
            var refundableItems = items
                .Where(i => !i.IsGiftItem && i.IsRefundable)
                .ToList();

            var itemTotal = 0m;
            foreach (var item in refundableItems)
            {
                if (item.Quantity <= 0 || item.UnitPrice < 0)
                {
                    _logger.LogWarning(
                        "비정상 주문 상품을 건너뜁니다. OrderItemId={OrderItemId}, Quantity={Quantity}, UnitPrice={UnitPrice}",
                        item.OrderItemId,
                        item.Quantity,
                        item.UnitPrice);
                    continue;
                }

                itemTotal += item.Quantity * item.UnitPrice;
            }

            var allItemTotal = items.Sum(i => i.Quantity * i.UnitPrice);

            // 주문 할인은 환불 대상 상품 금액 비율만큼 나눠서 뺀다.
            var discount = 0m;
            if (order.DiscountAmount > 0 && allItemTotal > 0)
            {
                discount = Math.Round(order.DiscountAmount * (itemTotal / allItemTotal), 0, MidpointRounding.AwayFromZero);
            }

            var refund = itemTotal - discount;

            // 배송비 처리: 배송 전이면 배송비까지 환불, 배송이 시작됐으면 배송비는 돌려주지 않고 반품 배송비를 뺀다.
            if (order.Status == OrderStatus.Paid)
            {
                refund += order.ShippingFee;
            }
            else if (order.Status == OrderStatus.Shipped)
            {
                refund -= ReturnShippingFee;

                // 무료 배송 기준을 넘어 배송비를 받지 않은 주문은 원래 배송비도 함께 부담한다.
                if (order.ShippingFee == 0 && allItemTotal >= FreeShippingThreshold)
                {
                    refund -= ReturnShippingFee;
                }
            }

            // 포인트 사용분은 포인트로 돌려주므로 현금 환불액에서 뺀다.
            if (order.PointUsed > 0)
            {
                refund -= order.PointUsed;
            }

            // 10원 미만은 버린다.
            refund = Math.Floor(refund / 10m) * 10m;

            if (refund < 0)
            {
                refund = 0m;
            }

            var maxRefund = order.TotalAmount - order.PointUsed;
            if (maxRefund > 0 && refund > maxRefund)
            {
                _logger.LogWarning(
                    "환불액이 결제액을 넘어 결제액으로 맞춥니다. OrderId={OrderId}, 계산값={Refund}, 결제액={Max}",
                    orderId,
                    refund,
                    maxRefund);
                refund = maxRefund;
            }

            _logger.LogDebug(
                "환불액 계산. OrderId={OrderId}, 상품={ItemTotal}, 할인={Discount}, 포인트={Point}, 환불={Refund}",
                orderId,
                itemTotal,
                discount,
                order.PointUsed,
                refund);

            return refund;
        }

        /// <summary>
        /// 조건에 맞는 주문을 검색한다.
        /// </summary>
        public async Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request)
        {
            if (request == null)
            {
                throw new ArgumentNullException(nameof(request));
            }

            if (request.Page < 1)
            {
                request.Page = 1;
            }

            if (request.PageSize < 1)
            {
                request.PageSize = 20;
            }
            else if (request.PageSize > MaxPageSize)
            {
                request.PageSize = MaxPageSize;
            }

            // 기간을 하나만 지정하면 30일 범위로 채운다.
            if (request.From.HasValue && !request.To.HasValue)
            {
                request.To = request.From.Value.AddDays(30);
            }
            else if (!request.From.HasValue && request.To.HasValue)
            {
                request.From = request.To.Value.AddDays(-30);
            }

            if (request.From.HasValue && request.To.HasValue && request.From > request.To)
            {
                throw new ArgumentException("검색 시작일이 종료일보다 늦습니다.");
            }

            if (!string.IsNullOrWhiteSpace(request.Keyword))
            {
                request.Keyword = request.Keyword.Trim();
            }

            var orders = await _repository.SearchAsync(request);

            _logger.LogInformation(
                "주문 검색. CustomerId={CustomerId}, Status={Status}, 결과={Count}",
                request.CustomerId,
                request.Status,
                orders.Count);

            return orders;
        }
    }
}
