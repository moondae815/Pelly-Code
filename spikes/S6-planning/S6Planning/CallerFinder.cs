using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.Files;

namespace S6Planning;

/// <summary>
/// Arm C: starting from a member a human chose to change, list the implementations and call sites a build would need,
/// using syntax only. Matching is by name, so a same-named method on an unrelated type is also picked up — an accepted
/// limit of the baseline, reported with the results.
/// </summary>
public static class CallerFinder
{
    public static string Plan(string corpusRoot, IReadOnlyList<string> scope, PlanTask seed)
    {
        var files = CorpusIndexBuilder.Build(corpusRoot, scope).Files.Where(f => f.Kind == "cs")
            .Select(f => (f.Path, Root: CSharpSyntaxTree.ParseText(TextDecoding.Decode(File.ReadAllBytes(Path.Combine(corpusRoot, f.Path)))).GetRoot()))
            .ToList();

        var name = TargetKey.Member(seed.Symbol);
        var seedKey = TargetKey.Of(seed);
        var seedFile = files.FirstOrDefault(f => TargetKey.Of("sql", f.Path, null) == TargetKey.Of("sql", seed.Path, null));
        var seedType = seedFile.Root?.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .FirstOrDefault(t => t.Members.Any(m => MemberName(m) == name))
            ?? throw new InvalidOperationException($"Seed member {seed.Path}#{name} not found.");
        var typeName = seedType.Identifier.Text;

        var targets = new List<string> { seedFile.Path + "#" + name };
        var seen = new HashSet<string>(StringComparer.Ordinal) { seedKey };

        foreach (var (path, root) in files)
            foreach (var type in root.DescendantNodes().OfType<TypeDeclarationSyntax>())
                if (type.BaseList?.Types.Any(b => SimpleName(b.Type) == typeName) == true
                    && type.Members.Any(m => MemberName(m) == name)
                    && seen.Add(TargetKey.Of("cs", path, name)))
                    targets.Add(path + "#" + name);

        foreach (var (path, root) in files)
            foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>().Where(m => m is MethodDeclarationSyntax or PropertyDeclarationSyntax))
            {
                var calls = member.DescendantNodes().OfType<InvocationExpressionSyntax>()
                    .Any(i => i.Expression is MemberAccessExpressionSyntax ma && ma.Name.Identifier.Text == name);
                var memberName = MemberName(member)!;
                if (calls && seen.Add(TargetKey.Of("cs", path, memberName)))
                    targets.Add(path + "#" + memberName);
            }

        var sb = new StringBuilder();
        for (var i = 0; i < targets.Count; i++)
            sb.Append("- [ ] T").Append(i + 1).Append(" cs ").Append(targets[i]).Append(" (code) [G1]\n");
        return sb.ToString();
    }

    private static string? MemberName(SyntaxNode m) => m switch
    {
        MethodDeclarationSyntax x => x.Identifier.Text,
        PropertyDeclarationSyntax x => x.Identifier.Text,
        _ => null,
    };

    private static string SimpleName(TypeSyntax t) => t switch
    {
        QualifiedNameSyntax q => SimpleName(q.Right),
        GenericNameSyntax g => g.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        _ => t.ToString(),
    };
}
