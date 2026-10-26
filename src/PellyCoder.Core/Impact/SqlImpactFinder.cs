using System.IO.Abstractions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Impact;

/// <summary>
/// SQL impact search (design 7.3): ① modules referencing a seed object, ② C# members naming it in a string, ③ modules
/// reading the columns the seed currently writes, capped per table with a summary. Only catalog reads go to the server.
/// </summary>
public sealed class SqlImpactFinder(IFileSystem fs, int compatLevel) : ISqlImpactFinder
{
    public const int MaxWarningsPerTable = 10;

    public const string ExecReason = "EXEC";
    public const string ReferenceReason = "참조";
    public const string SameColumnReason = "같은 컬럼 읽음";

    private const string NoConnection = "DB 연결 없음";

    public async Task<SqlImpactResult> FindAsync(Workspace ws, IReadOnlyList<SqlImpactSeed> seeds, SqlReaderOpen db, SqlImpactBasis basis, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(ws);
        ArgumentNullException.ThrowIfNull(seeds);
        ArgumentNullException.ThrowIfNull(db);

        var (strings, incomplete) = SqlNameStringFinder.Find(fs, ws, seeds.Select(s => new SqlNameSeed(s.TaskId, s.Object)).ToList());

        if (db.Problem is not null || db.Reader is null)
            return OnlyStrings(db.Problem ?? NoConnection);

        var search = new Search(db.Reader, compatLevel);
        try
        {
            foreach (var seed in seeds)
            {
                ct.ThrowIfCancellationRequested();
                await search.RunAsync(seed, ct).ConfigureAwait(false);
            }
        }
        catch (SqlUnavailableException e)
        {
            return OnlyStrings(e.Message);
        }

        return new SqlImpactResult(basis, null, [.. search.Targets, .. strings], search.Tables, search.NewObjectSeeds, incomplete, search.FullLists);

        SqlImpactResult OnlyStrings(string problem) =>
            new(null, problem, strings, [], [], incomplete, new Dictionary<string, IReadOnlyList<SqlImpactModuleRow>>());
    }

    /// <summary>Schema-qualified display with the dbo default (<c>dbo.usp_X</c>).</summary>
    public static string Display(SqlObjectName name)
    {
        ArgumentNullException.ThrowIfNull(name);
        return $"{name.Schema ?? "dbo"}.{name.Name}";
    }

    private sealed class Search(ISqlDefinitionReader reader, int compatLevel)
    {
        public List<ImpactTarget> Targets { get; } = [];
        public List<SqlTableShare> Tables { get; } = [];
        public List<string> NewObjectSeeds { get; } = [];
        public Dictionary<string, IReadOnlyList<SqlImpactModuleRow>> FullLists { get; } = new(StringComparer.Ordinal);

        private readonly Dictionary<string, IReadOnlyList<string>> _columns = new(StringComparer.OrdinalIgnoreCase);

        public async Task RunAsync(SqlImpactSeed seed, CancellationToken ct)
        {
            var def = await reader.GetDefinitionAsync(seed.Object, ct).ConfigureAwait(false);
            if (def is null)
            {
                NewObjectSeeds.Add(seed.TaskId);
                return;
            }

            // Modules already warned for this seed: a module found by ① and ③ warns once, with ①'s reason.
            var warned = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var module in await reader.GetReferencingModulesAsync(seed.Object, ct).ConfigureAwait(false))
            {
                var display = Display(module.Name);
                if (!warned.Add(display)) continue;
                var reason = await CallsAsync(module, seed.Object, ct).ConfigureAwait(false) ? ExecReason : ReferenceReason;
                Targets.Add(new ImpactTarget(seed.TaskId, "", display, reason, ImpactTargetKind.SqlObject));
            }

            if (def.Definition is null) return;   // encrypted or no permission: the written columns are unknown
            foreach (var (table, columns) in Written(SqlWriteColumns.Collect(def.Definition, compatLevel, def.UsesQuotedIdentifier)))
                await TableAsync(seed, table, columns ?? await ColumnsAsync(table, ct).ConfigureAwait(false), warned, ct).ConfigureAwait(false);
        }

