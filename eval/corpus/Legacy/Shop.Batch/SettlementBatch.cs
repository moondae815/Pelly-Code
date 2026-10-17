using System;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Globalization;
using System.Linq;
using System.Text;

namespace Shop.Batch
{
    /// <summary>
    /// 주문 정산 배치 (ADO.NET)
    /// 매일 새벽 2시에 전날 배송 완료 주문을 판매자별로 정산한다.
    /// </summary>
    public class SettlementBatch
    {
        private const decimal DefaultCommissionRate = 0.12m;
        private const decimal MinimumPayout = 1000m;
        private const int CommandTimeoutSeconds = 120;

        private readonly string _connectionString;

        public SettlementBatch(string connectionString)
        {
            _connectionString = connectionString;
        }

        /// <summary>
        /// 정산을 실행하고 결과 보고서를 만든다.
        /// </summary>
        public SettlementResult Execute(DateTime businessDate)
        {
            var result = new SettlementResult();
            var warnings = new List<string>();
            var rows = new List<SettlementRow>();
            var from = businessDate.Date;
            var to = from.AddDays(1);

            using (var conn = new SqlConnection(_connectionString))
            {
                conn.Open();

                // 중복 정산 방지
                using (var check = new SqlCommand("SELECT COUNT(*) FROM dbo.Settlements WHERE BusinessDate = @BusinessDate", conn))
                {
                    check.Parameters.Add("@BusinessDate", SqlDbType.Date).Value = from;
                    var count = (int)check.ExecuteScalar();
                    if (count > 0)
                    {
                        result.Success = true;
                        result.Report = "이미 정산된 날짜입니다: " + from.ToString("yyyy-MM-dd");
                        return result;
                    }
                }

                // 정산 대상 주문 집계
                var sql = @"
SELECT s.SellerId, s.Name AS SellerName, p.Category,
       COUNT(DISTINCT o.OrderId) AS OrderCount,
       SUM(i.Quantity * i.UnitPrice) AS SalesAmount,
       MAX(s.CommissionRate) AS CommissionRate
FROM dbo.Orders o
JOIN dbo.OrderItems i ON i.OrderId = o.OrderId
JOIN dbo.Products p ON p.ProductId = i.ProductId
JOIN dbo.Sellers s ON s.SellerId = p.SellerId
WHERE o.Status = 3
  AND o.DeliveredAt >= @From AND o.DeliveredAt < @To
GROUP BY s.SellerId, s.Name, p.Category";

                using (var cmd = new SqlCommand(sql, conn))
                {
                    cmd.CommandTimeout = CommandTimeoutSeconds;
                    cmd.Parameters.Add("@From", SqlDbType.DateTime).Value = from;
                    cmd.Parameters.Add("@To", SqlDbType.DateTime).Value = to;

                    using (var reader = cmd.ExecuteReader())
                    {
                        while (reader.Read())
                        {
                            var row = new SettlementRow();
                            row.SellerId = reader.GetInt32(0);
                            row.SellerName = reader.GetString(1);
                            row.Category = reader.IsDBNull(2) ? "미분류" : reader.GetString(2);
                            row.OrderCount = reader.GetInt32(3);
                            row.SalesAmount = reader.GetDecimal(4);
                            var rate = reader.IsDBNull(5) ? DefaultCommissionRate : reader.GetDecimal(5);
                            row.CommissionAmount = Math.Round(row.SalesAmount * rate, 0, MidpointRounding.AwayFromZero);
                            row.PayoutAmount = row.SalesAmount - row.CommissionAmount;
                            rows.Add(row);
                        }
                    }
                }

                if (rows.Count == 0)
                {
                    result.Success = true;
                    result.Report = BuildReport(from, rows, warnings);
                    return result;
                }

                // 판매자별 합계가 최소 지급액 미만이면 이월
                foreach (var group in rows.GroupBy(r => r.SellerId))
                {
                    var total = group.Sum(r => r.PayoutAmount);
                    if (total < MinimumPayout)
                    {
                        foreach (var r in group)
                        {
                            r.Deferred = true;
                        }

                        warnings.Add(string.Format("판매자 {0}: 지급액 {1:N0}원이 최소 지급액 미만이라 이월합니다.", group.Key, total));
                    }
                }

                using (var tx = conn.BeginTransaction())
                {
                    try
                    {
                        foreach (var row in rows.Where(r => !r.Deferred))
                        {
                            using (var insert = new SqlCommand(@"
INSERT INTO dbo.Settlements (BusinessDate, SellerId, Category, OrderCount, SalesAmount, CommissionAmount, PayoutAmount, CreatedAt)
VALUES (@BusinessDate, @SellerId, @Category, @OrderCount, @SalesAmount, @CommissionAmount, @PayoutAmount, GETDATE())", conn, tx))
                            {
                                insert.Parameters.Add("@BusinessDate", SqlDbType.Date).Value = from;
                                insert.Parameters.Add("@SellerId", SqlDbType.Int).Value = row.SellerId;
                                insert.Parameters.Add("@Category", SqlDbType.NVarChar, 50).Value = row.Category;
                                insert.Parameters.Add("@OrderCount", SqlDbType.Int).Value = row.OrderCount;
                                insert.Parameters.Add("@SalesAmount", SqlDbType.Decimal).Value = row.SalesAmount;
                                insert.Parameters.Add("@CommissionAmount", SqlDbType.Decimal).Value = row.CommissionAmount;
                                insert.Parameters.Add("@PayoutAmount", SqlDbType.Decimal).Value = row.PayoutAmount;
                                insert.ExecuteNonQuery();
                            }
                        }

                        tx.Commit();
                        result.Success = true;
                    }
                    catch (SqlException ex)
                    {
                        tx.Rollback();
                        warnings.Add("정산 저장 실패: " + ex.Message);
                        result.Success = false;
                    }
                }
            }

            result.Report = BuildReport(from, rows, warnings);
            return result;
        }

        /// <summary>
        /// 정산 결과 보고서(텍스트)를 만든다. 운영팀 메일 본문으로 그대로 쓰인다.
        /// </summary>
        public string BuildReport(DateTime businessDate, IList<SettlementRow> rows, IList<string> warnings)
        {
            var culture = CultureInfo.GetCultureInfo("ko-KR");
            var sb = new StringBuilder();
            var line = new string('=', 72);
            var thin = new string('-', 72);

            // 1. 머리말
            sb.AppendLine(line);
            sb.AppendLine("  주문 정산 보고서");
            sb.AppendLine("  정산일: " + businessDate.ToString("yyyy-MM-dd (ddd)", culture));
            sb.AppendLine("  작성 시각: " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", culture));
            sb.AppendLine(line);
            sb.AppendLine();

            if (rows == null || rows.Count == 0)
            {
                sb.AppendLine("  정산 대상 주문이 없습니다.");
                sb.AppendLine();
                if (warnings != null && warnings.Count > 0)
                {
                    sb.AppendLine("  [경고]");
                    foreach (var w in warnings)
                    {
                        sb.AppendLine("  - " + w);
                    }
                }

                sb.AppendLine(line);
                return sb.ToString();
            }

            // 2. 요약
            var paid = rows.Where(r => !r.Deferred).ToList();
            var deferred = rows.Where(r => r.Deferred).ToList();
            var totalOrders = rows.Sum(r => r.OrderCount);
            var totalSales = rows.Sum(r => r.SalesAmount);
            var totalCommission = paid.Sum(r => r.CommissionAmount);
            var totalPayout = paid.Sum(r => r.PayoutAmount);
            var deferredPayout = deferred.Sum(r => r.PayoutAmount);
            var sellerCount = rows.Select(r => r.SellerId).Distinct().Count();
            var paidSellerCount = paid.Select(r => r.SellerId).Distinct().Count();

            sb.AppendLine("  [요약]");
            sb.AppendLine(string.Format(culture, "  주문 수        : {0,12:N0} 건", totalOrders));
            sb.AppendLine(string.Format(culture, "  판매 금액      : {0,12:N0} 원", totalSales));
            sb.AppendLine(string.Format(culture, "  수수료         : {0,12:N0} 원", totalCommission));
            sb.AppendLine(string.Format(culture, "  지급 금액      : {0,12:N0} 원", totalPayout));
            sb.AppendLine(string.Format(culture, "  이월 금액      : {0,12:N0} 원", deferredPayout));
            sb.AppendLine(string.Format(culture, "  판매자         : {0,12:N0} 명 (지급 {1:N0} / 이월 {2:N0})", sellerCount, paidSellerCount, sellerCount - paidSellerCount));

            if (totalSales > 0)
            {
                var effectiveRate = totalCommission / totalSales;
                sb.AppendLine(string.Format(culture, "  평균 수수료율  : {0,12:P2}", effectiveRate));
            }

            sb.AppendLine();

            // 3. 판매자별 내역
            sb.AppendLine("  [판매자별 내역]");
            sb.AppendLine(thin);
            sb.AppendLine(string.Format(culture, "  {0,-8} {1,-20} {2,6} {3,14} {4,12} {5,14}", "판매자", "이름", "주문", "판매액", "수수료", "지급액"));
            sb.AppendLine(thin);

            foreach (var group in rows.GroupBy(r => new { r.SellerId, r.SellerName }).OrderByDescending(g => g.Sum(r => r.SalesAmount)))
            {
                var orders = group.Sum(r => r.OrderCount);
                var sales = group.Sum(r => r.SalesAmount);
                var commission = group.Sum(r => r.CommissionAmount);
                var payout = group.Sum(r => r.PayoutAmount);
                var name = group.Key.SellerName ?? string.Empty;
                if (name.Length > 20)
                {
                    name = name.Substring(0, 19) + "…";
                }

                var mark = group.Any(r => r.Deferred) ? " (이월)" : string.Empty;
                sb.AppendLine(string.Format(
                    culture,
                    "  {0,-8} {1,-20} {2,6:N0} {3,14:N0} {4,12:N0} {5,14:N0}{6}",
                    group.Key.SellerId,
                    name,
                    orders,
                    sales,
                    commission,
                    payout,
                    mark));
            }

            sb.AppendLine(thin);
            sb.AppendLine(string.Format(
                culture,
                "  {0,-29} {1,6:N0} {2,14:N0} {3,12:N0} {4,14:N0}",
                "합계",
                totalOrders,
                totalSales,
                rows.Sum(r => r.CommissionAmount),
                rows.Sum(r => r.PayoutAmount)));
            sb.AppendLine();

            // 4. 카테고리별 내역
            sb.AppendLine("  [카테고리별 내역]");
            sb.AppendLine(thin);
            sb.AppendLine(string.Format(culture, "  {0,-20} {1,8} {2,16} {3,10}", "카테고리", "주문", "판매액", "비중"));
            sb.AppendLine(thin);

            foreach (var group in rows.GroupBy(r => r.Category).OrderByDescending(g => g.Sum(r => r.SalesAmount)))
            {
                var sales = group.Sum(r => r.SalesAmount);
                var share = totalSales > 0 ? sales / totalSales : 0m;
                sb.AppendLine(string.Format(
                    culture,
                    "  {0,-20} {1,8:N0} {2,16:N0} {3,10:P1}",
                    group.Key,
                    group.Sum(r => r.OrderCount),
                    sales,
                    share));
            }

            sb.AppendLine(thin);
            sb.AppendLine();

            // 5. 상위 판매자
            sb.AppendLine("  [판매액 상위 5]");
            var rank = 1;
            foreach (var top in rows
                .GroupBy(r => new { r.SellerId, r.SellerName })
                .Select(g => new { g.Key.SellerId, g.Key.SellerName, Sales = g.Sum(r => r.SalesAmount) })
                .OrderByDescending(x => x.Sales)
                .Take(5))
            {
                sb.AppendLine(string.Format(culture, "  {0}. {1} ({2}) - {3:N0}원", rank, top.SellerName, top.SellerId, top.Sales));
                rank++;
            }

            sb.AppendLine();

            // 6. 이월 내역
            if (deferred.Count > 0)
            {
                sb.AppendLine("  [이월 판매자]");
                foreach (var group in deferred.GroupBy(r => new { r.SellerId, r.SellerName }))
                {
                    sb.AppendLine(string.Format(
                        culture,
                        "  - {0} ({1}): {2:N0}원",
                        group.Key.SellerName,
                        group.Key.SellerId,
                        group.Sum(r => r.PayoutAmount)));
                }

                sb.AppendLine(string.Format(culture, "  이월 합계: {0:N0}원 (다음 정산일에 합산)", deferredPayout));
                sb.AppendLine();
            }

            // 7. 이상 징후 점검
            var anomalies = new List<string>();
            foreach (var row in rows)
            {
                if (row.SalesAmount <= 0)
                {
                    anomalies.Add(string.Format(culture, "판매자 {0} / {1}: 판매액이 0 이하입니다.", row.SellerId, row.Category));
                }

                if (row.CommissionAmount > row.SalesAmount)
                {
                    anomalies.Add(string.Format(culture, "판매자 {0} / {1}: 수수료가 판매액보다 큽니다.", row.SellerId, row.Category));
                }

                if (row.OrderCount > 0 && row.SalesAmount / row.OrderCount > 5000000m)
                {
                    anomalies.Add(string.Format(culture, "판매자 {0} / {1}: 주문당 평균 판매액이 500만원을 넘습니다.", row.SellerId, row.Category));
                }
            }

            if (anomalies.Count > 0)
            {
                sb.AppendLine("  [이상 징후]");
                foreach (var a in anomalies)
                {
                    sb.AppendLine("  ! " + a);
                }

                sb.AppendLine();
            }

            // 8. 경고
            if (warnings != null && warnings.Count > 0)
            {
                sb.AppendLine("  [경고]");
                foreach (var w in warnings)
                {
                    sb.AppendLine("  - " + w);
                }

                sb.AppendLine();
            }

            // 9. 맺음말
            sb.AppendLine(line);
            sb.AppendLine("  본 보고서는 정산 배치(Shop.Batch)가 자동으로 작성했습니다.");
            sb.AppendLine("  문의: 정산운영팀 (내선 1234)");
            sb.AppendLine(line);

            return sb.ToString();
        }
    }

    /// <summary>정산 실행 결과</summary>
    public class SettlementResult
    {
        public bool Success { get; set; }
        public string Report { get; set; }
    }

    /// <summary>판매자·카테고리별 정산 행</summary>
    public class SettlementRow
    {
        public int SellerId { get; set; }
        public string SellerName { get; set; }
        public string Category { get; set; }
        public int OrderCount { get; set; }
        public decimal SalesAmount { get; set; }
        public decimal CommissionAmount { get; set; }
        public decimal PayoutAmount { get; set; }
        public bool Deferred { get; set; }
    }
}
