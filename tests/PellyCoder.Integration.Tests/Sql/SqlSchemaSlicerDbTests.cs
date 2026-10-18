using PellyCoder.Core.Llm;
using PellyCoder.Core.Sql;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.Sql;

public class SqlSchemaSlicerDbTests
{
    [SqlServerFact]
    public async Task CancelOrder_SchemaHasOrdersAndHistories()
    {
        var sql = new SqlServerExecutor(TestEnv.SqlConnection);
        var target = SqlObjectName.Parse("dbo.usp_CancelOrder");
        var batch = new SqlBatch(0, 1, 20, "ALTER PROCEDURE dbo.usp_CancelOrder AS SELECT 1", SqlObjectKind.Procedure, target, "AlterProcedure", []);
        var refs = new SqlReferences([], new HashSet<string>(StringComparer.OrdinalIgnoreCase), false, false, []);

        var r = await SqlSchemaSlicer.BuildAsync(batch, refs, sql, QwenTokenCounter.Shared, 100_000, 160, CancellationToken.None);

        var schema = r.Slice.Included.Single(p => p.Kind == "schema").Text;
        Assert.Contains("TABLE dbo.Orders (", schema);
        Assert.Contains("CancelReason nvarchar(200) NULL", schema);
        Assert.Contains("OrderHistories", schema);
    }
}
