using System.IO.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using PellyCoder.Core.Files;
using Workspace = PellyCoder.Core.Workspaces.Workspace;

namespace PellyCoder.Core.Impact;

public static class ImpactFinders
{
    public static IImpactFinder Default(IFileSystem fs) => new SemanticImpactFinder(fs, new MsBuildSemanticLoader(fs));
}

/// <summary>
/// The impact search by symbols (ported from the S8 <c>SemanticCallerFinder</c>, plus the upward search and the
/// constructor / outside-a-member naming). Whenever the projects cannot be scoped or loaded healthily it falls back to
/// <see cref="NameImpactFinder"/>, labelled with the reason, so a caller can always warn that the result is name-based.
/// </summary>
public sealed class SemanticImpactFinder(IFileSystem fs, ISemanticLoader loader) : IImpactFinder
{
    public async Task<ImpactResult> FindAsync(Workspace ws, IReadOnlyList<ImpactSeed> seeds, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var seedFiles = seeds.Select(s => FullPath(ws, s.Path)).Distinct(PathComparers.ForOs).ToList();

        IReadOnlyList<string> projects;
        try
        {
            projects = ImpactScope.Projects(fs, ws, seedFiles);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // The dependents are unknown, so the name search covers the whole workspace.
            return NameImpactFinder.Find(fs, ws, seeds, [], new SemanticFailure(SemanticFailureKind.ScopeUnknown));
        }

        if (projects.Count == 0)
            return NameImpactFinder.Find(fs, ws, seeds, projects, new SemanticFailure(SemanticFailureKind.NoProject));

        using var load = await loader.LoadAsync(projects, ct);
        if (load.Failure is not null || load.Solution is null)
            return NameImpactFinder.Find(fs, ws, seeds, projects, load.Failure ?? new SemanticFailure(SemanticFailureKind.LoadFailed));

        var targets = new List<ImpactTarget>();
        var unsupported = new List<string>();
        var notFound = new List<string>();
        foreach (var seed in seeds)
        {
            var seedSymbols = await FindSeedAsync(load.Solution, ws, seed, ct);
            if (seedSymbols.Count == 0)
            {
                (await DeclaresOtherKindAsync(load.Solution, ws, seed, ct) ? unsupported : notFound).Add(seed.TaskId);
                continue;
            }

            targets.AddRange(await FindForAsync(load.Solution, ws, seed, seedSymbols, ct));
        }

        return new ImpactResult(ImpactMode.Semantic, null, targets, unsupported) { SeedsNotFound = notFound };
    }

