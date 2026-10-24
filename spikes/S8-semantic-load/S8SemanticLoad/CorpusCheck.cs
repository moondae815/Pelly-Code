using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using S6Planning;

namespace S8SemanticLoad;

public sealed record A3Result(IReadOnlyList<string> Missing, IReadOnlyList<string> ForbiddenHit, IReadOnlyList<string> Extra)
{
    public bool Matches => Missing.Count == 0 && ForbiddenHit.Count == 0;
}

/// <summary>
/// Checks semantic find-references/implementations against the S6 a3 gold (rename IOrderDao.GetOrder).
/// Locations become S6 target keys ("path#member", path relative to the corpus root).
/// </summary>
public static class CorpusCheck
{
    public const string A3CaseId = "a3-rename-getorder";

    public static async Task<A3Result> CheckA3Async(Solution s, string corpusRoot, string casesDir, CancellationToken ct = default)
    {
        var gold = GoldLoader.Load(Path.Combine(casesDir, A3CaseId));
        var target = await FindMethodAsync(s, "Shop.Data.IOrderDao", "GetOrder", ct)
            ?? throw new InvalidOperationException("Shop.Data.IOrderDao.GetOrder not found in the solution");
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var loc in target.Locations.Where(l => l.IsInSource))
            AddKey(found, loc, corpusRoot);
        foreach (var impl in await SymbolFinder.FindImplementationsAsync(target, s, cancellationToken: ct))
        foreach (var loc in impl.Locations.Where(l => l.IsInSource))
            AddKey(found, loc, corpusRoot);
        foreach (var r in await SymbolFinder.FindReferencesAsync(target, s, ct))
        foreach (var loc in r.Locations)
            AddKey(found, loc.Location, corpusRoot);
        return Compare(found, gold);
    }

    /// <summary>Negative control: every invocation spelled GetOrder, as a name-only finder would see it.</summary>
    public static async Task<IReadOnlyList<string>> NameOnlyKeysAsync(Solution s, string corpusRoot, CancellationToken ct = default)
    {
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var doc in s.Projects.SelectMany(p => p.Documents))
        {
            var root = await doc.GetSyntaxRootAsync(ct);
            if (root is null) continue;
            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var name = call.Expression switch
                {
                    MemberAccessExpressionSyntax ma => ma.Name.Identifier.ValueText,
                    IdentifierNameSyntax id => id.Identifier.ValueText,
                    _ => null,
                };
                if (name == "GetOrder") AddKey(found, call.GetLocation(), corpusRoot);
            }
        }
        return found.ToList();
    }

    public static A3Result Compare(IEnumerable<string> found, GoldCase gold)
    {
        var set = found.ToHashSet(StringComparer.Ordinal);
        var required = gold.Required.Select(TargetKey.Of).ToHashSet(StringComparer.Ordinal);
        var forbidden = gold.ForbiddenKeys.ToHashSet(StringComparer.Ordinal);
        return new A3Result(
            required.Where(k => !set.Contains(k)).Order(StringComparer.Ordinal).ToList(),
            set.Where(forbidden.Contains).Order(StringComparer.Ordinal).ToList(),
            set.Where(k => !required.Contains(k) && !forbidden.Contains(k)).Order(StringComparer.Ordinal).ToList());
    }

    private static async Task<IMethodSymbol?> FindMethodAsync(Solution s, string typeName, string member, CancellationToken ct)
    {
        foreach (var project in s.Projects)
        {
            var type = (await project.GetCompilationAsync(ct))?.GetTypeByMetadataName(typeName);
            if (type is not null && type.Locations.Any(l => l.IsInSource))
                return type.GetMembers(member).OfType<IMethodSymbol>().FirstOrDefault();
        }
        return null;
    }

    private static void AddKey(HashSet<string> set, Location loc, string corpusRoot)
    {
        if (!loc.IsInSource || loc.SourceTree?.FilePath is not { Length: > 0 } path) return;
        var node = loc.SourceTree.GetRoot().FindNode(loc.SourceSpan);
        var member = node.AncestorsAndSelf().Select(MemberName).FirstOrDefault(n => n is not null);
        set.Add(TargetKey.Of("cs", Path.GetRelativePath(corpusRoot, path), member));
    }

    private static string? MemberName(Microsoft.CodeAnalysis.SyntaxNode n) => n switch
    {
        MethodDeclarationSyntax m => m.Identifier.ValueText,
        PropertyDeclarationSyntax p => p.Identifier.ValueText,
        ConstructorDeclarationSyntax c => c.Identifier.ValueText,
        _ => null,
    };
}
