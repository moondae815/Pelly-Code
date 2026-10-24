using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using S6Planning;

namespace S8SemanticLoad;

/// <summary>
/// S6 arm C with symbols instead of names: from the member a human chose to change, list its implementations/overrides
/// and every member that calls it or them. Same output grammar as S6 <c>CallerFinder</c> so the S6 scorer applies.
/// </summary>
public static class SemanticCallerFinder
{
    public static async Task<string> PlanAsync(Solution s, string corpusRoot, PlanTask seed, CancellationToken ct)
    {
        var seedSymbols = await FindSeedAsync(s, corpusRoot, seed, ct);
        if (seedSymbols.Count == 0)
            throw new InvalidOperationException($"Seed member {seed.Path}#{TargetKey.Member(seed.Symbol)} not found.");

        var targets = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(string path, string member)
        {
            if (seen.Add(TargetKey.Of("cs", path, member))) targets.Add(path + "#" + member);
        }

        foreach (var m in seedSymbols)
            foreach (var loc in m.Locations.Where(l => l.IsInSource))
                Add(Relative(corpusRoot, loc), m.Name);

        var family = new List<ISymbol>(seedSymbols);
        var impls = new List<(string Path, int Pos, ISymbol Symbol)>();
        foreach (var m in seedSymbols)
        {
            var found = (await SymbolFinder.FindImplementationsAsync(m, s, cancellationToken: ct))
                .Concat(await SymbolFinder.FindOverridesAsync(m, s, cancellationToken: ct));
            foreach (var impl in found)
                foreach (var loc in impl.Locations.Where(l => l.IsInSource))
                    impls.Add((Relative(corpusRoot, loc), loc.SourceSpan.Start, impl));
        }
        foreach (var (path, _, symbol) in impls.OrderBy(i => i.Path, StringComparer.Ordinal).ThenBy(i => i.Pos))
        {
            Add(path, symbol.Name);
            family.Add(symbol);
        }

        var callers = new List<(string Path, int Pos, string Member)>();
        foreach (var symbol in family.Distinct(SymbolEqualityComparer.Default))
            foreach (var r in await SymbolFinder.FindReferencesAsync(symbol, s, ct))
                foreach (var loc in r.Locations)
                {
                    var tree = loc.Location.SourceTree;
                    if (tree is null) continue;
                    var node = (await tree.GetRootAsync(ct)).FindNode(loc.Location.SourceSpan);
                    var member = node.AncestorsAndSelf().Select(MemberName).FirstOrDefault(n => n is not null);
                    if (member is not null)
                        callers.Add((Relative(corpusRoot, loc.Location), loc.Location.SourceSpan.Start, member));
                }
        foreach (var (path, _, member) in callers.OrderBy(c => c.Path, StringComparer.Ordinal).ThenBy(c => c.Pos))
            Add(path, member);

        var sb = new StringBuilder();
        for (var i = 0; i < targets.Count; i++)
            sb.Append("- [ ] T").Append(i + 1).Append(" cs ").Append(targets[i]).Append(" (code) [G1]\n");
        return sb.ToString();
    }

    /// <summary>Members named like the seed, on the first type in the seed file that declares one (as S6 arm C does).</summary>
    private static async Task<IReadOnlyList<ISymbol>> FindSeedAsync(Solution s, string corpusRoot, PlanTask seed, CancellationToken ct)
    {
        var name = TargetKey.Member(seed.Symbol);
        var wanted = TargetKey.Of("sql", seed.Path, null);
        foreach (var doc in s.Projects.SelectMany(p => p.Documents))
        {
            if (doc.FilePath is null || TargetKey.Of("sql", Path.GetRelativePath(corpusRoot, doc.FilePath), null) != wanted)
                continue;
            var root = await doc.GetSyntaxRootAsync(ct);
            var model = await doc.GetSemanticModelAsync(ct);
            if (root is null || model is null) continue;
            var type = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                .FirstOrDefault(t => t.Members.Any(m => MemberName(m) == name));
            if (type is null || model.GetDeclaredSymbol(type, ct) is not INamedTypeSymbol symbol) continue;
            return symbol.GetMembers(name).Where(m => m is IMethodSymbol or IPropertySymbol).ToList();
        }
        return [];
    }

    private static string Relative(string corpusRoot, Location loc) =>
        Path.GetRelativePath(corpusRoot, loc.SourceTree!.FilePath).Replace('\\', '/');

    private static string? MemberName(SyntaxNode n) => n switch
    {
        MethodDeclarationSyntax m => m.Identifier.ValueText,
        PropertyDeclarationSyntax p => p.Identifier.ValueText,
        _ => null,
    };
}
