using Dapper;
using Microsoft.Data.SqlClient;
using System.IO.Abstractions;
using PellyCoder.Core.Sql;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.Sql;

public class SqlPullRoundTripTests
{
    private static readonly SqlObjectName CancelOrder = SqlObjectName.Parse("dbo.usp_CancelOrder");

    [SqlServerFact]
    public async Task Pulled_file_reapplies_to_same_definition()
    {
        var ct = TestContext.Current.CancellationToken;
        var cs = ProdDb.ConnectionString!;
        var reader = new SqlDefinitionReader(cs);
        var before = await reader.GetDefinitionAsync(CancelOrder, ct);
        Assert.NotNull(before);
        var hashBefore = SqlDefinitionText.Hash(before.Definition!, 160);

        var fs = new FileSystem();
        var dir = fs.Path.Combine(fs.Path.GetTempPath(), "pelly-pull-" + Guid.NewGuid().ToString("N"));
        try
        {
            var endpoint = SqlConnectionText.Endpoint(cs);
            var items = await new SqlPullService(fs, TimeProvider.System, 160)
                .PullAsync(new SqlPullRequest(["dbo.usp_CancelOrder"], PullSource.Prod, dir, false), reader, endpoint, null, ct);
            var item = Assert.Single(items);
            Assert.Equal(PullItemStatus.Written, item.Status);

            // The test runs the pulled script itself (as sa): split on GO lines like SSMS.
            var batches = System.Text.RegularExpressions.Regex.Split(fs.File.ReadAllText(item.Path!), @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
                .Where(b => !string.IsNullOrWhiteSpace(b));
            await using var conn = new SqlConnection(cs);
            await conn.OpenAsync(ct);
            foreach (var batch in batches)
                await conn.ExecuteAsync(new CommandDefinition(batch, cancellationToken: ct));

            var after = await reader.GetDefinitionAsync(CancelOrder, ct);
            Assert.Equal(hashBefore, SqlDefinitionText.Hash(after!.Definition!, 160));
        }
        finally
        {
            if (fs.Directory.Exists(dir)) fs.Directory.Delete(dir, true);
        }
    }
}
