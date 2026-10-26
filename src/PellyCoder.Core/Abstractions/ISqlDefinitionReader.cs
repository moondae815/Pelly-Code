using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Abstractions;

/// <summary>One module definition read from a server's catalog (<c>sys.sql_modules</c>).</summary>
public sealed record SqlModuleDefinition(SqlObjectName Name, string TypeCode, string? Definition, bool UsesAnsiNulls, bool UsesQuotedIdentifier);
