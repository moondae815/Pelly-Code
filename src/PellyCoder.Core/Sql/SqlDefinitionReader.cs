using Dapper;
using Microsoft.Data.SqlClient;
using PellyCoder.Core.Abstractions;

namespace PellyCoder.Core.Sql;

/// <summary>
/// Reads definitions and dependency metadata from one server. Every statement is a read-only SELECT over catalog views or metadata
/// functions (<c>OBJECT_DEFINITION</c>, <c>OBJECT_ID</c>, <c>HAS_PERMS_BY_NAME</c>, <c>sys.dm_sql_referenced_entities</c>);
/// there is deliberately no SET NOEXEC, transaction, DDL or DML here, so a VIEW DEFINITION-only login suffices.
/// </summary>
public sealed class SqlDefinitionReader(string connectionString) : ISqlDefinitionReader
{
    public Task<SqlServerProbe> ProbeAsync(CancellationToken ct)
        => SqlCalls.RunAsync(connectionString, conn => SqlCalls.ProbeAsync(conn, ct), ct);

    public Task<SqlModuleDefinition?> GetDefinitionAsync(SqlObjectName name, CancellationToken ct)
        => SqlCalls.RunAsync(connectionString, async conn =>
        {
            var row = await conn.QuerySingleOrDefaultAsync<DefinitionRow>(Cmd(
                "SELECT SCHEMA_NAME(o.schema_id) AS [Schema], o.name AS Name, RTRIM(o.type) AS TypeCode, m.definition AS Definition, " +
                "m.uses_ansi_nulls AS UsesAnsiNulls, m.uses_quoted_identifier AS UsesQuotedIdentifier " +
                "FROM sys.objects o JOIN sys.sql_modules m ON m.object_id = o.object_id " +
                "WHERE o.object_id = OBJECT_ID(@name)", new { name = Quote(name) }, ct));
            return row is null
                ? null
                : new SqlModuleDefinition(new SqlObjectName(row.Schema, row.Name), row.TypeCode, row.Definition, row.UsesAnsiNulls, row.UsesQuotedIdentifier);
        }, ct);

    public Task<IReadOnlyList<SqlModuleRef>> GetReferencingModulesAsync(SqlObjectName name, CancellationToken ct)
        => SqlCalls.RunAsync<IReadOnlyList<SqlModuleRef>>(connectionString, async conn =>
        {
            var rows = await conn.QueryAsync<RefRow>(Cmd(
                "SELECT DISTINCT SCHEMA_NAME(o.schema_id) AS [Schema], o.name AS Name, RTRIM(o.type) AS TypeCode " +
                "FROM sys.sql_expression_dependencies d JOIN sys.objects o ON o.object_id = d.referencing_id " +
                "WHERE d.referenced_id = OBJECT_ID(@name) AND d.referencing_id <> d.referenced_id AND d.referencing_class = 1 " +
                "ORDER BY [Schema], Name", new { name = Quote(name) }, ct));
            return rows.Select(r => new SqlModuleRef(new SqlObjectName(r.Schema, r.Name), r.TypeCode)).ToList();
        }, ct);

    public Task<IReadOnlyList<SqlColumnUse>?> GetColumnUsesAsync(SqlObjectName module, CancellationToken ct)
        => SqlCalls.RunAsync<IReadOnlyList<SqlColumnUse>?>(connectionString, async conn =>
        {
            var quoted = Quote(module);
            // An object that is missing is as unknown to the caller as one that no longer compiles.
            if (await conn.ExecuteScalarAsync<int?>(Cmd("SELECT OBJECT_ID(@name)", new { name = quoted }, ct)) is null)
                return null;
            try
            {
                var rows = await conn.QueryAsync<UseRow>(Cmd(
                    "SELECT referenced_schema_name AS [Schema], referenced_entity_name AS Name, referenced_minor_name AS [Column], " +
                    "is_selected AS Selected, is_updated AS Updated, is_select_all AS SelectAll " +
                    "FROM sys.dm_sql_referenced_entities(@name, 'OBJECT') " +
                    "WHERE referenced_server_name IS NULL AND referenced_database_name IS NULL AND referenced_entity_name IS NOT NULL",
                    new { name = quoted }, ct));
                return rows.Select(r => new SqlColumnUse(new SqlObjectName(r.Schema, r.Name), r.Column, r.Selected, r.Updated, r.SelectAll)).ToList();
            }
            catch (SqlException ex) when (!SqlCalls.IsConnectionLevel(ex, conn) && !ct.IsCancellationRequested)
            {
                // The module no longer compiles (dropped column, Msg 207): the function fails with the connection intact.
                return null;
            }
        }, ct);

    public Task<IReadOnlyList<string>> GetColumnsAsync(SqlObjectName table, CancellationToken ct)
        => SqlCalls.RunAsync<IReadOnlyList<string>>(connectionString, async conn =>
        {
            var rows = await conn.QueryAsync<string>(Cmd(
                "SELECT c.name FROM sys.columns c WHERE c.object_id = OBJECT_ID(@name) ORDER BY c.column_id",
                new { name = Quote(table) }, ct));
            return rows.ToList();
        }, ct);

    private static CommandDefinition Cmd(string sql, object? parameters, CancellationToken ct)
        => SqlCalls.Cmd(sql, parameters, ct);

    /// <summary>
    /// The <c>OBJECT_ID</c> argument: always two-part, a missing schema meaning <c>dbo</c> as everywhere else in pelly
    /// (a one-part name would resolve in the login's default schema).
    /// </summary>
    internal static string Quote(SqlObjectName name)
    {
        static string Part(string p) => "[" + p.Replace("]", "]]", StringComparison.Ordinal) + "]";
        return $"{Part(name.Schema ?? "dbo")}.{Part(name.Name)}";
    }

    private sealed record DefinitionRow(string? Schema, string Name, string TypeCode, string? Definition, bool UsesAnsiNulls, bool UsesQuotedIdentifier);
    private sealed record RefRow(string? Schema, string Name, string TypeCode);
    private sealed record UseRow(string? Schema, string Name, string? Column, bool Selected, bool Updated, bool SelectAll);
}
