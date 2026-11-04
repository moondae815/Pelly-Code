using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Abstractions;

/// <summary>Purpose-built SQL Server access. Connection-level failures surface as <see cref="SqlUnavailableException"/>.</summary>
public interface ISqlExecutor
{
    Task<SqlServerProbe> ProbeAsync(CancellationToken ct);

    /// <summary>The names for which <c>OBJECT_ID</c> is NULL, in input order.</summary>
    Task<IReadOnlyList<SqlObjectName>> FindMissingAsync(IReadOnlyList<SqlObjectName> names, CancellationToken ct);

    /// <summary>Schemas of the user tables and views among <paramref name="names"/>; other kinds are dropped silently.</summary>
    Task<IReadOnlyList<SqlTableSchema>> GetSchemasAsync(IReadOnlyList<SqlObjectName> names, CancellationToken ct);

    /// <summary>Objects referenced by <paramref name="obj"/> (same-database only). Empty when the object does not exist.</summary>
    Task<IReadOnlyList<SqlObjectName>> GetReferencedEntitiesAsync(SqlObjectName obj, CancellationToken ct);

    /// <summary>Compiles one batch under <c>SET NOEXEC ON</c> inside a rolled-back transaction; returns severity 11+ errors (Line is batch-relative).</summary>
    Task<IReadOnlyList<SqlCompileError>> CompileWithNoExecAsync(string batch, SqlSessionSettings settings, CancellationToken ct);

    /// <summary>
    /// Parameters and first result set of the stored procedures among <paramref name="names"/>, in input order. Names that are
    /// not procedures (or do not exist) are dropped.
    /// </summary>
    Task<IReadOnlyList<SqlProcedureSignature>> GetProcedureSignaturesAsync(IReadOnlyList<SqlObjectName> names, CancellationToken ct);
}

public sealed record SqlServerProbe(string Version, string Database, bool CanViewDefinition, string? LoginName = null);

public sealed record SqlColumnInfo(string Name, string TypeDisplay, bool Nullable, bool IsPrimaryKey);

public sealed record SqlParameterInfo(string Name, string Type, bool IsOutput);

/// <param name="FirstResultSet">Columns of the first result set; null when the server cannot describe it (temp tables, errors).</param>
public sealed record SqlProcedureSignature(SqlObjectName Name, IReadOnlyList<SqlParameterInfo> Parameters, IReadOnlyList<SqlColumnInfo>? FirstResultSet);

public sealed record SqlIndexInfo(string Name, IReadOnlyList<string> KeyColumns, bool IsUnique);

public sealed record SqlTableSchema(SqlObjectName Name, string ObjectType, IReadOnlyList<SqlColumnInfo> Columns, IReadOnlyList<SqlIndexInfo> Indexes);

/// <param name="Line">1-based line within the compiled batch.</param>
public sealed record SqlCompileError(int Number, int Line, string Message);

public sealed class SqlUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
