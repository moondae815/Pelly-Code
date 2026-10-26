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

        private void Record(TableReference? target, FromClause? from, SqlWriteKind kind, IEnumerable<string?>? columns, bool allColumns = false)
        {
            if (target is not NamedTableReference named) return;
            var so = ResolveAlias(named, from);
            if (so?.BaseIdentifier is null || so.Identifiers.Count > 2) return;
            var baseName = so.BaseIdentifier.Value;
            if (baseName.StartsWith('#') || baseName.StartsWith('@')) return;
            var name = new SqlObjectName(so.SchemaIdentifier?.Value ?? "dbo", baseName);
            List<string>? list = allColumns ? null : [];
            if (list is not null && columns is not null)
                foreach (var c in columns)
                    if (c is not null && !list.Contains(c, StringComparer.OrdinalIgnoreCase)) list.Add(c);
            _writes.Add((name, kind, list));
        }

        /// <summary>UPDATE o ... FROM dbo.Orders o: a one-part target equal to a FROM alias is that aliased table.</summary>
        private static SchemaObjectName? ResolveAlias(NamedTableReference target, FromClause? from)
        {
            var so = target.SchemaObject;
            if (from is not null && so?.Identifiers.Count == 1)
            {
                var finder = new AliasFinder(so.BaseIdentifier.Value);
                from.Accept(finder);
                if (finder.Found is not null) return finder.Found;
            }
            return so;
        }

        private sealed class AliasFinder(string alias) : TSqlFragmentVisitor
        {
            public SchemaObjectName? Found { get; private set; }

            public override void ExplicitVisit(NamedTableReference node)
            {
                if (Found is null && node.Alias is not null && node.Alias.Value.Equals(alias, StringComparison.OrdinalIgnoreCase))
                    Found = node.SchemaObject;
                base.ExplicitVisit(node);
            }
        }

        private static string? ColumnName(ColumnReferenceExpression? c) =>
            c?.MultiPartIdentifier?.Identifiers is { Count: > 0 } ids ? ids[^1].Value : null;

        public override void ExplicitVisit(InsertSpecification node)
        {
            var cols = node.Columns?.Select(ColumnName).ToList();
            Record(node.Target, null, SqlWriteKind.Insert, cols, allColumns: cols is null or { Count: 0 });
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(UpdateSpecification node)
        {
            var cols = node.SetClauses.OfType<AssignmentSetClause>().Select(s => ColumnName(s.Column)).ToList();
            Record(node.Target, node.FromClause, SqlWriteKind.Update, cols);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DeleteSpecification node)
        {
            Record(node.Target, node.FromClause, SqlWriteKind.Delete, null);
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
            base.ExplicitVisit(node);
        }
    }
}
