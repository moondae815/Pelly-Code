using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PellyCoder.Core.CharTests;

/// <summary>What the prompt and the shape check need from the test class: usings, member names, helper signatures, an example test.</summary>
internal sealed record TestClassContext(IReadOnlyList<string> Usings, IReadOnlySet<string> MemberNames, string? Helpers, string? Example)
{
    public static TestClassContext Of(string fileTextLf, string className, FrameworkInfo framework, LanguageVersion version)
    {
        var root = CSharpSyntaxTree.ParseText(fileTextLf, new CSharpParseOptions(version)).GetCompilationUnitRoot();
        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Select(u => u.ToString().Trim()).Distinct(StringComparer.Ordinal).ToList();
        var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.ValueText == className);
        if (cls is null) return new TestClassContext(usings, new HashSet<string>(StringComparer.Ordinal), null, null);

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var member in cls.Members)
        {
            switch (member)
            {
                case MethodDeclarationSyntax m: names.Add(m.Identifier.ValueText); break;
                case PropertyDeclarationSyntax p: names.Add(p.Identifier.ValueText); break;
                case BaseFieldDeclarationSyntax f: foreach (var v in f.Declaration.Variables) names.Add(v.Identifier.ValueText); break;
                case BaseTypeDeclarationSyntax t: names.Add(t.Identifier.ValueText); break;
                case EventDeclarationSyntax e: names.Add(e.Identifier.ValueText); break;
                case DelegateDeclarationSyntax d: names.Add(d.Identifier.ValueText); break;
            }
        }

        var tests = cls.Members.OfType<MethodDeclarationSyntax>().Where(m => IsTest(m, framework)).ToList();
        var helperLines = new List<string>();
        foreach (var member in cls.Members)
        {
            if (member is MethodDeclarationSyntax m && tests.Contains(m)) continue;
            if (member is not (MethodDeclarationSyntax or BaseTypeDeclarationSyntax or BaseFieldDeclarationSyntax or PropertyDeclarationSyntax
                or ConstructorDeclarationSyntax)) continue;
            helperLines.Add(Signature(member));
        }

        var example = tests.OrderBy(t => t.Span.Length).FirstOrDefault() is { } shortest ? Dedent(fileTextLf, shortest) : null;
        return new TestClassContext(usings, names, helperLines.Count == 0 ? null : string.Join("\n", helperLines), example);
    }

    /// <summary>Names of the test methods (framework test or data-test attribute) in the generated members, in order.</summary>
    public static IReadOnlyList<string> TestMethodNames(CharTestOutput output, FrameworkInfo framework, LanguageVersion version)
    {
        var tree = CSharpSyntaxTree.ParseText("class __P {\n" + string.Join("\n", output.Members) + "\n}", new CSharpParseOptions(version));
        return tree.GetCompilationUnitRoot().DescendantNodes().OfType<ClassDeclarationSyntax>().First().Members
            .OfType<MethodDeclarationSyntax>().Where(m => IsTest(m, framework)).Select(m => m.Identifier.ValueText).ToList();
    }

    private static bool IsTest(MethodDeclarationSyntax m, FrameworkInfo framework) =>
        m.AttributeLists.SelectMany(l => l.Attributes).Any(a =>
        {
            var name = a.Name switch
            {
                QualifiedNameSyntax q => q.Right.Identifier.Text,
                AliasQualifiedNameSyntax q => q.Name.Identifier.Text,
                SimpleNameSyntax s => s.Identifier.Text,
                _ => a.Name.ToString(),
            };
            if (name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length) name = name[..^"Attribute".Length];
            return name == framework.TestAttribute || framework.DataTestAttributes.Contains(name);
        });

    /// <summary>The member without bodies or initializers, whitespace normalized (nested types keep their member signatures).</summary>
    private static string Signature(MemberDeclarationSyntax member)
    {
        var stripped = (MemberDeclarationSyntax)new BodyStripper().Visit(member.WithoutTrivia());
        return stripped.NormalizeWhitespace().ToFullString().Trim();
    }

    /// <summary>The member's lines from the file with their common indentation removed.</summary>
    private static string Dedent(string text, SyntaxNode node)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, node.SpanStart - 1)) + 1;
        if (node.SpanStart == 0) start = 0;
        var lines = text[start..node.Span.End].Split('\n');
        var indent = lines.Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart(' ', '\t').Length).DefaultIfEmpty(0).Min();
        return string.Join("\n", lines.Select(l => l.Length >= indent ? l[indent..] : l.TrimStart(' ', '\t')));
    }

    private sealed class BodyStripper : CSharpSyntaxRewriter
    {
        private static readonly SyntaxToken Semicolon = SyntaxFactory.Token(SyntaxKind.SemicolonToken);

        public override SyntaxNode? VisitMethodDeclaration(MethodDeclarationSyntax node) =>
            node.Body is null && node.ExpressionBody is null ? node
                : node.WithBody(null).WithExpressionBody(null).WithSemicolonToken(Semicolon);

        public override SyntaxNode? VisitConstructorDeclaration(ConstructorDeclarationSyntax node) =>
            node.Body is null && node.ExpressionBody is null ? node
                : node.WithBody(null).WithExpressionBody(null).WithInitializer(null).WithSemicolonToken(Semicolon);

        public override SyntaxNode? VisitAccessorDeclaration(AccessorDeclarationSyntax node) =>
            node.Body is null && node.ExpressionBody is null ? node
                : node.WithBody(null).WithExpressionBody(null).WithSemicolonToken(Semicolon);

        public override SyntaxNode? VisitPropertyDeclaration(PropertyDeclarationSyntax node)
        {
            var visited = (PropertyDeclarationSyntax)base.VisitPropertyDeclaration(node)!;
            if (visited.ExpressionBody is not null)
                visited = visited.WithExpressionBody(null).WithSemicolonToken(default)
                    .WithAccessorList(SyntaxFactory.AccessorList(SyntaxFactory.SingletonList(
                        SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration).WithSemicolonToken(Semicolon))));
            return visited.Initializer is null ? visited : visited.WithInitializer(null).WithSemicolonToken(default);
        }

        public override SyntaxNode? VisitVariableDeclarator(VariableDeclaratorSyntax node) => node.WithInitializer(null);
    }
}
