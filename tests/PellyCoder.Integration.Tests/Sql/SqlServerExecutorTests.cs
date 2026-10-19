using Dapper;
using Microsoft.Data.SqlClient;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Sql;
using PellyCoder.Integration.Tests.Corpus;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.Sql;

public class SqlServerExecutorTests
{
    private const string CancelOrder = "dbo.usp_CancelOrder";

    private const string ValidAlter = """
        ALTER PROCEDURE dbo.usp_CancelOrder
            @OrderId int,
            @Reason  nvarchar(200)
        AS
        BEGIN
            SET NOCOUNT ON;
            DECLARE @Status tinyint;
            SELECT @Status = Status FROM dbo.Orders WHERE OrderId = @OrderId;
            IF @Status IN (2, 3)
            BEGIN
                RAISERROR(N'x', 16, 1);
                RETURN;
            END
            UPDATE dbo.Orders SET Status = 9, CancelledAt = SYSDATETIME(), CancelReason = @Reason WHERE OrderId = @OrderId;
        END
        """;

    private static SqlServerExecutor Executor() => new(TestEnv.SqlConnection);

    private static async Task<string?> DefinitionAsync(string name)
    {
        await using var conn = new SqlConnection(TestEnv.SqlConnection);
        return await conn.ExecuteScalarAsync<string?>("SELECT OBJECT_DEFINITION(OBJECT_ID(@name))", new { name });
    }

    private static async Task WithUnchangedDefinition(Func<Task> body)
    {
        var before = await DefinitionAsync(CancelOrder);
        Assert.NotNull(before);
        await body();
        Assert.Equal(before, await DefinitionAsync(CancelOrder));
    }

