using System.Text.RegularExpressions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Specs;

/// <summary>
/// A stored procedure whose parameter list a sql task changed (A2 design §4.5).
/// </summary>
/// <param name="Summary">Joined with <c>", "</c>: <c>@status 추가</c>, <c>@from 삭제</c>, <c>@to 형식 변경</c>, …</param>
/// <param name="OnlyTrailingDefaultsAdded">
/// Parameters with a default were only added after the existing ones: a Classic ADO call that leaves them out may still work.
/// </param>
public sealed record ParameterChange(SqlObjectName Object, string Summary, bool OnlyTrailingDefaultsAdded)
{
    /// <summary>
    /// Compares a procedure's parameters before and after by name (case-insensitive), order, type (case-insensitive, whitespace
    /// normalized), OUTPUT and default. Null when they are the same.
    /// </summary>
    public static ParameterChange? Compare(SqlProcedureSignature before, SqlProcedureSignature after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var b = before.Parameters;
        var a = after.Parameters;
        var parts = new List<string>();

        foreach (var old in b)
        {
            var now = a.FirstOrDefault(p => Same(p.Name, old.Name));
            if (now is null) { parts.Add($"{old.Name} 삭제"); continue; }
            if (!string.Equals(Type(old.Type), Type(now.Type), StringComparison.OrdinalIgnoreCase)) parts.Add($"{old.Name} 형식 변경");
            if (old.IsOutput != now.IsOutput) parts.Add($"{old.Name} OUTPUT 변경");
            if (old.HasDefault != now.HasDefault) parts.Add($"{old.Name} 기본값 변경");
        }
        foreach (var added in a.Where(p => !b.Any(o => Same(o.Name, p.Name))))
            parts.Add($"{added.Name} 추가");

        var kept = b.Where(o => a.Any(p => Same(p.Name, o.Name))).Select(o => o.Name.ToUpperInvariant());
        var order = a.Where(p => b.Any(o => Same(p.Name, o.Name))).Select(p => p.Name.ToUpperInvariant());
        if (!kept.SequenceEqual(order)) parts.Add("순서 변경");

        if (parts.Count == 0) return null;

        // Only additions, all after the existing parameters, all with a default.
        var trailing = parts.All(p => p.EndsWith(" 추가", StringComparison.Ordinal))
            && a.Count > b.Count
            && a.Take(b.Count).Zip(b).All(z => Same(z.First.Name, z.Second.Name))
            && a.Skip(b.Count).All(p => p.HasDefault);
        return new ParameterChange(after.Name, string.Join(", ", parts), trailing);
    }

    private static bool Same(string x, string y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);

    /// <summary>The type with whitespace made insignificant: <c>varchar (10)</c> and <c>varchar(10)</c> are the same.</summary>
    private static string Type(string type) => Regex.Replace(Regex.Replace(type.Trim(), @"\s+", " "), @" ?([(),]) ?", "$1");
}
