using System.IO.Abstractions;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Workspaces;
using Workspace = PellyCoder.Core.Workspaces.Workspace;

namespace PellyCoder.Core.Impact;

/// <summary>A SQL object whose name the search looks for in C# string literals, tied to the spec task that changes it.</summary>
public sealed record SqlNameSeed(string TaskId, SqlObjectName Name);

/// <summary>
/// Finds C# members that call a stored procedure by writing its name in a string literal (<c>"dbo.usp_X"</c>,
/// <c>"EXEC usp_X @Id"</c>). Syntax only: a literal that merely looks like the name is an accepted false hit.
/// </summary>
public static class SqlNameStringFinder
{
    public const string Reason = "이름 호출(문자열)";

    private const string OutsideMember = "멤버 밖";
    private const string IdentChar = @"[A-Za-z0-9_@#$\]]";

    public static (IReadOnlyList<ImpactTarget> Targets, bool Incomplete) Find(IFileSystem fs, Workspace ws, IReadOnlyList<SqlNameSeed> seeds, int maxDirectories = 2000)
    {
        var (files, incomplete) = NameImpactFinder.Load(fs, ws, [], maxDirectories);
        var matchers = seeds.Select(s => (Seed: s, Forms: Forms(s.Name))).ToList();
        var targets = new List<ImpactTarget>();

        foreach (var (seed, forms) in matchers)
        {
            var seen = new HashSet<(string, string)>();
            var found = new List<(string Path, int Position, string Member, string Reason)>();
            foreach (var file in files)
                foreach (var token in file.Root.DescendantTokens().Where(IsStringText))
                {
                    if (!forms.IsMatch(token.ValueText)) continue;
                    var (member, reason) = NameImpactFinder.Enclosing(token.Parent!);
                    if (seen.Add((file.Path, member)))
                        found.Add((file.Path, token.SpanStart, member, reason == "호출" ? Reason : OutsideMember));
                }
            targets.AddRange(found
                .OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Position)
                .Select(f => new ImpactTarget(seed.TaskId, f.Path, f.Member, f.Reason)));
        }

        return (targets, incomplete);
    }

    private static bool IsStringText(SyntaxToken t) => t.Kind() is SyntaxKind.StringLiteralToken or SyntaxKind.Utf8StringLiteralToken
        or SyntaxKind.SingleLineRawStringLiteralToken or SyntaxKind.MultiLineRawStringLiteralToken
        or SyntaxKind.InterpolatedStringTextToken;

    /// <summary>
    /// Whole literal equal to a name form, or an EXEC/EXECUTE followed by one that is not the start of a longer name.
    /// An unqualified form counts only for dbo (or no schema); each part may be bracketed.
    /// </summary>
    private static Regex Forms(SqlObjectName name)
    {
        var schema = name.Schema ?? "dbo";
        var obj = Part(name.Name);
        var form = $@"{Part(schema)}\s*\.\s*{obj}";
        if (schema.Equals("dbo", StringComparison.OrdinalIgnoreCase)) form = $"(?:{form}|{obj})";
        return new Regex($@"^\s*{form}\s*$|\bEXEC(?:UTE)?\s+{form}(?!{IdentChar})",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    private static string Part(string part) => $@"(?:\[{Regex.Escape(part)}\]|{Regex.Escape(part)})";
}
