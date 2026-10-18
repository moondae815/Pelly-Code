using Dapper;
using Microsoft.Data.SqlClient;
using PellyCoder.Core.Abstractions;

namespace PellyCoder.Core.Sql;

/// <summary>The only place that touches SqlClient/Dapper. Opens a pooled connection per call.</summary>
public sealed class SqlServerExecutor(string? connectionString) : ISqlExecutor
{
    public const int CommandTimeoutSeconds = 30;
    private const int TimeoutErrorNumber = -2;

    public async Task<SqlServerProbe> ProbeAsync(CancellationToken ct)
        => await RunAsync(async conn =>
        {
            var row = await conn.QuerySingleAsync<(string Version, string Database, int CanView)>(Cmd(
                "SELECT @@VERSION AS Version, DB_NAME() AS [Database], " +
                "CAST(CASE WHEN HAS_PERMS_BY_NAME(DB_NAME(), 'DATABASE', 'VIEW DEFINITION') = 1 THEN 1 ELSE 0 END AS int) AS CanView", ct: ct));
            var first = row.Version.Split('\n')[0].Trim();
            return new SqlServerProbe(first, row.Database, row.CanView == 1);
        }, ct);

    public async Task<IReadOnlyList<SqlObjectName>> FindMissingAsync(IReadOnlyList<SqlObjectName> names, CancellationToken ct)
        => await RunAsync<IReadOnlyList<SqlObjectName>>(async conn =>
        {
            var missing = new List<SqlObjectName>();
            foreach (var name in names)
            {
                var id = await conn.ExecuteScalarAsync<int?>(Cmd("SELECT OBJECT_ID(@name)", new { name = Quote(name) }, ct));
                if (id is null)
                    missing.Add(name);
            }
            return missing;
        }, ct);

    public async Task<IReadOnlyList<SqlTableSchema>> GetSchemasAsync(IReadOnlyList<SqlObjectName> names, CancellationToken ct)
        => await RunAsync<IReadOnlyList<SqlTableSchema>>(async conn =>
        {
            var result = new List<SqlTableSchema>();
            foreach (var name in names)
            {
                var obj = await conn.QuerySingleOrDefaultAsync<(int ObjectId, string Schema, string Name, string Type)?>(Cmd(
                    "SELECT o.object_id, SCHEMA_NAME(o.schema_id), o.name, RTRIM(o.type) FROM sys.objects o " +
                    "WHERE o.object_id = OBJECT_ID(@name) AND o.type IN ('U', 'V')", new { name = Quote(name) }, ct));
                if (obj is not { } o)
                    continue;

                var columns = (await conn.QueryAsync<ColumnRow>(Cmd(
                    "SELECT c.name AS Name, t.name AS TypeName, c.max_length AS MaxLength, c.precision AS [Precision], c.scale AS Scale, " +
                    "c.is_nullable AS IsNullable, " +
                    "CAST(CASE WHEN EXISTS (SELECT 1 FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
                    "WHERE i.object_id = c.object_id AND i.is_primary_key = 1 AND ic.column_id = c.column_id) THEN 1 ELSE 0 END AS bit) AS IsPk " +
                    "FROM sys.columns c JOIN sys.types t ON t.user_type_id = c.user_type_id " +
                    "WHERE c.object_id = @id ORDER BY c.column_id", new { id = o.ObjectId }, ct)))
                    .Select(c => new SqlColumnInfo(c.Name, TypeDisplay(c), c.IsNullable, c.IsPk))
                    .ToList();

                var indexRows = await conn.QueryAsync<IndexRow>(Cmd(
                    "SELECT i.name AS Name, i.is_unique AS IsUnique, ic.key_ordinal AS Ordinal, c.name AS [Column] " +
                    "FROM sys.indexes i JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id " +
                    "JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id " +
                    "WHERE i.object_id = @id AND i.is_primary_key = 0 AND i.type > 0 AND ic.key_ordinal > 0 " +
                    "ORDER BY i.index_id, ic.key_ordinal", new { id = o.ObjectId }, ct));
                var indexes = indexRows
                    .GroupBy(r => r.Name)
                    .Select(g => new SqlIndexInfo(g.Key, g.Select(r => r.Column).ToList(), g.First().IsUnique))
                    .ToList();

                result.Add(new SqlTableSchema(new SqlObjectName(o.Schema, o.Name), o.Type == "V" ? "VIEW" : "TABLE", columns, indexes));
            }
            return result;
        }, ct);