    [SqlServerFact]
    public async Task PreCancelledToken_OnMetadataCall_ThrowsOperationCanceled()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Executor().GetReferencedEntitiesAsync(SqlObjectName.Parse(CancelOrder), cts.Token));
    }

    [SqlServerFact]
    public async Task CancelDuringRunningCommand_ThrowsOperationCanceledQuickly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Executor().RunAsync(
            conn => conn.ExecuteAsync(new CommandDefinition("WAITFOR DELAY '00:00:05'", cancellationToken: cts.Token)), cts.Token));
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(3), $"took {sw.Elapsed}");
    }

    [SqlServerFact]
    public Task Probe_ReportsDatabaseAndViewDefinition() => WithUnchangedDefinition(async () =>
    {
        var probe = await Executor().ProbeAsync(CancellationToken.None);
        Assert.Equal("PellyCorpus", probe.Database);
        Assert.True(probe.CanViewDefinition);
        Assert.Equal("sa", probe.LoginName);
        Assert.StartsWith("Microsoft SQL Server", probe.Version);
        Assert.DoesNotContain('\n', probe.Version);
    });

    [SqlServerFact]
    public Task FindMissing_ReturnsOnlyUnknown() => WithUnchangedDefinition(async () =>
    {
        var missing = await Executor().FindMissingAsync(
            [SqlObjectName.Parse("dbo.Orders"), SqlObjectName.Parse("dbo.NoSuchTable"), SqlObjectName.Parse("Customers")],
            CancellationToken.None);
        Assert.Equal([SqlObjectName.Parse("dbo.NoSuchTable")], missing);
    });

    [SqlServerFact]
    public Task GetSchemas_OrdersHasPkAndTypes() => WithUnchangedDefinition(async () =>
    {
        var schemas = await Executor().GetSchemasAsync(
            [SqlObjectName.Parse("dbo.Orders"), SqlObjectName.Parse(CancelOrder), SqlObjectName.Parse("dbo.vw_ShippedOrders")],
            CancellationToken.None);

        Assert.Equal(2, schemas.Count);
        var orders = schemas.Single(s => s.Name.Name == "Orders");
        Assert.Equal("TABLE", orders.ObjectType);
        Assert.Equal(new SqlColumnInfo("OrderId", "int", false, true), orders.Columns[0]);
        Assert.Contains(new SqlColumnInfo("CancelReason", "nvarchar(200)", true, false), orders.Columns);
        Assert.Contains(new SqlColumnInfo("TotalAmount", "decimal(18,2)", false, false), orders.Columns);
        Assert.Contains(new SqlColumnInfo("OrderedAt", "datetime2(0)", false, false), orders.Columns);
        Assert.DoesNotContain(orders.Indexes, i => i.Name == "PK_Orders");
        var ix = orders.Indexes.Single(i => i.Name == "IX_Orders_Customer_OrderedAt");
        Assert.Equal(["CustomerId", "OrderedAt"], ix.KeyColumns);
        Assert.False(ix.IsUnique);
        Assert.Equal("VIEW", schemas.Single(s => s.Name.Name == "vw_ShippedOrders").ObjectType);
    });

    [SqlServerFact]
    public Task ReferencedEntities_OfCancelOrder_IncludeOrders() => WithUnchangedDefinition(async () =>
    {
        var refs = await Executor().GetReferencedEntitiesAsync(SqlObjectName.Parse(CancelOrder), CancellationToken.None);
        Assert.Contains(refs, r => r.SameAs(SqlObjectName.Parse("dbo.Orders")));
        Assert.Empty(await Executor().GetReferencedEntitiesAsync(SqlObjectName.Parse("dbo.NoSuchProc"), CancellationToken.None));
    });

    [SqlServerFact]
    public Task Compile_ValidAlter_NoErrors_DefinitionUnchanged() => WithUnchangedDefinition(async () =>
    {
        var errors = await Executor().CompileWithNoExecAsync(ValidAlter, SqlSessionSettings.Default, CancellationToken.None);
        Assert.Empty(errors);
    });

    [SqlServerFact]
    public Task Compile_MissingColumn_Is207_WithBatchLine() => WithUnchangedDefinition(async () =>
    {
        var batch = ValidAlter.Replace("CancelReason = @Reason", "CancelReasonX = @Reason");
        var errors = await Executor().CompileWithNoExecAsync(batch, SqlSessionSettings.Default, CancellationToken.None);
        var e = Assert.Single(errors, x => x.Number == 207);
        Assert.Equal(14, e.Line); // the UPDATE line, 1-based within the batch
        Assert.Contains("CancelReasonX", e.Message);
    });

    [SqlServerFact]
    public Task Compile_SyntaxError_Is102() => WithUnchangedDefinition(async () =>
    {
        var batch = ValidAlter.Replace("SELECT @Status", "SELEC @Status");
        var errors = await Executor().CompileWithNoExecAsync(batch, SqlSessionSettings.Default, CancellationToken.None);
        Assert.Contains(errors, x => x.Number == 102);
    });

    [SqlServerFact]
    public Task Compile_CreateOfExistingObject_HasNoErrors() => WithUnchangedDefinition(async () =>
    {
        var text = File.ReadAllText(CorpusPaths.Get("Sql/objects/usp_CancelOrder.sql")).TrimStart('﻿').Replace("\r\n", "\n");
        var script = SqlBatchSplitter.Split(text, 160);
        var batch = Assert.Single(script.ObjectBatches);
        Assert.StartsWith("CREATE PROCEDURE", batch.Text.TrimStart().Split('\n').First(l => !l.TrimStart().StartsWith("--")));
        var errors = await Executor().CompileWithNoExecAsync(batch.Text, script.SettingsBefore(batch.Index), CancellationToken.None);
        Assert.Empty(errors);
    });

    [SqlServerFact]
    public Task Compile_LeavesNoOpenTransaction() => WithUnchangedDefinition(async () =>
    {
        var executor = Executor();
        var bad = ValidAlter.Replace("CancelReason = @Reason", "CancelReasonX = @Reason");
        Assert.NotEmpty(await executor.CompileWithNoExecAsync(bad, SqlSessionSettings.Default, CancellationToken.None));
        Assert.Empty(await executor.CompileWithNoExecAsync(ValidAlter, SqlSessionSettings.Default, CancellationToken.None));
    });

    [SqlServerFact]
    public Task Compile_QuotedIdentifierOff_IsApplied() => WithUnchangedDefinition(async () =>
    {
        const string batch = "ALTER PROCEDURE dbo.usp_CancelOrder AS SELECT \"x\" AS v;";
        var executor = Executor();
        var off = await executor.CompileWithNoExecAsync(batch, new SqlSessionSettings(true, false), CancellationToken.None);
        Assert.Empty(off);
        var on = await executor.CompileWithNoExecAsync(batch, SqlSessionSettings.Default, CancellationToken.None);
        Assert.Contains(on, x => x.Number == 207);
    });

    [Fact]
    public async Task BadServer_ThrowsUnavailable_WithMaskedText()
    {
        var executor = new SqlServerExecutor("Server=127.0.0.1,1;Database=x;User Id=u;Password=secret;Connect Timeout=1");
        var ex = await Assert.ThrowsAsync<SqlUnavailableException>(() => executor.ProbeAsync(CancellationToken.None));
        Assert.DoesNotContain("secret", ex.Message);
        Assert.DoesNotContain("secret", ex.ToString());
    }

    [Fact]
    public async Task NullConnection_ThrowsUnavailable()
    {
        var executor = new SqlServerExecutor(null);
        await Assert.ThrowsAsync<SqlUnavailableException>(() => executor.ProbeAsync(CancellationToken.None));
        await Assert.ThrowsAsync<SqlUnavailableException>(() =>
            executor.CompileWithNoExecAsync("SELECT 1", SqlSessionSettings.Default, CancellationToken.None));
    }

    /// <summary>
    /// A deployed proc whose table lost a column makes sys.dm_sql_referenced_entities raise Msg 207 on an open connection.
    /// Uses tempdb so the PellyCorpus database never changes.
    /// </summary>
    [SqlServerFact]
    public async Task ReferencedEntities_OfProcWithDroppedColumn_ReturnsEmptyInsteadOfThrowing()
    {
        var csb = new SqlConnectionStringBuilder(TestEnv.SqlConnection) { InitialCatalog = "tempdb" };
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var table = $"pelly_t_{suffix}";
        var proc = $"pelly_p_{suffix}";
        await using var conn = new SqlConnection(csb.ConnectionString);
        await conn.OpenAsync();
        try
        {
            await conn.ExecuteAsync($"CREATE TABLE dbo.{table} (Id int, Gone int)");
            await conn.ExecuteAsync($"CREATE PROCEDURE dbo.{proc} AS SELECT Id, Gone FROM dbo.{table}");
            await conn.ExecuteAsync($"ALTER TABLE dbo.{table} DROP COLUMN Gone");

            var refs = await new SqlServerExecutor(csb.ConnectionString).GetReferencedEntitiesAsync(SqlObjectName.Parse($"dbo.{proc}"), CancellationToken.None);

            Assert.Empty(refs);
        }
        finally
        {
            await conn.ExecuteAsync($"DROP PROCEDURE IF EXISTS dbo.{proc}; DROP TABLE IF EXISTS dbo.{table};");
        }
    }
}
