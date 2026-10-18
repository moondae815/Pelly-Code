using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace PellyCoder.Core.Sql;

/// <summary>An object referenced by a batch. <see cref="Line"/> is relative to the batch and marks the first occurrence.</summary>
public sealed record SqlObjectReference(SqlObjectName Name, int Line, int Column);

public sealed record SqlReferences(
    IReadOnlyList<SqlObjectReference> Objects,
    IReadOnlySet<string> Columns,
    bool HasDynamicSql,
    bool UsesTempTables,
    IReadOnlyList<string> SkippedMultiPart);

/// <summary>Collects the objects, columns, dynamic SQL and temp-table use of one parsed batch.</summary>
public static class SqlReferenceCollector
{
    public static SqlReferences Collect(string batchText, SqlObjectName target, int compatLevel, bool quotedIdentifier)
    {
        ArgumentNullException.ThrowIfNull(batchText);
        ArgumentNullException.ThrowIfNull(target);

        var parser = SqlParsers.Create(compatLevel, quotedIdentifier);
        TSqlFragment fragment;
        IList<ParseError> errors;
        using (var reader = new StringReader(batchText))
            fragment = parser.Parse(reader, out errors);
        if (errors.Count > 0)
            throw new ArgumentException($"Batch does not parse: {errors[0].Message} (line {errors[0].Line})", nameof(batchText));

        var ranges = new StatementRanges();
        fragment.Accept(ranges);
        var visitor = new Visitor(batchText, target, ranges);
        fragment.Accept(visitor);
        return visitor.Build();
    }

    /// <summary>Start/length of every statement, so a fragment can be assigned to its nearest enclosing statement.</summary>
    private sealed class StatementRanges : TSqlFragmentVisitor
    {
        private readonly List<(int Start, int End)> _ranges = [];

        public override void Visit(TSqlFragment node)
        {
            if (node is TSqlStatement && node.StartOffset >= 0)
                _ranges.Add((node.StartOffset, node.StartOffset + node.FragmentLength));
            base.Visit(node);
        }

        /// <summary>Start offset of the innermost statement containing <paramref name="offset"/>, or -1 outside any.</summary>
        public int StatementOf(int offset)
        {
            var best = -1;
            var bestLen = int.MaxValue;
            foreach (var (start, end) in _ranges)
            {
                if (offset >= start && offset < end && end - start < bestLen)
                {
                    best = start;
                    bestLen = end - start;
                }
            }
            return best;
        }
    }

    private sealed class Visitor(string text, SqlObjectName target, StatementRanges ranges) : TSqlFragmentVisitor
    {
        private sealed record Candidate(SqlObjectReference Reference, bool IsFunction, int Statement, int Offset);
        private sealed record AliasDecl(string Name, int Statement, int Offset);

        private readonly List<Candidate> _candidates = [];
        private readonly List<AliasDecl> _aliases = [];
        private readonly HashSet<string> _columns = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ctes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _skipped = [];
        private readonly List<(string Text, string First, int Statement)> _skippedFunctions = [];
        private bool _dynamic;
        private bool _temp;

