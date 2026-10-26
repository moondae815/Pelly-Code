using System.IO.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Testing;

/// <summary>A test method declaration: full name (namespace.Outer.Inner.Method), its file and the nearest project file.</summary>
public sealed record TestMethodDecl(string FullName, string File, string? Csproj);

/// <summary>Finds attributed test methods in the workspace sources (bounded like the spec checker's scans).</summary>
public static class TestMethodIndex
{
    private static readonly HashSet<string> TestAttributes =
        new(["Fact", "Theory", "Test", "TestCase", "TestMethod", "DataTestMethod"], StringComparer.Ordinal);

    public static IReadOnlyList<TestMethodDecl> Find(IFileSystem fs, string workspaceRoot, string name)
    {
        var found = new List<TestMethodDecl>();
        foreach (var (path, text) in BoundedSources.Read(fs, workspaceRoot, "*.cs"))
        {
            // Cheap pre-filter: skip files that never mention the method name.
            if (!text.Contains(name[(name.LastIndexOf('.') + 1)..], StringComparison.Ordinal)) continue;
            foreach (var m in CSharpSyntaxTree.ParseText(text).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>())
            {
                if (!m.AttributeLists.SelectMany(l => l.Attributes).Any(IsTestAttribute)) continue;
                var fullName = FullName(m);
                if (TestNames.Matches(name, fullName))
                    found.Add(new TestMethodDecl(fullName, path, ProjectFileLocator.FindNearestCsproj(fs, path)));
            }
        }
        return found.OrderBy(d => d.FullName, StringComparer.Ordinal).ThenBy(d => d.File, StringComparer.Ordinal).ToList();
    }

    /// <summary>Texts (without trivia) of the attributed test methods in <paramref name="source"/> whose full name is listed, in source order.</summary>
    public static IReadOnlyList<string> DeclarationTexts(string source, IReadOnlyCollection<string> fullNames) =>
        CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(m => m.AttributeLists.SelectMany(l => l.Attributes).Any(IsTestAttribute) && fullNames.Contains(FullName(m)))
            .Select(m => m.ToString())
            .ToList();

    private static bool IsTestAttribute(AttributeSyntax a)
    {
        var last = a.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.ValueText,
            AliasQualifiedNameSyntax al => al.Name.Identifier.ValueText,
            SimpleNameSyntax s => s.Identifier.ValueText,
            _ => "",
        };
        if (last.EndsWith("Attribute", StringComparison.Ordinal)) last = last[..^"Attribute".Length];
        return TestAttributes.Contains(last);
    }

    private static string FullName(MethodDeclarationSyntax method)
    {
        var parts = new List<string> { method.Identifier.ValueText };
        for (var node = method.Parent; node is not null; node = node.Parent)
            switch (node)
            {
                case BaseTypeDeclarationSyntax t: parts.Add(t.Identifier.ValueText); break;
                case BaseNamespaceDeclarationSyntax ns: parts.Add(ns.Name.ToString()); break;
            }
        parts.Reverse();
        return string.Join('.', parts);
    }
}
