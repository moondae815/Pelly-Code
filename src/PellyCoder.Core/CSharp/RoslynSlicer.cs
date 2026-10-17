using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.CSharp;

/// <summary>The context the model sees, the lines the patcher may change, and whether the target alone blew the budget.</summary>
public sealed record SliceResult(Slice Slice, IReadOnlyList<EditableRegion> Regions, bool TargetTooLarge);

/// <summary>Builds the budgeted prompt context around one member (design 8.5). Parts carry no line numbers.</summary>
public static class RoslynSlicer
{
    private const int SignaturesPriority = 1;
    private const int UsingsPriority = 3;
    private const int FieldsPriority = 4;

    private static readonly Regex LineBreaks = new(@"\s*\n\s*", RegexOptions.Compiled);

    public static SliceResult Build(string sourceLf, MemberCandidate target, LanguageTarget language, ITokenCounter tokens, int contextBudget)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceLf, new CSharpParseOptions(language.Version));
        var root = (CompilationUnitSyntax)tree.GetRoot();
        var lines = sourceLf.Split('\n');

        // Same kinds and display as MemberLocator emits; an enclosing type or sibling on the same lines must not match.
        var member = root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .LastOrDefault(m => MemberLocator.SpanOf(tree, m) == target.Span
                && MemberLocator.DisplayOf(m) is { } display
                && target.Display.EndsWith("." + display, StringComparison.Ordinal))
            ?? throw new InvalidOperationException($"No member found at lines {target.Span.StartLine}-{target.Span.EndLine}.");

        var targetPart = MakePart("target", LinesText(lines, target.Span), tokens, int.MaxValue);

        var usingsSpan = UsingsSpan(tree, root);
        var usings = usingsSpan is null ? null : MakePart("usings", LinesText(lines, usingsSpan), tokens, UsingsPriority);

        SlicePart? fields = null, signatures = null;
        if (member.Parent is TypeDeclarationSyntax type)
        {
            var fieldLines = type.Members.OfType<FieldDeclarationSyntax>().Select(f => f.WithoutTrivia().ToString().Trim())
                .Concat(type.Members.OfType<ConstructorDeclarationSyntax>().Where(c => c != member).Select(ConstructorSignature));
            fields = NonEmpty("fields", fieldLines, tokens, FieldsPriority);

            var others = type.Members.Where(m => m != member && m is not FieldDeclarationSyntax and not ConstructorDeclarationSyntax);
            signatures = NonEmpty("signatures", others.Select(Signature), tokens, SignaturesPriority);
        }

        // Lowest priority is dropped first; the target is never in this list.
        var included = new[] { usings, fields, signatures }.OfType<SlicePart>().OrderBy(p => p.Priority).ToList();
        var omitted = new List<SlicePart>();
        var used = targetPart.Tokens + included.Sum(p => p.Tokens);
        var tooLarge = targetPart.Tokens > contextBudget;

        while (included.Count > 0 && (tooLarge || used > contextBudget))
        {
            var drop = included[0];
            included.RemoveAt(0);
            omitted.Add(drop);
            used -= drop.Tokens;
        }

        // Prompt order: surroundings first, the member being edited last.
        var ordered = included.OrderBy(p => p.Kind switch { "usings" => 0, "fields" => 1, _ => 2 }).Append(targetPart).ToList();

        var regions = new List<EditableRegion> { new(target.Span, "target") };
        if (usingsSpan is not null && included.Any(p => p.Kind == "usings"))
            regions.Add(new EditableRegion(usingsSpan, "usings"));

        return new SliceResult(new Slice(ordered, omitted, language.Hint), regions, tooLarge);
    }

    private static SlicePart MakePart(string kind, string text, ITokenCounter tokens, int priority) =>
        new(kind, text, tokens.Count(text), priority);

    private static SlicePart? NonEmpty(string kind, IEnumerable<string> lines, ITokenCounter tokens, int priority)
    {
        var text = string.Join('\n', lines);
        return text.Length == 0 ? null : MakePart(kind, text, tokens, priority);
    }

    private static string LinesText(string[] lines, TextSpanInfo span) =>
        string.Join('\n', lines[(span.StartLine - 1)..span.EndLine]);

    private static TextSpanInfo? UsingsSpan(SyntaxTree tree, CompilationUnitSyntax root)
    {
        if (root.Usings.Count > 0)
        {
            var first = tree.GetLineSpan(root.Usings[0].Span).StartLinePosition.Line + 1;
            var last = tree.GetLineSpan(root.Usings[^1].Span).EndLinePosition.Line + 1;
            return new TextSpanInfo(first, last);
        }

        var ns = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault();
        if (ns is null)
            return null;
        var line = tree.GetLineSpan(ns.NamespaceKeyword.Span).StartLinePosition.Line + 1;
        return new TextSpanInfo(line, line);
    }

    private static string ConstructorSignature(ConstructorDeclarationSyntax c) =>
        OneLine(c.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)) + " { ... }";

    private static string Signature(MemberDeclarationSyntax m) => m switch
    {
        MethodDeclarationSyntax { Body: null, ExpressionBody: null } => OneLine(m),
        MethodDeclarationSyntax x => Stub(x.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)),
        OperatorDeclarationSyntax x => Stub(x.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)),
        ConversionOperatorDeclarationSyntax x => Stub(x.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)),
        DestructorDeclarationSyntax x => Stub(x.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)),
        PropertyDeclarationSyntax x => WithAccessors(m, x.AccessorList, x.ExpressionBody,
            () => x.WithAccessorList(null).WithExpressionBody(null).WithInitializer(null).WithSemicolonToken(default)),
        IndexerDeclarationSyntax x => WithAccessors(m, x.AccessorList, x.ExpressionBody,
            () => x.WithAccessorList(null).WithExpressionBody(null).WithSemicolonToken(default)),
        EventDeclarationSyntax x => WithAccessors(m, x.AccessorList, null, () => x.WithAccessorList(null)),
        BaseTypeDeclarationSyntax nested => NestedTypeHeader(nested),
        _ => OneLine(m),
    };

    private static string Stub(SyntaxNode header) => OneLine(header) + " { ... }";

    /// <summary>Properties, indexers and events: bodied accessors become <c>get { ... }</c>; auto accessors stay as written.</summary>
    private static string WithAccessors(MemberDeclarationSyntax original, AccessorListSyntax? list, ArrowExpressionClauseSyntax? arrow, Func<SyntaxNode> header)
    {
        var accessors = list?.Accessors ?? default;
        var bodied = arrow is not null || accessors.Any(a => a.Body is not null || a.ExpressionBody is not null);
        if (!bodied)
            return OneLine(original);

        var head = OneLine(header());
        if (list is null)
            return head + " { get { ... } }";

        var parts = accessors.Select(a => a.Body is null && a.ExpressionBody is null
            ? a.Keyword.Text + ";"
            : a.Keyword.Text + " { ... }");
        return head + " { " + string.Join(' ', parts) + " }";
    }

    private static string NestedTypeHeader(BaseTypeDeclarationSyntax type)
    {
        // A positional record without a body (`record R(int A);`) has no braces: it is already one bodyless line.
        if (type.OpenBraceToken.IsKind(SyntaxKind.None))
            return OneLine(type);

        var text = type.ToString();
        var open = type.OpenBraceToken.SpanStart - type.SpanStart;
        return LineBreaks.Replace(text[..open].Trim(), " ") + " { ... }";
    }

    private static string OneLine(SyntaxNode node) =>
        LineBreaks.Replace(node.WithoutTrivia().NormalizeWhitespace().ToString().Trim(), " ");
}
