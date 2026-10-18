using PellyCoder.Core.Abstractions;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Sql;

/// <summary>Builds the budgeted prompt context around one SQL batch (design 8.6): the target plus the schema of what it touches.</summary>
public static class SqlSchemaSlicer
{
    private const int SchemaPriority = 1;

    public static async Task<SliceResult> BuildAsync(SqlBatch target, SqlReferences refs, ISqlExecutor sql, ITokenCounter tokens,
        int contextBudget, int compatLevel, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(refs);
        ArgumentNullException.ThrowIfNull(sql);
        ArgumentNullException.ThrowIfNull(tokens);

        var names = new List<SqlObjectName>();
        void Add(SqlObjectName n)
        {
            if (!names.Any(x => x.SameAs(n))) names.Add(n);
        }
        foreach (var o in refs.Objects) Add(o.Name);
        if (target.ObjectName is { } own)
            foreach (var n in await sql.GetReferencedEntitiesAsync(own, ct).ConfigureAwait(false)) Add(n);

        var fetched = names.Count == 0 ? [] : await sql.GetSchemasAsync(names, ct).ConfigureAwait(false);
        // Reference order, not the executor's order; a schema nobody asked for is not shown.
        var tables = names.Select(n => fetched.FirstOrDefault(s => s.Name.SameAs(n))).OfType<SqlTableSchema>().Select(s => new Entry(s)).ToList();

        var targetPart = new SlicePart("target", target.Text, tokens.Count(target.Text), int.MaxValue);
        var tooLarge = targetPart.Tokens > contextBudget;
        var regions = new List<EditableRegion> { new(new TextSpanInfo(target.StartLine, target.EndLine), "target") };
        var hint = $"T-SQL, SQL Server compatibility level {compatLevel}";

        var omitted = new List<SlicePart>();
        string Fits(out bool ok)
        {
            var text = Render(tables);
            ok = !tooLarge && targetPart.Tokens + (text.Length == 0 ? 0 : tokens.Count(text)) <= contextBudget;
            return text;
        }

        var schema = Fits(out var fits);
        if (!fits && !tooLarge)
        {
            // 1. every index line, last table first.
            var dropped = new List<string>();
            foreach (var t in Enumerable.Reverse(tables))
            {
                if (t.Schema.Indexes.Count == 0 || !t.ShowIndexes) continue;
                dropped.AddRange(IndexLines(t.Schema));
                t.ShowIndexes = false;
                schema = Fits(out fits);
                if (fits) break;
            }
            AddOmitted("schema-indexes", Enumerable.Reverse(dropped).ToList());

            // 2. columns that are neither primary key nor referenced, last table first.
            if (!fits)
            {
                dropped = [];
                foreach (var t in Enumerable.Reverse(tables))
                {
                    var unused = t.Schema.Columns.Where(c => !c.IsPrimaryKey && !refs.Columns.Contains(c.Name)).ToList();
                    // A table that would end up with no columns is left for stage 3, which removes it whole.
                    if (unused.Count == 0 || unused.Count == t.Schema.Columns.Count) continue;
                    t.Hidden = unused;
                    dropped.Add($"{t.Schema.ObjectType} {t.Schema.Name.Display}: {string.Join(", ", unused.Select(ColumnText))}");
                    schema = Fits(out fits);
                    if (fits) break;
                }
                AddOmitted("schema-columns", dropped);
            }

            // 3. whole table lines, last first.
            if (!fits)
            {
                dropped = [];
                foreach (var t in Enumerable.Reverse(tables))
                {
                    t.Removed = true;
                    dropped.Add(HeaderLine(t));
                    schema = Fits(out fits);
                    if (fits) break;
                }
                AddOmitted("schema-tables", dropped);
            }
        }
        else if (tooLarge && schema.Length > 0)
        {
            // The target alone busts the budget: nothing else may ride along.
            AddOmitted("schema", schema.Split('\n'));
            schema = "";
        }

        var included = new List<SlicePart>();
        if (schema.Length > 0) included.Add(new SlicePart("schema", schema, tokens.Count(schema), SchemaPriority));
        included.Add(targetPart);
        return new SliceResult(new Slice(included, omitted, hint), regions, tooLarge);

        void AddOmitted(string kind, IReadOnlyCollection<string> lines)
        {
            if (lines.Count == 0) return;
            var text = string.Join('\n', lines);
            omitted.Add(new SlicePart(kind, text, tokens.Count(text), SchemaPriority));
        }
    }

    private sealed class Entry(SqlTableSchema schema)
    {
        public SqlTableSchema Schema { get; } = schema;
        public bool ShowIndexes { get; set; } = true;
        public IReadOnlyList<SqlColumnInfo> Hidden { get; set; } = [];
        public bool Removed { get; set; }
    }

    private static string Render(List<Entry> tables)
    {
        var lines = new List<string>();
        foreach (var t in tables.Where(t => !t.Removed))
        {
            lines.Add(HeaderLine(t));
            if (t.ShowIndexes) lines.AddRange(IndexLines(t.Schema));
        }
        return string.Join('\n', lines);
    }

    private static string HeaderLine(Entry t)
    {
        var cols = t.Schema.Columns.Where(c => !t.Hidden.Contains(c)).Select(ColumnText);
        return $"{t.Schema.ObjectType} {t.Schema.Name.Display} ({string.Join(", ", cols)})";
    }

    private static string ColumnText(SqlColumnInfo c) =>
        $"{c.Name} {c.TypeDisplay} {(c.Nullable ? "NULL" : "NOT NULL")}{(c.IsPrimaryKey ? " PK" : "")}";

    private static IEnumerable<string> IndexLines(SqlTableSchema s) =>
        s.Indexes.Select(i => $"{(i.IsUnique ? "UNIQUE " : "")}INDEX {i.Name} ({string.Join(", ", i.KeyColumns)})");
}
