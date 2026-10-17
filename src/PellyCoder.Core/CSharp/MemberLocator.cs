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

/// <summary>Finds members by exact, case-sensitive name, by line, or lists them all (design 8.4).</summary>
public static class MemberLocator
{
    public static IReadOnlyList<MemberCandidate> FindByName(string sourceLf, LanguageVersion version, string? className, string memberName) =>
        Enumerate(sourceLf, version)
            .Where(m => m.Name == memberName && (className is null || m.InnermostType == className))
            .Select(m => m.Candidate)
            .ToList();

    /// <summary>The members <see cref="FindByName"/> can find (methods, constructors, properties), in source order.</summary>
    public static IReadOnlyList<MemberCandidate> AllMembers(string sourceLf, LanguageVersion version) =>
        Enumerate(sourceLf, version).Where(m => m.Name is not null).Select(m => m.Candidate).ToList();

    /// <summary>
    /// The innermost method, constructor, property, operator or conversion operator whose <see cref="MemberCandidate.Span"/>
    /// (leading doc comment included) contains the 1-based <paramref name="line"/>. Accessors, local functions and lambdas
    /// belong to the member around them. Null for any other line (fields, usings, type declarations, blank lines between members).
    /// </summary>
    public static MemberCandidate? FindAtLine(string sourceLf, LanguageVersion version, int line) =>
        Locatable(sourceLf, version)
            .Where(c => c.Span.StartLine <= line && line <= c.Span.EndLine)
            // Innermost: the latest start; members on the same lines keep source order.
            .OrderByDescending(c => c.Span.StartLine)
            .ThenBy(c => c.Span.EndLine)
            .FirstOrDefault();

    /// <summary>Every member <see cref="FindAtLine"/> can return (operators included), in source order.</summary>
    internal static IReadOnlyList<MemberCandidate> Locatable(string sourceLf, LanguageVersion version) =>
        Enumerate(sourceLf, version).Select(m => m.Candidate).ToList();

    /// <summary>The member name of a display: text before the parameter list, after the last dot (<c>operator +</c> for operators).</summary>
    internal static string NameOf(string display)
    {
        var paren = display.IndexOf('(');
        var head = paren >= 0 ? display[..paren] : display;
        return head[(head.LastIndexOf('.') + 1)..];
    }

    /// <summary>The display text (<c>Name(int, string)</c>, <c>operator +(P, P)</c>) for the member kinds this locator emits, else null.</summary>
    internal static string? DisplayOf(MemberDeclarationSyntax member) => NameAndDisplay(member).Display;

    private sealed record Located(string? Name, string InnermostType, MemberCandidate Candidate);

    private static IEnumerable<Located> Enumerate(string sourceLf, LanguageVersion version)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceLf, new CSharpParseOptions(version));
        foreach (var member in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            var (name, display) = NameAndDisplay(member);
            if (display is null)
                continue;

            var types = member.Ancestors().OfType<BaseTypeDeclarationSyntax>().ToList();
            if (types.Count == 0)
                continue;

            var containing = string.Join('.', Enumerable.Reverse(types).Select(t => t.Identifier.Text));
            yield return new Located(name, types[0].Identifier.Text,
                new MemberCandidate($"{containing}.{display}", containing, SpanOf(tree, member)));
        }
    }

    /// <summary>Operators have no name: <see cref="FindByName"/> and <see cref="AllMembers"/> skip them; only a line finds them.</summary>
    private static (string? Name, string? Display) NameAndDisplay(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => (m.Identifier.Text, m.Identifier.Text + ParameterList(m.ParameterList)),
        ConstructorDeclarationSyntax c => (c.Identifier.Text, c.Identifier.Text + ParameterList(c.ParameterList)),
        PropertyDeclarationSyntax p => (p.Identifier.Text, p.Identifier.Text),
        OperatorDeclarationSyntax o => (null, "operator " + o.OperatorToken.Text + ParameterList(o.ParameterList)),
        ConversionOperatorDeclarationSyntax c => (null, "operator " + c.Type + ParameterList(c.ParameterList)),
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
