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

        var visitor = new Visitor(batchText, target);
        fragment.Accept(visitor);
        return visitor.Build();
    }

    private sealed class Visitor(string text, SqlObjectName target) : TSqlFragmentVisitor
    {
        private readonly Dictionary<string, SqlObjectReference> _objects = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _columns = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _ctes = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<string> _skipped = [];
        private bool _dynamic;
        private bool _temp;

        public SqlReferences Build() => new(
            // CTE names are known only after the whole statement is visited (WITH is a late child of SelectStatement).
            _objects.Values.Where(o => o.Name.Schema is not null || !_ctes.Contains(o.Name.Name)).OrderBy(o => o.Line).ThenBy(o => o.Column).ToList(),
            _columns, _dynamic, _temp, _skipped);

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

        private void Add(SqlObjectName name, TSqlFragment at)
        {
            if (name.SameAs(target)) return;
            var key = (name.Schema ?? "dbo") + "." + name.Name;
            if (_objects.ContainsKey(key)) return;
            _objects[key] = new SqlObjectReference(name, at.StartLine, at.StartColumn);
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
            AddSchemaObject(node.SchemaObject);
            base.ExplicitVisit(node);
        }

        public override void ExplicitVisit(CreateTableStatement node)
        {
            if (IsTemp(node.SchemaObjectName?.BaseIdentifier?.Value)) _temp = true;
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
                if (parts.Count == 1)
                {
                    if (node.FunctionName.Value.Equals("sp_executesql", StringComparison.OrdinalIgnoreCase))
                        _dynamic = true;
                    else
                        Add(new SqlObjectName(parts[0].Value, node.FunctionName.Value), node);
                }
                else if (parts.Count > 1)
                {
                    Skip(node);
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
