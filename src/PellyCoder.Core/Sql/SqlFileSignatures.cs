using System.Text;
using System.Text.RegularExpressions;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PellyCoder.Core.Abstractions;

namespace PellyCoder.Core.Sql;

/// <summary>
/// Stored procedure parameters read from a .sql file rather than the database (A2 design §4.4): what an asp task sees of a
/// procedure an earlier sql task changed but nobody deployed yet. The spec draft index renders the same parameters.
/// </summary>
public static class SqlFileSignatures
{
    /// <summary>
    /// One signature per <c>CREATE</c>, <c>ALTER</c> or <c>CREATE OR ALTER PROCEDURE</c> in <paramref name="sqlTextLf"/>, in file
    /// order, with each parameter's name, type as written, OUTPUT and whether it has a default. <see cref="SqlProcedureSignature.FirstResultSet"/>
    /// is null (result columns are not read from files). Null when the file does not parse.
    /// </summary>
    public static IReadOnlyList<SqlProcedureSignature>? Read(string sqlTextLf, int compatLevel)
    {
        ArgumentNullException.ThrowIfNull(sqlTextLf);
        if (Parse(sqlTextLf, compatLevel) is not { } script)
            return null;
        var result = new List<SqlProcedureSignature>();
        foreach (var p in script.Batches.SelectMany(b => b.Statements).OfType<ProcedureStatementBody>())
        {
            var n = p.ProcedureReference.Name;
            var parameters = p.Parameters.Select(x => new SqlParameterInfo(x.VariableName.Value, Text(x.DataType),
                x.Modifier == ParameterModifier.Output, HasDefault: x.Value is not null)).ToList();
            result.Add(new SqlProcedureSignature(new SqlObjectName(n.SchemaIdentifier?.Value, n.BaseIdentifier.Value), parameters, null));
        }
        return result;
    }

    /// <summary>The parsed script; null when it has parse errors.</summary>
    internal static TSqlScript? Parse(string text, int compatLevel)
    {
        var fragment = SqlParsers.Create(compatLevel, true).Parse(new StringReader(text), out var errors);
        return errors.Count > 0 ? null : (TSqlScript)fragment;
    }

    /// <summary>The fragment's tokens as written, with each whitespace run made one space.</summary>
    internal static string Text(TSqlFragment f)
    {
        var sb = new StringBuilder();
        for (var i = f.FirstTokenIndex; i <= f.LastTokenIndex; i++)
            sb.Append(f.ScriptTokenStream[i].Text);
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