    public async Task<IReadOnlyList<SqlObjectName>> GetReferencedEntitiesAsync(SqlObjectName obj, CancellationToken ct)
        => await RunAsync<IReadOnlyList<SqlObjectName>>(async conn =>
        {
            var name = Quote(obj);
            if (await conn.ExecuteScalarAsync<int?>(Cmd("SELECT OBJECT_ID(@name)", new { name }, ct)) is null)
                return [];
            try
            {
                var rows = await conn.QueryAsync<(string? Schema, string Name)>(Cmd(
                    "SELECT DISTINCT referenced_schema_name, referenced_entity_name FROM sys.dm_sql_referenced_entities(@name, 'OBJECT') " +
                    "WHERE referenced_minor_id = 0 AND referenced_server_name IS NULL AND referenced_database_name IS NULL " +
                    "AND referenced_entity_name IS NOT NULL", new { name }, ct));
                return rows.Select(r => new SqlObjectName(r.Schema, r.Name)).ToList();
            }
            catch (SqlException ex) when (!IsConnectionLevel(ex, conn) && !ct.IsCancellationRequested)
            {
                // A deployed object that no longer compiles (dropped column, Msg 207) makes the DMF fail with the connection intact.
                // The caller still has the references the collector found in the batch text.
                return [];
            }
        }, ct);

    public async Task<IReadOnlyList<SqlCompileError>> CompileWithNoExecAsync(string batch, SqlSessionSettings settings, CancellationToken ct)
        => await RunAsync<IReadOnlyList<SqlCompileError>>(async conn =>
        {
            var errors = new List<SqlCompileError>();
            await ExecAsync(conn, $"SET ANSI_NULLS {OnOff(settings.AnsiNulls)}; SET QUOTED_IDENTIFIER {OnOff(settings.QuotedIdentifier)};", ct);
            await ExecAsync(conn, "BEGIN TRAN;", ct);
            try
            {
                await ExecAsync(conn, "SET NOEXEC ON;", ct);
                try
                {
                    await ExecAsync(conn, batch, ct);
                }
                catch (SqlException ex) when (ex.Number != TimeoutErrorNumber && conn.State == System.Data.ConnectionState.Open && !ct.IsCancellationRequested)
                {
                    foreach (SqlError e in ex.Errors)
                    {
                        if (e.Class >= 11)
                            errors.Add(new SqlCompileError(e.Number, e.LineNumber, e.Message));
                    }
                }
            }
            finally
            {
                // Must run even when the target batch failed; CancellationToken.None so a cancelled call still cleans up.
                await TryExecAsync(conn, "SET NOEXEC OFF;");
                await TryExecAsync(conn, "IF @@TRANCOUNT > 0 ROLLBACK;");
            }
            return errors;
        }, ct);

    private async Task<T> RunAsync<T>(Func<SqlConnection, Task<T>> body, CancellationToken ct)
    {
        if (connectionString is null)
            throw new SqlUnavailableException("SQL 연결 문자열이 설정되지 않았습니다.");

        SqlConnection conn;
        try
        {
            conn = new SqlConnection(connectionString);
        }
        catch (ArgumentException)
        {
            throw new SqlUnavailableException($"SQL 연결 문자열을 해석할 수 없습니다: {SqlConnectionText.Mask(connectionString)}");
        }

        await using (conn)
        {
            try
            {
                await conn.OpenAsync(ct);
                return await body(conn);
            }
            catch (SqlException ex) when (IsConnectionLevel(ex, conn))
            {
                throw new SqlUnavailableException(
                    $"SQL Server에 연결할 수 없습니다 ({SqlConnectionText.Mask(connectionString)}): {ex.Message.Replace(connectionString, SqlConnectionText.Mask(connectionString))}", ex);
            }
        }
    }

    private static bool IsConnectionLevel(SqlException ex, SqlConnection conn)
        => conn.State != System.Data.ConnectionState.Open || ex.Number == TimeoutErrorNumber;

    private static CommandDefinition Cmd(string sql, object? parameters = null, CancellationToken ct = default)
        => new(sql, parameters, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct);

    private static Task ExecAsync(SqlConnection conn, string sql, CancellationToken ct)
        => conn.ExecuteAsync(Cmd(sql, ct: ct));

    private static async Task TryExecAsync(SqlConnection conn, string sql)
    {
        try { await conn.ExecuteAsync(Cmd(sql)); }
        catch (SqlException) { /* connection is gone; the server rolls back on disconnect */ }
        catch (InvalidOperationException) { }
    }

    private static string OnOff(bool on) => on ? "ON" : "OFF";

    private static string Quote(SqlObjectName name)
    {
        static string Part(string p) => "[" + p.Replace("]", "]]") + "]";
        return name.Schema is null ? Part(name.Name) : $"{Part(name.Schema)}.{Part(name.Name)}";
    }

    private static string TypeDisplay(ColumnRow c)
    {
        var t = c.TypeName;
        switch (t)
        {
            case "char" or "varchar" or "binary" or "varbinary":
                return c.MaxLength == -1 ? $"{t}(max)" : $"{t}({c.MaxLength})";
            case "nchar" or "nvarchar":
                return c.MaxLength == -1 ? $"{t}(max)" : $"{t}({c.MaxLength / 2})";
            case "decimal" or "numeric":
                return $"{t}({c.Precision},{c.Scale})";
            case "datetime2" or "time" or "datetimeoffset":
                return $"{t}({c.Scale})";
            default:
                return t;
        }
    }

    private sealed record ColumnRow(string Name, string TypeName, short MaxLength, byte Precision, byte Scale, bool IsNullable, bool IsPk);

    private sealed record IndexRow(string Name, bool IsUnique, byte Ordinal, string Column);
}
