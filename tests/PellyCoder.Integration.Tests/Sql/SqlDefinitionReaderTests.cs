using Dapper;
using Microsoft.Data.SqlClient;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Sql;
using PellyCoder.Integration.Tests.Infrastructure;

namespace PellyCoder.Integration.Tests.Sql;

public class SqlDefinitionReaderTests
{
    private static SqlDefinitionReader Reader() => new(ProdDb.ConnectionString!);

    private static readonly SqlObjectName CancelOrder = SqlObjectName.Parse("dbo.usp_CancelOrder");
    private static readonly SqlObjectName Orders = SqlObjectName.Parse("dbo.Orders");

    [SqlServerFact]
    public async Task GetDefinition_returns_text_and_settings()
    {
        var def = await Reader().GetDefinitionAsync(CancelOrder, CancellationToken.None);

        Assert.NotNull(def);
        Assert.Equal("P", def.TypeCode);
        Assert.Contains("CREATE PROCEDURE", def.Definition);

        await using var conn = new SqlConnection(ProdDb.ConnectionString);
        var catalog = await conn.QuerySingleAsync<(bool Nulls, bool Quoted)>(
            "SELECT uses_ansi_nulls, uses_quoted_identifier FROM sys.sql_modules WHERE object_id = OBJECT_ID(N'dbo.usp_CancelOrder')");
        Assert.Equal(catalog.Nulls, def.UsesAnsiNulls);
        Assert.Equal(catalog.Quoted, def.UsesQuotedIdentifier);
    }

    [SqlServerFact]
    public async Task GetDefinition_missing_object_is_null()
        => Assert.Null(await Reader().GetDefinitionAsync(SqlObjectName.Parse("dbo.usp_DoesNotExist"), CancellationToken.None));

    [SqlServerFact]
    public async Task GetReferencingModules_finds_callers()
    {
        var modules = await Reader().GetReferencingModulesAsync(Orders, CancellationToken.None);

        Assert.Contains(modules, m => m.Name == CancelOrder && m.TypeCode == "P");
        Assert.DoesNotContain(modules, m => m.Name == Orders);
    }

    [SqlServerFact]
    public async Task GetColumnUses_reports_selected_columns()
    {
        var uses = await Reader().GetColumnUsesAsync(CancelOrder, CancellationToken.None);

        Assert.NotNull(uses);
        Assert.Contains(uses, u => u.Table == Orders && u.Column == "Status" && u.Selected);
    }

    [SqlServerFact]
    public async Task GetColumns_are_in_column_id_order()
    {
        var columns = await Reader().GetColumnsAsync(Orders, CancellationToken.None);

        Assert.Equal("OrderId", columns[0]);
        Assert.Contains("Status", columns);
    }

    [SqlServerFact]
    public async Task GetColumnUses_of_broken_module_is_null()
    {
        var suffix = Guid.NewGuid().ToString("N")[..8];
        var table = $"PellyBrokenT_{suffix}";
        var proc = $"usp_PellyBroken_{suffix}";
        await using var conn = new SqlConnection(ProdDb.ConnectionString);
        await conn.OpenAsync();
        try
        {
            await conn.ExecuteAsync($"CREATE TABLE dbo.{table} (A int NOT NULL, B int NOT NULL)");
            await conn.ExecuteAsync($"CREATE PROCEDURE dbo.{proc} AS SELECT B FROM dbo.{table}");
            // The procedure already exists, so dropping the column leaves a module that no longer compiles (Msg 207 from the DMF).
            await conn.ExecuteAsync($"ALTER TABLE dbo.{table} DROP COLUMN B");

            Assert.Null(await Reader().GetColumnUsesAsync(new SqlObjectName("dbo", proc), CancellationToken.None));
        }
        finally
        {
            await conn.ExecuteAsync($"DROP PROCEDURE IF EXISTS dbo.{proc}; DROP TABLE IF EXISTS dbo.{table};");
        }
    }

    [SqlServerFact]
    public async Task Reader_works_with_view_definition_only_login()
    {
        var login = $"pelly_vd_{Guid.NewGuid():N}"[..24];
        const string password = "Pelly_Vd_2026!x";
        await using var admin = new SqlConnection(ProdDb.ConnectionString);
        await admin.OpenAsync();
        try
        {
            await admin.ExecuteAsync($"CREATE LOGIN [{login}] WITH PASSWORD = '{password}', CHECK_POLICY = OFF");
            await admin.ExecuteAsync($"CREATE USER [{login}] FOR LOGIN [{login}]");
            await admin.ExecuteAsync($"GRANT CONNECT TO [{login}]; GRANT VIEW DEFINITION TO [{login}];");

            var cs = new SqlConnectionStringBuilder(ProdDb.ConnectionString!)
            {
                IntegratedSecurity = false,
                UserID = login,
                Password = password,
                Pooling = false,
            }.ConnectionString;
            var reader = new SqlDefinitionReader(cs);
            var ct = CancellationToken.None;

            var probe = await reader.ProbeAsync(ct);
            Assert.True(probe.CanViewDefinition);
            Assert.Equal(login, probe.LoginName);
            Assert.NotNull(await reader.GetDefinitionAsync(CancelOrder, ct));
            Assert.Contains(await reader.GetReferencingModulesAsync(Orders, ct), m => m.Name == CancelOrder);
            Assert.NotNull(await reader.GetColumnUsesAsync(CancelOrder, ct));
            Assert.Contains("Status", await reader.GetColumnsAsync(Orders, ct));
        }
        finally
        {
            await admin.ExecuteAsync($"DROP USER IF EXISTS [{login}]");
            await admin.ExecuteAsync($"USE master; DROP LOGIN [{login}]");
        }
    }
}
