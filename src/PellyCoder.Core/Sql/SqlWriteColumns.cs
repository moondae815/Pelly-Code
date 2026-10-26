using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace PellyCoder.Core.Sql;

public enum SqlWriteKind { Insert, Update, Merge, Delete }

/// <summary>A table a module writes. <see cref="Columns"/> null means all columns; DELETE always has an empty list.</summary>
public sealed record SqlWrite(SqlObjectName Table, IReadOnlyList<string>? Columns, SqlWriteKind Kind);

/// <summary>Collects the (table, columns) written by INSERT, UPDATE, MERGE and DELETE statements of a module.</summary>
public static class SqlWriteColumns
{
    public static IReadOnlyList<SqlWrite> Collect(string definition, int compatLevel, bool quotedIdentifier)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var parser = SqlParsers.Create(compatLevel, quotedIdentifier);
        TSqlFragment? fragment;
        using (var reader = new StringReader(definition))
            fragment = parser.Parse(reader, out _);
        if (fragment is null) return [];

        var visitor = new Visitor();
        fragment.Accept(visitor);
        return visitor.Build();
    }

    private sealed class Visitor : TSqlFragmentVisitor
    {
        // Columns null = all columns. Insertion order is kept for stable output.
        private readonly List<(SqlObjectName Table, SqlWriteKind Kind, List<string>? Columns)> _writes = [];

        public IReadOnlyList<SqlWrite> Build()
        {
            var merged = new List<(SqlObjectName Table, SqlWriteKind Kind, List<string>? Columns)>();
            foreach (var (table, kind, cols) in _writes)
            {
                var i = merged.FindIndex(m => m.Kind == kind && m.Table.SameAs(table));
                if (i < 0) { merged.Add((table, kind, cols is null ? null : [.. cols])); continue; }
                var old = merged[i];
                if (old.Columns is null || cols is null) { merged[i] = (old.Table, kind, null); continue; }
                foreach (var c in cols)
                    if (!old.Columns.Contains(c, StringComparer.OrdinalIgnoreCase)) old.Columns.Add(c);
            }
            return merged.Select(m => new SqlWrite(m.Table, m.Columns, m.Kind)).ToList();
        }

        private Dictionary<string, QueryExpression> _ctes = new(StringComparer.OrdinalIgnoreCase);

        private void Record(TableReference? target, FromClause? from, SqlWriteKind kind, IEnumerable<string?>? columns, bool allColumns = false)
        {
            if (target is not NamedTableReference named) return;
            var so = Resolve(named.SchemaObject, from, 0);
            if (so?.BaseIdentifier is null || so.Identifiers.Count > 2) return;
            var baseName = so.BaseIdentifier.Value;
            if (baseName.StartsWith('#') || baseName.StartsWith('@')) return;
            var name = new SqlObjectName(so.SchemaIdentifier?.Value ?? "dbo", baseName);
            List<string>? list = allColumns ? null : [];
            if (list is not null && columns is not null)
                foreach (var c in columns)
                    if (c is not null && !list.Contains(c, StringComparer.OrdinalIgnoreCase)) list.Add(c);
            if (list is { Count: 0 } && kind == SqlWriteKind.Update) return; // SET @x = col writes no column
            _writes.Add((name, kind, list));
        }

        /// <summary>OUTPUT ... INTO table [(cols)] is an insert into that table.</summary>
        private void RecordOutputInto(DataModificationSpecification spec)
        {
            var into = spec.OutputIntoClause;
            if (into is null) return;
            var cols = into.IntoTableColumns?.Select(ColumnName).ToList();
            Record(into.IntoTable, null, SqlWriteKind.Insert, cols, allColumns: cols is null or { Count: 0 });
        }

        /// <summary>
        /// Resolves a write target to its base table: a one-part name is first a FROM alias (top level only), then a CTE
        /// over a single base table. Returns null when it cannot be resolved to a base table (derived table, variable, join CTE).
        /// </summary>
        private SchemaObjectName? Resolve(SchemaObjectName? so, FromClause? from, int depth)
        {
            if (so is null || depth > 8) return null;
            if (so.Identifiers.Count != 1) return so;
            var name = so.BaseIdentifier.Value;
            if (depth == 0 && from is not null)
            {
                var aliased = FindAlias(from.TableReferences, name);
                if (aliased is not null)
                    return aliased is NamedTableReference n ? Resolve(n.SchemaObject, null, depth + 1) : null;
            }
            if (_ctes.TryGetValue(name, out var body))
            {
                if (body is QuerySpecification { FromClause.TableReferences: [NamedTableReference only] })
                    return Resolve(only.SchemaObject, null, depth + 1);
                return null;
            }
            return so;
        }

        private static TableReference? FindAlias(IEnumerable<TableReference> refs, string alias)
        {
            foreach (var r in refs)
            {
                switch (r)
                {
                    case QualifiedJoin j:
                        if (FindAlias([j.FirstTableReference, j.SecondTableReference], alias) is { } f1) return f1;
                        break;
                    case UnqualifiedJoin j:
                        if (FindAlias([j.FirstTableReference, j.SecondTableReference], alias) is { } f2) return f2;
                        break;
                    case JoinParenthesisTableReference p:
                        if (p.Join is not null && FindAlias([p.Join], alias) is { } f3) return f3;
                        break;
                    case TableReferenceWithAlias { Alias: not null } t when t.Alias.Value.Equals(alias, StringComparison.OrdinalIgnoreCase):
                        return t;
                }
            }
            return null;
        }

        private void WithCtes(WithCtesAndXmlNamespaces? with, Action visit)
        {
            var saved = _ctes;
            _ctes = new Dictionary<string, QueryExpression>(saved, StringComparer.OrdinalIgnoreCase);
            foreach (var c in with?.CommonTableExpressions ?? [])
                if (c.ExpressionName is not null && c.QueryExpression is not null) _ctes[c.ExpressionName.Value] = c.QueryExpression;
            try { visit(); } finally { _ctes = saved; }
        }

        public override void ExplicitVisit(InsertStatement node) => WithCtes(node.WithCtesAndXmlNamespaces, () => base.ExplicitVisit(node));
        public override void ExplicitVisit(UpdateStatement node) => WithCtes(node.WithCtesAndXmlNamespaces, () => base.ExplicitVisit(node));
        public override void ExplicitVisit(DeleteStatement node) => WithCtes(node.WithCtesAndXmlNamespaces, () => base.ExplicitVisit(node));
        public override void ExplicitVisit(MergeStatement node) => WithCtes(node.WithCtesAndXmlNamespaces, () => base.ExplicitVisit(node));

        private static string? ColumnName(ColumnReferenceExpression? c) =>
            c?.MultiPartIdentifier?.Identifiers is { Count: > 0 } ids ? ids[^1].Value : null;

        public override void ExplicitVisit(InsertSpecification node)
        {
            var cols = node.Columns?.Select(ColumnName).ToList();
            Record(node.Target, null, SqlWriteKind.Insert, cols, allColumns: cols is null or { Count: 0 });
            RecordOutputInto(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateSpecification node)
        {
            var cols = node.SetClauses.OfType<AssignmentSetClause>().Select(s => ColumnName(s.Column)).ToList();
            Record(node.Target, node.FromClause, SqlWriteKind.Update, cols);
            RecordOutputInto(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteSpecification node)
        {
            Record(node.Target, node.FromClause, SqlWriteKind.Delete, null);
            RecordOutputInto(node);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(MergeSpecification node)
        {
            var cols = new List<string?>();
            var all = false;
            foreach (var action in node.ActionClauses.Select(a => a.Action))
            {
                switch (action)
                {
                    case UpdateMergeAction u:
                        cols.AddRange(u.SetClauses.OfType<AssignmentSetClause>().Select(s => ColumnName(s.Column)));
                        break;
                    case InsertMergeAction i:
                        if (i.Columns is null or { Count: 0 }) all = true;
                        else cols.AddRange(i.Columns.Select(ColumnName));
                        break;
                }
            }
            Record(node.Target, null, SqlWriteKind.Merge, cols, all);
            RecordOutputInto(node);
            base.ExplicitVisit(node);
        }
    }
}
