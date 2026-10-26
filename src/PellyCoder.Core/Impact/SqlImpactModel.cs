using PellyCoder.Core.Sql;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Impact;

/// <summary>A spec <c>sql</c> task the SQL impact search starts from: the object the task changes.</summary>
public sealed record SqlImpactSeed(string TaskId, int Line, SqlObjectName Object);

/// <summary>Which database's dependency information the search used (design 7.2).</summary>
public enum SqlImpactBasis { Prod, Dev }

/// <summary>
/// Summary of one table a seed writes: <paramref name="Modules"/> other modules reference it, <paramref name="Warned"/> of
/// the ones reading a written column became warnings here (modules a sql task names, or ① already warned, are not counted),
/// <paramref name="Omitted"/> more were cut by the per-table cap and
/// <paramref name="Unverifiable"/> could not report their column uses. <paramref name="WrittenColumns"/> is empty for a DELETE-only table.
/// </summary>
public sealed record SqlTableShare(string SeedTaskId, SqlObjectName Table, IReadOnlyList<string> WrittenColumns, int Modules, int Warned, int Omitted, int Unverifiable);

/// <summary>One candidate module of a table share, warned or not (the full list written to a file).</summary>
public sealed record SqlImpactModuleRow(SqlObjectName Module, IReadOnlyList<string> OverlapColumns, bool Unverifiable);

/// <param name="Basis">The database used; null when no database could be read and only the C# string search ran.</param>
/// <param name="Unavailable">Why the database could not be read (Korean); null when it was.</param>
/// <param name="NewObjectSeeds">Task ids of seeds whose object is not on the server, so only the string search ran for them.</param>
/// <param name="Incomplete">True when the C# file walk was cut short.</param>
/// <param name="FullLists">Every candidate per seed and table, keyed <c>"&lt;task id&gt;|&lt;schema.table&gt;"</c>.</param>
public sealed record SqlImpactResult(SqlImpactBasis? Basis, string? Unavailable, IReadOnlyList<ImpactTarget> Targets,
    IReadOnlyList<SqlTableShare> Tables, IReadOnlyList<string> NewObjectSeeds, bool Incomplete,
    IReadOnlyDictionary<string, IReadOnlyList<SqlImpactModuleRow>> FullLists);

public interface ISqlImpactFinder
{
    /// <param name="covered">Every object a sql task of the spec names: the per-table cap of ③ skips them, they are never missing.</param>
    Task<SqlImpactResult> FindAsync(Workspace ws, IReadOnlyList<SqlImpactSeed> seeds, IReadOnlyCollection<SqlObjectName> covered, SqlReaderOpen db,
        SqlImpactBasis basis, CancellationToken ct);
}
