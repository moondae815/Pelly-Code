using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using Dapper;
using Microsoft.Data.SqlClient;

namespace Shop.Jobs
{
    /// <summary>
    /// 일별 정산 배치. 전날 배송 완료된 주문을 판매자별로 모아 정산 레코드를 만든다.
    /// </summary>
    public class DailySettlementJob
    {
        private const decimal DefaultCommissionRate = 0.12m;
        private const decimal MinimumPayout = 1000m;

        private readonly string _connectionString;
        private readonly TextWriter _log;

        public DailySettlementJob(string connectionString, TextWriter log)
        {
            _connectionString = connectionString;
            _log = log;
        }

        /// <summary>
        /// 정산을 실행한다. 이미 정산된 날짜면 아무것도 하지 않고 true를 반환한다.
        /// </summary>
        public bool Run(DateTime businessDate)
        {
            var from = businessDate.Date;
            var to = from.AddDays(1);
            _log.WriteLine("[정산] 시작: {0:yyyy-MM-dd}", from);

            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                // 같은 날짜를 두 번 정산하지 않는다.
                var already = conn.ExecuteScalar<int>(
                    "SELECT COUNT(*) FROM dbo.Settlements WHERE BusinessDate = @BusinessDate",
                    new { BusinessDate = from });
                if (already > 0)
                {
                    _log.WriteLine("[정산] 이미 정산된 날짜입니다. 건너뜁니다.");
                    return true;
                }

                var rows = conn.Query<SettlementSource>(@"
SELECT o.OrderId, o.SellerId, o.TotalAmount, o.ShippingFee, o.DiscountAmount, s.CommissionRate
FROM dbo.Orders o
JOIN dbo.Sellers s ON s.SellerId = o.SellerId
WHERE o.Status = 3
  AND o.DeliveredAt >= @From AND o.DeliveredAt < @To",
                    new { From = from, To = to }).ToList();

                if (rows.Count == 0)
                {
                    _log.WriteLine("[정산] 대상 주문이 없습니다.");
                    return true;
                }

                // 판매자별로 금액을 모은다.
                var bySeller = new Dictionary<int, SellerTotal>();
                foreach (var row in rows)
                {
                    if (row.TotalAmount <= 0)
                    {
                        _log.WriteLine("[정산] 금액이 0 이하인 주문을 건너뜁니다. OrderId={0}", row.OrderId);
                        continue;
                    }

                    SellerTotal total;
                    if (!bySeller.TryGetValue(row.SellerId, out total))
                    {
                        total = new SellerTotal { SellerId = row.SellerId };
                        bySeller.Add(row.SellerId, total);
                    }

                    var rate = row.CommissionRate.HasValue ? row.CommissionRate.Value : DefaultCommissionRate;
                    var sales = row.TotalAmount - row.ShippingFee;
                    var commission = Math.Round(sales * rate, 0, MidpointRounding.AwayFromZero);

                    total.OrderCount++;
                    total.SalesAmount += sales;
                    total.ShippingAmount += row.ShippingFee;
                    total.CommissionAmount += commission;
                    total.DiscountAmount += row.DiscountAmount;
                }

                var payouts = new List<SellerTotal>();
                var deferred = new List<SellerTotal>();
                foreach (var total in bySeller.Values.OrderBy(t => t.SellerId))
                {
                    total.PayoutAmount = total.SalesAmount + total.ShippingAmount - total.CommissionAmount;

                    // 최소 지급액 미만은 다음 정산으로 넘긴다.
                    if (total.PayoutAmount < MinimumPayout)
                    {
                        deferred.Add(total);
                        continue;
                    }

                    payouts.Add(total);
                }

                using (var tx = conn.BeginTransaction(IsolationLevel.ReadCommitted))
                {
                    try
                    {
                        foreach (var p in payouts)
                        {
                            conn.Execute(@"
INSERT INTO dbo.Settlements (BusinessDate, SellerId, OrderCount, SalesAmount, ShippingAmount, CommissionAmount, PayoutAmount, CreatedAt)
VALUES (@BusinessDate, @SellerId, @OrderCount, @SalesAmount, @ShippingAmount, @CommissionAmount, @PayoutAmount, SYSDATETIME())",
                                new
                                {
                                    BusinessDate = from,
                                    p.SellerId,
                                    p.OrderCount,
                                    p.SalesAmount,
                                    p.ShippingAmount,
                                    p.CommissionAmount,
                                    p.PayoutAmount
                                },
                                tx);
                        }

                        foreach (var d in deferred)
                        {
                            conn.Execute(
                                "INSERT INTO dbo.SettlementDeferrals (BusinessDate, SellerId, Amount) VALUES (@BusinessDate, @SellerId, @Amount)",
                                new { BusinessDate = from, d.SellerId, Amount = d.PayoutAmount },
                                tx);
                        }

                        tx.Commit();
                    }
                    catch (Exception ex)
                    {
                        tx.Rollback();
                        _log.WriteLine("[정산] 실패하여 롤백했습니다: {0}", ex.Message);
                        return false;
                    }
                }

                _log.WriteLine(
                    "[정산] 완료: 판매자 {0}명 지급, {1}명 이월, 총 지급액 {2:N0}원",
                    payouts.Count,
                    deferred.Count,
                    payouts.Sum(p => p.PayoutAmount));
                return true;
            }
        }

        private sealed class SettlementSource
        {
            public int OrderId { get; set; }
            public int SellerId { get; set; }
            public decimal TotalAmount { get; set; }
            public decimal ShippingFee { get; set; }
            public decimal DiscountAmount { get; set; }
            public decimal? CommissionRate { get; set; }
        }

        private sealed class SellerTotal
        {
            public int SellerId { get; set; }
            public int OrderCount { get; set; }
            public decimal SalesAmount { get; set; }
            public decimal ShippingAmount { get; set; }
            public decimal CommissionAmount { get; set; }
            public decimal DiscountAmount { get; set; }
            public decimal PayoutAmount { get; set; }
        }
    }
}
