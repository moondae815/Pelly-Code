using System.IO.Abstractions;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Workspaces;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.Impact;

public class SqlImpactFinderDbTests
{
    [SqlServerFact]
    public async Task Finds_corpus_callers_and_column_readers()
    {
        var root = Directory.CreateTempSubdirectory("pelly-sqlimpact-").FullName;
        try
        {
            var ws = new Workspace(root, Path.Combine(root, ".pelly"));
            var db = new SqlReaderOpen(new SqlDefinitionReader(ProdDb.ConnectionString!), null, null);
            var seed = new SqlImpactSeed("T1", 1, SqlObjectName.Parse("dbo.usp_CancelOrder"));

            var result = await new SqlImpactFinder(new FileSystem(), 160).FindAsync(ws, [seed], [], db, SqlImpactBasis.Prod, CancellationToken.None);

            Assert.Equal(SqlImpactBasis.Prod, result.Basis);
            Assert.Null(result.Unavailable);
            Assert.Empty(result.NewObjectSeeds);
            var orders = Assert.Single(result.Tables, t => t.Table.SameAs(SqlObjectName.Parse("dbo.Orders")));
            Assert.Contains("Status", orders.WrittenColumns, StringComparer.OrdinalIgnoreCase);
            Assert.True(orders.Modules >= 1);
            Assert.Contains("T1|dbo.Orders", result.FullLists.Keys);
            // usp_GetOrderDetail (and other corpus readers) select dbo.Orders.Status, which usp_CancelOrder updates.
            Assert.Contains(result.Targets, t => t.TargetKind == ImpactTargetKind.SqlObject && t.Reason == SqlImpactFinder.SameColumnReason);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