        private async Task TableAsync(SqlImpactSeed seed, SqlObjectName table, IReadOnlyList<string> written, HashSet<string> warned, CancellationToken ct)
        {
            var candidates = (await reader.GetReferencingModulesAsync(table, ct).ConfigureAwait(false))
                .Where(m => !m.Name.SameAs(seed.Object))
                .DistinctBy(m => Display(m.Name), StringComparer.OrdinalIgnoreCase)
                .ToList();

            var rows = new List<SqlImpactModuleRow>();
            foreach (var candidate in candidates)
            {
                var uses = await reader.GetColumnUsesAsync(candidate.Name, ct).ConfigureAwait(false);
                if (uses is null)
                {
                    rows.Add(new SqlImpactModuleRow(candidate.Name, [], true));
                    continue;
                }

                var onTable = uses.Where(u => u.Table.SameAs(table)).ToList();
                var read = new HashSet<string>(onTable.Where(u => u.Selected && u.Column is not null).Select(u => u.Column!), StringComparer.OrdinalIgnoreCase);
                if (written.Count > 0 && onTable.Any(u => u.SelectAll))
                    read.UnionWith(await ColumnsAsync(table, ct).ConfigureAwait(false));
                rows.Add(new SqlImpactModuleRow(candidate.Name, written.Where(read.Contains).ToList(), false));
            }

            var readers = rows.Where(r => r.OverlapColumns.Count > 0)
                .OrderByDescending(r => r.OverlapColumns.Count)
                .ThenBy(r => Display(r.Module), StringComparer.Ordinal)
                .ToList();
            var shown = readers.Take(MaxWarningsPerTable).ToList();
            foreach (var row in shown)
            {
                var display = Display(row.Module);
                if (warned.Add(display))
                    Targets.Add(new ImpactTarget(seed.TaskId, "", display, SameColumnReason, ImpactTargetKind.SqlObject));
            }

            Tables.Add(new SqlTableShare(seed.TaskId, table, written, candidates.Count, shown.Count, readers.Count - shown.Count, rows.Count(r => r.Unverifiable)));
            FullLists[$"{seed.TaskId}|{Display(table)}"] = rows.OrderBy(r => Display(r.Module), StringComparer.Ordinal).ToList();
        }

        private async Task<IReadOnlyList<string>> ColumnsAsync(SqlObjectName table, CancellationToken ct)
        {
            var key = Display(table);
            if (!_columns.TryGetValue(key, out var columns))
                _columns[key] = columns = await reader.GetColumnsAsync(table, ct).ConfigureAwait(false);
            return columns;
        }

        /// <summary>Per table in first-write order: written columns, null for all, empty when the seed only deletes from it.</summary>
        private static IEnumerable<(SqlObjectName Table, IReadOnlyList<string>? Columns)> Written(IReadOnlyList<SqlWrite> writes)
        {
            foreach (var group in writes.GroupBy(w => Display(w.Table), StringComparer.OrdinalIgnoreCase))
            {
                var table = group.First().Table;
                var nonDelete = group.Where(w => w.Kind != SqlWriteKind.Delete).ToList();
                if (nonDelete.Count == 0)
                    yield return (table, []);
                else if (nonDelete.Any(w => w.Columns is null))
                    yield return (table, null);
                else
                    yield return (table, nonDelete.SelectMany(w => w.Columns!).Distinct(StringComparer.OrdinalIgnoreCase).ToList());
            }
        }

        /// <summary>True when a procedure's definition has EXEC/EXECUTE followed by the seed name.</summary>
        private async Task<bool> CallsAsync(SqlModuleRef module, SqlObjectName seed, CancellationToken ct)
        {
            if (!string.Equals(module.TypeCode, "P", StringComparison.OrdinalIgnoreCase)) return false;
            var def = await reader.GetDefinitionAsync(module.Name, ct).ConfigureAwait(false);
            return def?.Definition is { } text && HasExec(text, seed, compatLevel, def.UsesQuotedIdentifier);
        }
    }

    /// <summary>
    /// Token scan (comments and string literals never match): EXEC/EXECUTE, an optional <c>@rc =</c>, then a one- or
    /// two-part name equal to <paramref name="seed"/> (a one-part name means dbo). Three-part names never match.
    /// </summary>
    internal static bool HasExec(string definition, SqlObjectName seed, int compatLevel, bool quotedIdentifier)
    {
        IList<TSqlParserToken> raw;
        using (var r = new StringReader(definition))
            raw = SqlParsers.Create(compatLevel, quotedIdentifier).GetTokenStream(r, out _);
        var tokens = raw.Where(t => t.TokenType is not (TSqlTokenType.WhiteSpace or TSqlTokenType.SingleLineComment or TSqlTokenType.MultilineComment)).ToList();

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].TokenType is not (TSqlTokenType.Exec or TSqlTokenType.Execute)) continue;
            var j = i + 1;
            if (j + 1 < tokens.Count && tokens[j].TokenType == TSqlTokenType.Variable && tokens[j + 1].TokenType == TSqlTokenType.EqualsSign)
                j += 2;

            var parts = new List<string>();
            while (j < tokens.Count && tokens[j].TokenType is TSqlTokenType.Identifier or TSqlTokenType.QuotedIdentifier)
            {
                parts.Add(Unquote(tokens[j].Text));
                if (j + 1 < tokens.Count && tokens[j + 1].TokenType == TSqlTokenType.Dot) j += 2;
                else break;
            }

            var called = parts.Count switch
            {
                1 => new SqlObjectName(null, parts[0]),
                2 => new SqlObjectName(parts[0], parts[1]),
                _ => null,
            };
            if (called is not null && called.SameAs(seed)) return true;
        }
        return false;
    }

    private static string Unquote(string text)
    {
        if (text.Length < 2) return text;
        return (text[0], text[^1]) switch
        {
            ('[', ']') => text[1..^1].Replace("]]", "]", StringComparison.Ordinal),
            ('"', '"') => text[1..^1].Replace("\"\"", "\"", StringComparison.Ordinal),
            _ => text,
        };
    }
}
