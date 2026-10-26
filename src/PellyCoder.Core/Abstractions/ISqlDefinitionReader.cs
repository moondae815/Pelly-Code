using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Abstractions;

/// <summary>One module definition read from a server's catalog (<c>sys.sql_modules</c>).</summary>
public sealed record SqlModuleDefinition(SqlObjectName Name, string TypeCode, string? Definition, bool UsesAnsiNulls, bool UsesQuotedIdentifier);

/// <summary>A module that references another object (<c>sys.sql_expression_dependencies</c>); <paramref name="TypeCode"/> is <c>sys.objects.type</c> trimmed.</summary>
public sealed record SqlModuleRef(SqlObjectName Name, string TypeCode);

/// <summary>One referenced table/column of a module (<c>sys.dm_sql_referenced_entities</c>); <paramref name="Column"/> is null for the table-level row.</summary>
public sealed record SqlColumnUse(SqlObjectName Table, string? Column, bool Selected, bool Updated, bool SelectAll);

/// <summary>Read-only access to a server's definitions. Only catalog/metadata SELECTs are ever sent: no SET NOEXEC, transactions, DDL or DML.</summary>
public interface ISqlDefinitionReader
{
    Task<SqlServerProbe> ProbeAsync(CancellationToken ct);

    /// <summary>Null when the object does not exist. <c>TypeCode</c> is <c>sys.objects.type</c> with trailing blanks removed.</summary>
    Task<SqlModuleDefinition?> GetDefinitionAsync(SqlObjectName name, CancellationToken ct);

    /// <summary>Modules in the same database that reference <paramref name="name"/>, itself excluded.</summary>
    Task<IReadOnlyList<SqlModuleRef>> GetReferencingModulesAsync(SqlObjectName name, CancellationToken ct);

    /// <summary>Null when the dependency function fails (the module no longer compiles).</summary>
    Task<IReadOnlyList<SqlColumnUse>?> GetColumnUsesAsync(SqlObjectName module, CancellationToken ct);

    /// <summary>Column names in <c>column_id</c> order.</summary>
    Task<IReadOnlyList<string>> GetColumnsAsync(SqlObjectName table, CancellationToken ct);
}
