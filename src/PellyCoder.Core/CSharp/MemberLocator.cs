using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.CSharp;

/// <summary>A method, constructor or property that matched a name lookup.</summary>
/// <param name="Display">e.g. <c>OrderService.CancelAsync(int, string)</c>.</param>
/// <param name="ContainingType">Dotted path of the enclosing types (<c>Outer.Inner</c>).</param>
/// <param name="Span">1-based lines, from the leading XML doc comment (or declaration) to the member's last line.</param>
public sealed record MemberCandidate(string Display, string ContainingType, TextSpanInfo Span);

/// <summary>Finds members by exact, case-sensitive name (design 8.4).</summary>
public static class MemberLocator
{
    public static IReadOnlyList<MemberCandidate> FindByName(string sourceLf, LanguageVersion version, string? className, string memberName)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceLf, new CSharpParseOptions(version));
        var result = new List<MemberCandidate>();

        foreach (var member in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            var (name, display) = NameAndDisplay(member);
            if (name != memberName || display is null)
                continue;

            var types = member.Ancestors().OfType<BaseTypeDeclarationSyntax>().ToList();
            if (types.Count == 0 || (className is not null && types[0].Identifier.Text != className))
                continue;

            var containing = string.Join('.', Enumerable.Reverse(types).Select(t => t.Identifier.Text));
            result.Add(new MemberCandidate($"{containing}.{display}", containing, SpanOf(tree, member)));
        }

        return result;
    }

    /// <summary>The display text (<c>Name(int, string)</c>) for the member kinds this locator emits, else null.</summary>
    internal static string? DisplayOf(MemberDeclarationSyntax member) => NameAndDisplay(member).Display;

    private static (string? Name, string? Display) NameAndDisplay(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => (m.Identifier.Text, m.Identifier.Text + ParameterList(m.ParameterList)),
        ConstructorDeclarationSyntax c => (c.Identifier.Text, c.Identifier.Text + ParameterList(c.ParameterList)),
        PropertyDeclarationSyntax p => (p.Identifier.Text, p.Identifier.Text),
        _ => (null, null),
    };

    private static string ParameterList(ParameterListSyntax list) =>
        "(" + string.Join(", ", list.Parameters.Select(p => p.Type?.ToString() ?? "?")) + ")";

    internal static TextSpanInfo SpanOf(SyntaxTree tree, MemberDeclarationSyntax member)
    {
        var doc = member.GetLeadingTrivia().FirstOrDefault(t =>
            t.IsKind(SyntaxKind.SingleLineDocumentationCommentTrivia) || t.IsKind(SyntaxKind.MultiLineDocumentationCommentTrivia));
        var start = doc.IsKind(SyntaxKind.None) ? member.SpanStart : doc.SpanStart;
        var startLine = tree.GetLineSpan(new Microsoft.CodeAnalysis.Text.TextSpan(start, 0)).StartLinePosition.Line + 1;
        var endLine = tree.GetLineSpan(member.Span).EndLinePosition.Line + 1;
        return new TextSpanInfo(startLine, endLine);
    }
}
