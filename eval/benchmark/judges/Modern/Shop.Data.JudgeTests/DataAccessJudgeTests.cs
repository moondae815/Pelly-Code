using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Shop.Api.Data;
using Shop.Api.Models;
using Shop.Jobs;
using Xunit;

namespace Shop.Data.JudgeTests
{
    /// <summary>
    /// 데이터 접근 코드(Dapper)의 동작 판정. 코퍼스 소스를 가짜 Dapper·SqlConnection(<see cref="FakeDatabase"/>)에 묶어 실행하고,
    /// 보낸 SQL·매개변수와 돌려받은 결과를 본다. 테스트마다 원본에서는 실패하고 올바르게 수정한 뒤에만 통과해야 한다.
    /// </summary>
    public class DataAccessJudgeTests
    {
        // cs-modern-003: 사은품(IsGiftItem = 1)은 일반 상품 뒤. 같은 묶음 안은 원래 순서(OrderItemId)를 지킨다.
        // 가짜 DB가 SQL의 ORDER BY를 행에 적용하므로 SQL로 정렬하든 C#으로 정렬하든 같은 기준이다. 넣는 순서는 일부러 섞는다.
        [Fact]
        public async Task cs_modern_003_GiftItemsAfterRegularItems()
        {
            var db = FakeDatabase.Create();
            var stored = new[] { (3, true), (1, true), (4, false), (2, false), (5, false) }
                .Select(x => Row.Of(("OrderItemId", x.Item1), ("OrderId", 1), ("ProductId", 10 + x.Item1), ("ProductName", "상품" + x.Item1),
                    ("Quantity", 1), ("UnitPrice", 1000m), ("IsGiftItem", x.Item2), ("IsRefundable", true)))
                .ToList();
            db.OnQuery = c => c.Sql.IndexOf("OrderItems", StringComparison.OrdinalIgnoreCase) >= 0
                ? FakeOrderBy.Apply(c.Sql, stored)
                : Array.Empty<IDictionary<string, object>>();

            var items = await new OrderRepository(db.ConnectionString).GetItemsAsync(1);

            Assert.Equal(new[] { 2, 4, 5, 1, 3 }, items.Select(i => i.OrderItemId).ToArray());
        }

        // cs-modern-004: 공백뿐인 키워드는 키워드 조건(LIKE)을 넣지 않는다. 공백이 섞인 실제 키워드는 그대로 검색한다.
        [Fact]
        public async Task cs_modern_004_WhitespaceKeywordAddsNoCondition()
        {
            foreach (var keyword in new[] { "   ", "\t", "", null })
            {
                var sql = await SearchSqlAsync(new OrderSearchRequest { Keyword = keyword });
                Assert.True(sql.Sql.IndexOf("LIKE", StringComparison.OrdinalIgnoreCase) < 0, $"키워드 '{keyword}': LIKE 조건이 들어감");
            }

            var real = await SearchSqlAsync(new OrderSearchRequest { Keyword = " 머그 " });
            Assert.Contains("LIKE", real.Sql, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("머그", Convert.ToString(real["Keyword"]));
        }

        // cs-modern-011: page가 1보다 작으면 첫 페이지(OFFSET 0)로 본다. 1 이상은 기존 계산 그대로다.
        [Fact]
        public async Task cs_modern_011_PageBelowOneReadsFirstPage()
        {
            var cases = new (int Page, int PageSize, int Skip)[] { (0, 20, 0), (-3, 20, 0), (1, 20, 0), (3, 10, 20) };
            foreach (var (page, pageSize, skip) in cases)
            {
                var sql = await SearchSqlAsync(new OrderSearchRequest { CustomerId = 100, Page = page, PageSize = pageSize });
                Assert.True(skip == Convert.ToInt32(sql["Skip"]), $"page {page}: 기대 Skip {skip}, 실제 {sql["Skip"]}");
                Assert.Equal(pageSize, Convert.ToInt32(sql["Take"]));
            }
        }

        // cs-modern-008: 수수료율이 0 미만 또는 1 초과면 기본 수수료율(12%). 0과 1, 정상 값은 그대로 쓰고 NULL은 원래대로 기본값이다.
        [Fact]
        public void cs_modern_008_InvalidCommissionRateUsesDefault()
        {
            // 판매액 100,000원(총액 103,000 - 배송비 3,000)인 주문을 판매자마다 하나씩. 모두 최소 지급액 이상이라 지급 대상이다.
            var rates = new Dictionary<int, decimal?> { [1] = -0.1m, [2] = 1.5m, [3] = 0.05m, [4] = null, [5] = 0m, [6] = 1m };
            var expected = new Dictionary<int, decimal> { [1] = 12000m, [2] = 12000m, [3] = 5000m, [4] = 12000m, [5] = 0m, [6] = 100000m };

            var db = FakeDatabase.Create();
            db.OnScalar = _ => 0;
            db.OnQuery = c => c.Sql.IndexOf("FROM dbo.Orders", StringComparison.OrdinalIgnoreCase) >= 0
                ? rates.Select(r => Row.Of(("OrderId", 100 + r.Key), ("SellerId", r.Key), ("TotalAmount", 103000m), ("ShippingFee", 3000m),
                    ("DiscountAmount", 0m), ("CommissionRate", r.Value))).ToList()
                : new List<IDictionary<string, object>>();

            var ok = new DailySettlementJob(db.ConnectionString, new StringWriter()).Run(new DateTime(2026, 10, 1));

            Assert.True(ok);
            var settled = db.Commands
                .Where(c => c.Kind == "execute" && c.Sql.IndexOf("INSERT INTO dbo.Settlements", StringComparison.OrdinalIgnoreCase) >= 0)
                .ToDictionary(c => Convert.ToInt32(c["SellerId"]), c => Convert.ToDecimal(c["CommissionAmount"]));
            foreach (var pair in expected)
            {
                Assert.True(settled.ContainsKey(pair.Key), $"판매자 {pair.Key}(수수료율 {rates[pair.Key]?.ToString() ?? "NULL"})가 지급 정산되지 않음");
                Assert.True(pair.Value == settled[pair.Key], $"판매자 {pair.Key}(수수료율 {rates[pair.Key]?.ToString() ?? "NULL"}): 기대 수수료 {pair.Value}, 실제 {settled[pair.Key]}");
            }
        }

        /// <summary>저장소 검색을 실행하고 마지막으로 보낸 조회 명령을 돌려준다.</summary>
        private static async Task<FakeCommand> SearchSqlAsync(OrderSearchRequest request)
        {
            var db = FakeDatabase.Create();
            await new OrderRepository(db.ConnectionString).SearchAsync(request);
            return db.Commands.Last(c => c.Kind == "query");
        }
    }
}
