using Dapper;
using Microsoft.Data.SqlClient;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.Corpus;

/// <summary>SQL 코퍼스가 PellyCorpus DB에 배포됐는지 확인한다 (eval/corpus/Sql/deploy.sh 이후).</summary>
public class SqlCorpusTests
{
    public static readonly string[] CorpusObjects =
    [
        "dbo.usp_CancelOrder", "dbo.usp_SearchOrders", "dbo.usp_GetOrderDetail", "dbo.fn_OrderTotal",
        "dbo.vw_ShippedOrders", "dbo.usp_SearchOrdersDynamic", "dbo.usp_BuildSettlement",
        "dbo.usp_SettleDaily", "dbo.usp_SettleMonthly",
    ];

    [SqlServerFact]
    public async Task AllCorpusObjects_Exist()
    {
        await using var conn = new SqlConnection(TestEnv.SqlConnection);
        var missing = new List<string>();
        foreach (var name in CorpusObjects)
        {
            if (await conn.ExecuteScalarAsync<int?>("SELECT OBJECT_ID(@name)", new { name }) is null)
                missing.Add(name);
        }

        Assert.Empty(missing);
    }

    [SqlServerFact]
    public async Task SeedData_HasShippedOrders()
    {
        await using var conn = new SqlConnection(TestEnv.SqlConnection);
        var shipped = await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM dbo.Orders WHERE Status = 2");
        Assert.True(shipped >= 3, $"Shipped 주문 {shipped}건");
    }
}
