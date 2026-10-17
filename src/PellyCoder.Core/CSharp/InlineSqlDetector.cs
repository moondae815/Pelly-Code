using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PellyCoder.Core.CSharp;

/// <summary>
/// Notices SQL text that the edit introduced into the target member (design §8.15). A build cannot check it, so REVIEW warns.
/// Strings only: regular, verbatim, raw and the text parts of interpolated strings.
/// </summary>
public static partial class InlineSqlDetector
{
    [GeneratedRegex(@"\b(SELECT|INSERT|UPDATE|DELETE|EXEC)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SqlKeyword();

    /// <summary>
    /// True when the member <paramref name="targetDisplay"/> of <paramref name="afterLf"/> holds a string literal that is not in
    /// the same member of <paramref name="beforeLf"/> and contains a SQL keyword.
    /// </summary>
    public static bool ChangedSqlLiteral(string beforeLf, string afterLf, string targetDisplay, LanguageVersion version)
    {
        var before = LiteralsOf(beforeLf, targetDisplay, version).ToHashSet(StringComparer.Ordinal);
        return LiteralsOf(afterLf, targetDisplay, version).Any(l => !before.Contains(l) && SqlKeyword().IsMatch(l));
    }

    private static IEnumerable<string> LiteralsOf(string sourceLf, string targetDisplay, LanguageVersion version)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceLf, new CSharpParseOptions(version));
        var wanted = MemberLocator.Locatable(sourceLf, version).FirstOrDefault(c => c.Display == targetDisplay);
        if (wanted is null)
            yield break;

        foreach (var member in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            if (MemberLocator.DisplayOf(member) is null || MemberLocator.SpanOf(tree, member) != wanted.Span)
                continue;
            foreach (var token in member.DescendantTokens())
            {
                if (token.IsKind(SyntaxKind.StringLiteralToken) || token.IsKind(SyntaxKind.InterpolatedStringTextToken)
                    || token.IsKind(SyntaxKind.Utf8StringLiteralToken))
                    yield return token.ValueText;
            }
            yield break;
        }
    }
}
