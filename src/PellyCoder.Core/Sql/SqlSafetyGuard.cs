using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;

namespace PellyCoder.Core.Sql;

/// <summary>
/// Outcome of <see cref="SqlSafetyGuard.Check"/>. A parse failure has <c>Ok=false</c>, <c>Reason=null</c>
/// and a non-empty <c>ParseErrors</c>; any other rejection has a non-null English <c>Reason</c>.
/// </summary>
public sealed record SqlSafetyResult(bool Ok, string? Reason, IReadOnlyList<SqlParseError> ParseErrors);

/// <summary>
/// Decides whether a patched SQL file may be sent to VERIFY (SET NOEXEC ON inside a rolled-back transaction).
/// Only top-level statements of the target batch could ever execute, so exactly one top-level statement is required;
/// forbidden constructs are searched in the whole batch, including the object body.
/// </summary>
public static partial class SqlSafetyGuard
{
    public static SqlSafetyResult Check(SqlScript original, int targetIndex, string patchedLf, int compatLevel)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(patchedLf);

        var patched = SqlBatchSplitter.Split(patchedLf, compatLevel);
        if (patched.Batches.Count != original.Batches.Count)
            return Reject("the edit added or removed a GO batch separator. Keep the whole object in one batch and do not add GO.");

        for (var i = 0; i < original.Batches.Count; i++)
        {
            if (i != targetIndex && !string.Equals(original.Batches[i].Text, patched.Batches[i].Text, StringComparison.Ordinal))
                return Reject("text outside the target object batch changed. Only the target object may change.");
        }

        var target = patched.Batches[targetIndex];
        if (target.ParseErrors.Count > 0)
            return new SqlSafetyResult(false, null, target.ParseErrors);

        var source = original.Batches[targetIndex];
        var settings = original.SettingsBefore(targetIndex);
        var statements = TopLevelStatements(Parse(target.Text, compatLevel, settings.QuotedIdentifier, out _));
        var sameShape = statements.Count == 1
            && target.StatementType == source.StatementType
            && target.ObjectName is not null
            && source.ObjectName is not null
            && target.ObjectName.SameAs(source.ObjectName);
        if (!sameShape)
        {
            var shown = source.StatementType is null ? "object" : StatementDisplay(source.StatementType);
            var name = source.ObjectName?.Display ?? "the original object";
            return Reject($"the target batch must contain exactly one {shown} statement for {name}.");
        }

        var forbidden = ForbiddenIn(target.Text, compatLevel, settings.QuotedIdentifier);
        if (forbidden.Count > 0)
            return Reject($"forbidden construct '{forbidden[0]}' in the target object. Remove it.");

        return new SqlSafetyResult(true, null, []);
    }

    /// <summary>Display names of forbidden constructs found anywhere in the batch, in the order they appear.</summary>
    public static IReadOnlyList<string> ForbiddenIn(SqlBatch batch, int compatLevel)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return ForbiddenIn(batch.Text, compatLevel, quotedIdentifier: true);
    }

    private static IReadOnlyList<string> ForbiddenIn(string text, int compatLevel, bool quotedIdentifier)
    {
        var fragment = Parse(text, compatLevel, quotedIdentifier, out _);
        var visitor = new ForbiddenVisitor();
        fragment?.Accept(visitor);
        return visitor.Found;
    }

    private static SqlSafetyResult Reject(string reason) => new(false, reason, []);

    private static TSqlFragment? Parse(string text, int compatLevel, bool quotedIdentifier, out IList<ParseError> errors)
    {
        var parser = SqlParsers.Create(compatLevel, quotedIdentifier);
        using var reader = new StringReader(text);
        return parser.Parse(reader, out errors);
    }

    private static List<TSqlStatement> TopLevelStatements(TSqlFragment? fragment) =>
        fragment is TSqlScript script ? script.Batches.SelectMany(b => b.Statements).ToList() : [];

    /// <summary>CreateOrAlterFunctionStatement becomes CREATE OR ALTER FUNCTION.</summary>
    private static string StatementDisplay(string statementType) =>
        string.Join(' ', PascalWord().Matches(statementType.EndsWith("Statement", StringComparison.Ordinal)
            ? statementType[..^"Statement".Length]
            : statementType).Select(m => m.Value.ToUpperInvariant()));

    [GeneratedRegex("[A-Z][a-z0-9]*")]
    private static partial Regex PascalWord();

    private sealed class ForbiddenVisitor : TSqlFragmentVisitor
    {
        public List<string> Found { get; } = [];

        private void Add(string name)
        {
            if (!Found.Contains(name))
                Found.Add(name);
        }

        public override void Visit(TSqlFragment node)
        {
            switch (node)
            {
                case PredicateSetStatement set:
                    if (set.Options.HasFlag(SetOptions.NoExec)) Add("SET NOEXEC");
                    if (set.Options.HasFlag(SetOptions.ParseOnly)) Add("SET PARSEONLY");
                    if (set.Options.HasFlag(SetOptions.FmtOnly)) Add("SET FMTONLY");
                    break;
                case ShutdownStatement:
                    Add("SHUTDOWN");
                    break;
                case ReconfigureStatement:
                    Add("RECONFIGURE");
                    break;
                case ExecutableProcedureReference { ProcedureReference.ProcedureReference.Name.BaseIdentifier.Value: { } proc }
                    when string.Equals(proc, "xp_cmdshell", StringComparison.OrdinalIgnoreCase):
                    Add("xp_cmdshell");
                    break;
                case OpenRowsetTableReference or BulkOpenRowset or InternalOpenRowset or OpenRowsetCosmos:
                    Add("OPENROWSET");
                    break;
                case AdHocTableReference:
                    Add("OPENDATASOURCE");
                    break;
                case ExecutableProcedureReference { AdHocDataSource: not null }:
                    Add("OPENDATASOURCE");
                    break;
                case AlterDatabaseStatement
                    or AlterDatabaseScopedConfigurationStatement
                    or AlterDatabaseEncryptionKeyStatement
                    or AlterDatabaseAuditSpecificationStatement:
                    Add("ALTER DATABASE");
                    break;
                case CreateLoginStatement:
                    Add("CREATE LOGIN");
                    break;
                case AlterLoginStatement:
                    Add("ALTER LOGIN");
                    break;
                case DropLoginStatement:
                    Add("DROP LOGIN");
                    break;
                case GrantStatement or GrantStatement80:
                    Add("GRANT");
                    break;
                case RevokeStatement or RevokeStatement80:
                    Add("REVOKE");
                    break;
                case DenyStatement or DenyStatement80:
                    Add("DENY");
                    break;
            }
        }
    }
}