        public SqlReferences Build()
        {
            // CTE names and table aliases are known only after the whole batch is visited (WITH is a late child
            // of SelectStatement, aliases may follow the UPDATE/DELETE target), so schema-less names are filtered here.
            // Aliases only hide names within their own statement, and a table never hides behind its own alias (M3a T13).
            bool AliasedByOther(string name, int statement, int ownOffset) =>
                _aliases.Any(a => a.Statement == statement && a.Offset != ownOffset && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            bool AliasInStatement(string name, int statement) =>
                _aliases.Any(a => a.Statement == statement && a.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

            var merged = new Dictionary<string, SqlObjectReference>(StringComparer.OrdinalIgnoreCase);
            foreach (var c in _candidates)
            {
                var r = c.Reference;
                if (r.Name.Schema is null && (_ctes.Contains(r.Name.Name) || AliasedByOther(r.Name.Name, c.Statement, c.Offset))) continue;
                if (c.IsFunction && r.Name.Schema is not null && AliasInStatement(r.Name.Schema, c.Statement)) continue;
                var key = (r.Name.Schema ?? "dbo") + "." + r.Name.Name;
                if (!merged.TryGetValue(key, out var old) || (r.Line, r.Column).CompareTo((old.Line, old.Column)) < 0)
                    merged[key] = r;
            }
            return new SqlReferences(
                merged.Values.OrderBy(o => o.Line).ThenBy(o => o.Column).ToList(),
                _columns, _dynamic, _temp,
                _skipped.Concat(_skippedFunctions.Where(f => !AliasInStatement(f.First, f.Statement)).Select(f => f.Text)).Distinct().ToList());
        }

        public override void Visit(TSqlFragment node)
        {
            if (node is TableReferenceWithAlias { Alias: not null } t) _aliases.Add(new AliasDecl(t.Alias.Value, ranges.StatementOf(t.StartOffset), t.StartOffset));
            // ALTER TABLE has several derived statement types; Visit(TSqlFragment) sees them all.
            if (node is AlterTableStatement a && IsTemp(a.SchemaObjectName?.BaseIdentifier?.Value)) _temp = true;
            base.Visit(node);
        }

        private static bool IsTemp(string? name) => name is not null && name.StartsWith('#');

        private void AddSchemaObject(SchemaObjectName? name, bool isProcedure = false)
        {
            if (name?.BaseIdentifier is null) return;
            if (name.Identifiers.Count > 2)
            {
                Skip(name);
                return;
            }
            var baseName = name.BaseIdentifier.Value;
            if (IsTemp(baseName)) { _temp = true; return; }
            var schema = name.SchemaIdentifier?.Value;
            if (schema is null)
            {
                if (baseName.Equals("inserted", StringComparison.OrdinalIgnoreCase)
                    || baseName.Equals("deleted", StringComparison.OrdinalIgnoreCase)) return;
                if (isProcedure && (baseName.StartsWith("sp_", StringComparison.OrdinalIgnoreCase)
                    || baseName.StartsWith("xp_", StringComparison.OrdinalIgnoreCase))) return;
            }
            Add(new SqlObjectName(schema, baseName), name);
        }

        private void Add(SqlObjectName name, TSqlFragment at, bool isFunction = false)
        {
            if (name.SameAs(target)) return;
            _candidates.Add(new Candidate(new SqlObjectReference(name, at.StartLine, at.StartColumn), isFunction, ranges.StatementOf(at.StartOffset), at.StartOffset));
        }

        private void Skip(TSqlFragment f) =>
            _skipped.Add(text.Substring(f.StartOffset, f.FragmentLength));

        public override void ExplicitVisit(CommonTableExpression node)
        {
            if (node.ExpressionName is not null) _ctes.Add(node.ExpressionName.Value);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(NamedTableReference node)
        {
            AddSchemaObject(node.SchemaObject);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SchemaObjectFunctionTableReference node)
        {
            var so = node.SchemaObject;
            if (so?.Identifiers.Count > 2)
            {
                // o.Xml.nodes(...) is a method call on an aliased column; only judged against aliases in Build().
                _skippedFunctions.Add((text.Substring(so.StartOffset, so.FragmentLength), so.Identifiers[0].Value, ranges.StatementOf(so.StartOffset)));
            }
            else
                AddSchemaObject(so);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateTableStatement node)
        {
            if (IsTemp(node.SchemaObjectName?.BaseIdentifier?.Value)) _temp = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(DropTableStatement node)
        {
            foreach (var o in node.Objects)
                if (IsTemp(o.BaseIdentifier?.Value)) _temp = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(TruncateTableStatement node)
        {
            if (IsTemp(node.TableName?.BaseIdentifier?.Value)) _temp = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(SelectStatement node)
        {
            if (IsTemp(node.Into?.BaseIdentifier?.Value)) _temp = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecutableProcedureReference node)
        {
            var name = node.ProcedureReference?.ProcedureReference?.Name;
            if (name is not null)
            {
                if (name.BaseIdentifier.Value.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase))
                    _dynamic = true;
                else
                    AddSchemaObject(name, isProcedure: true);
            }
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(ExecutableStringList node)
        {
            _dynamic = true;
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(FunctionCall node)
        {
            if (node.CallTarget is MultiPartIdentifierCallTarget mp && node.FunctionName is not null
                && !IsXmlMethod(node.FunctionName.Value))
            {
                var parts = mp.MultiPartIdentifier.Identifiers;
                // Two or more parts: a method call on a column (e.OrgNode.GetLevel()) - not an object.
                if (parts.Count == 1)
                {
                    if (node.FunctionName.Value.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase))
                        _dynamic = true;
                    else
                        Add(new SqlObjectName(parts[0].Value, node.FunctionName.Value), node, isFunction: true);
                }
            }
            base.ExplicitVisit(node);
        }

        private static bool IsXmlMethod(string name) =>
            name.Equals("value", StringComparison.OrdinalIgnoreCase)
            || name.Equals("query", StringComparison.OrdinalIgnoreCase)
            || name.Equals("exist", StringComparison.OrdinalIgnoreCase)
            || name.Equals("nodes", StringComparison.OrdinalIgnoreCase)
            || name.Equals("modify", StringComparison.OrdinalIgnoreCase);

        public override void ExplicitVisit(ColumnReferenceExpression node)
        {
            var ids = node.MultiPartIdentifier?.Identifiers;
            if (node.ColumnType == ColumnType.Regular && ids is { Count: > 0 })
                _columns.Add(ids[^1].Value);
            base.ExplicitVisit(node);
        }
    }
}