    private async Task<IEnumerable<ImpactTarget>> FindForAsync(Solution solution, Workspace ws, ImpactSeed seed, IReadOnlyList<ISymbol> seedSymbols, CancellationToken ct)
    {
        var seedPath = Normalize(seed.Path);
        var found = new List<(string Path, int Position, string Member, string Reason)>();
        var seen = new HashSet<(string, string)>(new KeyComparer()) { (seedPath, seed.Member) };

        void Add(Location location, string member, string reason)
        {
            if (TargetPath(solution, ws, location) is { } path && seen.Add((path, member)))
                found.Add((path, location.SourceSpan.Start, member, reason));
        }

        void AddDeclarations(ISymbol symbol, string reason)
        {
            foreach (var (location, name) in Declarations(symbol))
                Add(location, name, reason);
        }

        // The seed's other declarations: partial parts and same-named overloads in other files.
        foreach (var symbol in seedSymbols)
            AddDeclarations(symbol, "구현");

        var family = new HashSet<ISymbol>(seedSymbols, SymbolEqualityComparer.Default);

        // Upward: interface members the family implements and members it overrides, transitively. Members outside the
        // source (framework types) are not followed: every override of object.ToString is not an impact.
        var queue = new Queue<ISymbol>(seedSymbols);
        while (queue.Count > 0)
        {
            var symbol = queue.Dequeue();
            foreach (var baseMember in await BaseMembersAsync(symbol, solution, ct))
            {
                if (!baseMember.Locations.Any(l => l.IsInSource) || !family.Add(baseMember)) continue;
                AddDeclarations(baseMember, "기반 멤버");
                queue.Enqueue(baseMember);
            }
        }

        // Downward: implementations and overrides of every family member, transitively.
        queue = new Queue<ISymbol>(family);
        while (queue.Count > 0)
        {
            var symbol = queue.Dequeue();
            var derived = (await SymbolFinder.FindImplementationsAsync(symbol, solution, cancellationToken: ct))
                .Concat(await SymbolFinder.FindOverridesAsync(symbol, solution, cancellationToken: ct));
            foreach (var member in derived)
            {
                if (!family.Add(member)) continue;
                AddDeclarations(member, member.IsOverride ? "재정의" : "구현");
                queue.Enqueue(member);
            }
        }

        // Every member that calls or refers to a family member.
        foreach (var symbol in family)
            foreach (var reference in await SymbolFinder.FindReferencesAsync(symbol, solution, ct))
                foreach (var location in reference.Locations)
                {
                    var tree = location.Location.SourceTree;
                    if (tree is null) continue;
                    var node = (await tree.GetRootAsync(ct)).FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                    var (member, reason) = NameImpactFinder.Enclosing(node);
                    Add(location.Location, member, reason);
                }

        return found
            .OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Position)
            .Select(f => new ImpactTarget(seed.TaskId, f.Path, f.Member, f.Reason));
    }

    /// <summary>
    /// Every source declaration of a member, partial parts included, named by its identifier like <c>MemberLocator</c>
    /// (an explicit implementation's <see cref="ISymbol.Name"/> is "N.I.M", which no spec task can name).
    /// </summary>
    private static IEnumerable<(Location Location, string Name)> Declarations(ISymbol symbol)
    {
        var parts = new List<ISymbol> { symbol };
        switch (symbol)
        {
            case IMethodSymbol m:
                if (m.PartialDefinitionPart is { } md) parts.Add(md);
                if (m.PartialImplementationPart is { } mi) parts.Add(mi);
                break;
            case IPropertySymbol p:
                if (p.PartialDefinitionPart is { } pd) parts.Add(pd);
                if (p.PartialImplementationPart is { } pi) parts.Add(pi);
                break;
        }

        foreach (var reference in parts.SelectMany(p => p.DeclaringSyntaxReferences))
        {
            var node = reference.GetSyntax();
            yield return (Location.Create(reference.SyntaxTree, reference.Span), DeclaredName(node) ?? symbol.Name);
        }
    }

    private static string? DeclaredName(SyntaxNode node) => node switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        _ => null,
    };

    private static async Task<IEnumerable<ISymbol>> BaseMembersAsync(ISymbol symbol, Solution solution, CancellationToken ct)
    {
        ISymbol? overridden = symbol switch
        {
            IMethodSymbol m => m.OverriddenMethod,
            IPropertySymbol p => p.OverriddenProperty,
            _ => null,
        };
        var interfaces = await SymbolFinder.FindImplementedInterfaceMembersAsync(symbol, solution, cancellationToken: ct);
        return overridden is null ? interfaces : interfaces.Append(overridden);
    }

    /// <summary>
    /// The methods and properties named like the seed on its type in the seed file: the type named
    /// <see cref="ImpactSeed.ClassName"/>, or else the first type that declares the member. Empty when the file is in
    /// no loaded project or has no such member.
    /// </summary>
    private async Task<IReadOnlyList<ISymbol>> FindSeedAsync(Solution solution, Workspace ws, ImpactSeed seed, CancellationToken ct)
    {
        var seedPath = Normalize(seed.Path);
        foreach (var document in solution.Projects.SelectMany(p => p.Documents))
        {
            if (document.FilePath is null || !PathComparers.ForOs.Equals(Relative(ws, document.FilePath), seedPath)) continue;
            var root = await document.GetSyntaxRootAsync(ct);
            var model = await document.GetSemanticModelAsync(ct);
            if (root is null || model is null) continue;
            var type = root.DescendantNodes().OfType<TypeDeclarationSyntax>()
                .FirstOrDefault(t => (seed.ClassName is null || t.Identifier.Text == seed.ClassName) && t.Members.Any(m => Declares(m, seed.Member)));
            if (type is null || model.GetDeclaredSymbol(type, ct) is not INamedTypeSymbol symbol) continue;
            // By declared identifier, so an explicit implementation ("N.I.M") is found under its member name.
            return symbol.GetMembers()
                .Where(m => m is IMethodSymbol { MethodKind: MethodKind.Ordinary or MethodKind.ExplicitInterfaceImplementation } or IPropertySymbol)
                .Where(m => m.DeclaringSyntaxReferences.Any(r => DeclaredName(r.GetSyntax(ct)) == seed.Member))
                .ToList();
        }
        return [];
    }

    /// <summary>
    /// True when the seed names an existing member that is not a method or property (ImpactUnsupported); false when
    /// the file or member is not there (ImpactSeedNotFound), e.g. a legacy project whose Compile list omits the file.
    /// The file is read from disk when no loaded project holds it.
    /// </summary>
    private async Task<bool> DeclaresOtherKindAsync(Solution solution, Workspace ws, ImpactSeed seed, CancellationToken ct)
    {
        var seedPath = Normalize(seed.Path);
        var document = solution.Projects.SelectMany(p => p.Documents)
            .FirstOrDefault(d => d.FilePath is not null && PathComparers.ForOs.Equals(Relative(ws, d.FilePath), seedPath));
        SyntaxNode? root = document is null ? null : await document.GetSyntaxRootAsync(ct);
        root ??= NameImpactFinder.ReadRoot(fs, ws, seedPath);
        return root is not null && NameImpactFinder.DeclaresOtherKind(root, seed);
    }

    private static bool Declares(MemberDeclarationSyntax m, string name) =>
        (m is MethodDeclarationSyntax method && method.Identifier.Text == name)
        || (m is PropertyDeclarationSyntax property && property.Identifier.Text == name);

    private string FullPath(Workspace ws, string relative) =>
        fs.Path.Combine(ws.Root, relative.Replace('\\', '/').Replace('/', fs.Path.DirectorySeparatorChar));

    /// <summary>
    /// The workspace-relative path of a found place, or null when no spec task could name it: a source-generated
    /// document, or a file outside the workspace root (a loaded project that lies elsewhere).
    /// </summary>
    private string? TargetPath(Solution solution, Workspace ws, Location location)
    {
        var tree = location.SourceTree;
        if (tree is null || string.IsNullOrEmpty(tree.FilePath)) return null;
        if (solution.GetDocument(tree) is null or SourceGeneratedDocument) return null;
        var relative = Relative(ws, tree.FilePath);
        return fs.Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith("../", StringComparison.Ordinal) ? null : relative;
    }

    private string Relative(Workspace ws, string full) => Normalize(fs.Path.GetRelativePath(ws.Root, full));

    private static string Normalize(string path) => path.Replace('\\', '/');

    private sealed class KeyComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) =>
            PathComparers.ForOs.Equals(x.Item1, y.Item1) && string.Equals(x.Item2, y.Item2, StringComparison.Ordinal);

        public int GetHashCode((string, string) obj) => HashCode.Combine(PathComparers.ForOs.GetHashCode(obj.Item1), obj.Item2);
    }
}
