using System.IO.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.Files;
using PellyCoder.Core.Workspaces;
using Workspace = PellyCoder.Core.Workspaces.Workspace;

namespace PellyCoder.Core.Impact;

/// <summary>
/// The syntax-only fallback of the impact search. Matching is by name, so a same-named member of an unrelated type is
/// also reported: an accepted false hit, flagged to the user by the <c>ImpactNameBased</c> warning.
/// </summary>
public static class NameImpactFinder
{
    private const int MaxDirectories = 2000;

    internal sealed record Parsed(string Path, SyntaxNode Root);

    public static ImpactResult Find(IFileSystem fs, Workspace ws, IReadOnlyList<ImpactSeed> seeds, IReadOnlyList<string> projects, SemanticFailure fallback, int maxDirectories = MaxDirectories)
    {
        var (files, incomplete) = Load(fs, ws, projects, maxDirectories);
        var targets = new List<ImpactTarget>();
        var unsupported = new List<string>();
        var notFound = new List<string>();

        foreach (var seed in seeds)
        {
            var seedPath = Normalize(seed.Path);
            var seedFile = files.FirstOrDefault(f => PathComparers.ForOs.Equals(f.Path, seedPath)) ?? Read(fs, ws, seedPath);
            var seedTypes = seedFile is null ? [] : SeedTypes(seedFile.Root, seed);
            if (seedTypes.Count == 0)
            {
                (seedFile is not null && DeclaresOtherKind(seedFile.Root, seed) ? unsupported : notFound).Add(seed.TaskId);
                continue;
            }

            targets.AddRange(FindFor(files, seed, seedPath, seedTypes));
        }

        return new ImpactResult(ImpactMode.Name, fallback, targets, unsupported, incomplete) { SeedsNotFound = notFound };
    }

    private static List<TypeDeclarationSyntax> SeedTypes(SyntaxNode root, ImpactSeed seed) =>
        root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(t => (seed.ClassName is null || t.Identifier.Text == seed.ClassName) && t.Members.Any(m => IsSeedKind(m, seed.Member)))
            .ToList();

    /// <summary>
    /// True when the seed's type declares a member of that name that is not a method or property (a field, event,
    /// constructor, nested type…): the seed exists but the search does not start from it. False means not found.
    /// </summary>
    internal static bool DeclaresOtherKind(SyntaxNode root, ImpactSeed seed) =>
        root.DescendantNodes().OfType<TypeDeclarationSyntax>()
            .Where(t => seed.ClassName is null || t.Identifier.Text == seed.ClassName)
            .Any(t => t.Members.Any(m => DeclaredNames(t, m).Contains(seed.Member)));

    private static IEnumerable<string> DeclaredNames(TypeDeclarationSyntax type, MemberDeclarationSyntax m) => m switch
    {
        BaseFieldDeclarationSyntax f => f.Declaration.Variables.Select(v => v.Identifier.Text),
        EventDeclarationSyntax e => [e.Identifier.Text],
        ConstructorDeclarationSyntax or DestructorDeclarationSyntax => [type.Identifier.Text],
        BaseTypeDeclarationSyntax nested => [nested.Identifier.Text],
        DelegateDeclarationSyntax d => [d.Identifier.Text],
        _ => [],
    };

    private static bool IsSeedKind(MemberDeclarationSyntax m, string name) =>
        (m is MethodDeclarationSyntax method && method.Identifier.Text == name)
        || (m is PropertyDeclarationSyntax property && property.Identifier.Text == name);

    private static IEnumerable<ImpactTarget> FindFor(List<Parsed> files, ImpactSeed seed, string seedPath, List<TypeDeclarationSyntax> seedTypes)
    {
        var name = seed.Member;
        var found = new List<(string Path, int Position, string Member, string Reason)>();
        var seen = new HashSet<(string, string)>(new KeyComparer());
        seen.Add((seedPath, name));

        void Add(string path, int position, string member, string reason)
        {
            if (seen.Add((path, member))) found.Add((path, position, member, reason));
        }

        var seedTypeNames = seedTypes.Select(t => t.Identifier.Text).ToHashSet(StringComparer.Ordinal);
        var allTypes = files.SelectMany(f => f.Root.DescendantNodes().OfType<TypeDeclarationSyntax>()).ToList();

        // Upward closure of the seed types' base lists, by simple name.
        var baseNames = seedTypes.SelectMany(BaseNames).ToHashSet(StringComparer.Ordinal);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var type in allTypes.Where(t => baseNames.Contains(t.Identifier.Text)))
                foreach (var b in BaseNames(type))
                    grew |= baseNames.Add(b);
        }

        // Downward closure: every type that derives, however indirectly, from the seed type or one of its bases.
        var related = new HashSet<string>(seedTypeNames.Concat(baseNames), StringComparer.Ordinal);
        for (bool grew = true; grew;)
        {
            grew = false;
            foreach (var type in allTypes.Where(t => BaseNames(t).Any(related.Contains)))
                grew |= related.Add(type.Identifier.Text);
        }

        foreach (var file in files)
            foreach (var type in file.Root.DescendantNodes().OfType<TypeDeclarationSyntax>())
            {
                var member = type.Members.FirstOrDefault(m => IsSeedKind(m, name));
                if (member is null) continue;
                if (baseNames.Contains(type.Identifier.Text))
                    Add(file.Path, member.SpanStart, name, "기반 멤버");
                else if (BaseNames(type).Any(related.Contains))
                    Add(file.Path, member.SpanStart, name, member.Modifiers.Any(SyntaxKind.OverrideKeyword) ? "재정의" : "구현");
                else if (seedTypeNames.Contains(type.Identifier.Text))
                    // Another part of a partial seed type (or a same-named type): its base list may sit on the
                    // seed's part only, so the name of the type is the one link left.
                    Add(file.Path, member.SpanStart, name, "구현");
            }

        foreach (var file in files)
            // SimpleNameSyntax: a call with explicit type arguments (Get<Order>()) is a GenericNameSyntax.
            foreach (var reference in file.Root.DescendantNodes().OfType<SimpleNameSyntax>().Where(i => i.Identifier.Text == name))
            {
                var (member, reason) = Enclosing(reference);
                Add(file.Path, reference.SpanStart, member, reason);
            }

        return found
            .OrderBy(f => f.Path, StringComparer.Ordinal).ThenBy(f => f.Position)
            .Select(f => new ImpactTarget(seed.TaskId, f.Path, f.Member, f.Reason));
    }

    /// <summary>Member name of a place outside every type: top-level statements, assembly attributes.</summary>
    public const string TopLevel = "<top-level>";

    /// <summary>
    /// The member that holds a reference, named like <c>MemberLocator</c>; fields, indexers and the like give the
    /// enclosing type, and a place outside every type (top-level statements) gives <see cref="TopLevel"/>. Never
    /// drops a place: no spec task can name <see cref="TopLevel"/>, so it always warns, which beats silence.
    /// </summary>
    internal static (string Member, string Reason) Enclosing(SyntaxNode node)
    {
        foreach (var ancestor in node.Ancestors())
        {
            switch (ancestor)
            {
                case MethodDeclarationSyntax m: return (m.Identifier.Text, "호출");
                case ConstructorDeclarationSyntax c: return (c.Identifier.Text, "호출");
                case PropertyDeclarationSyntax p: return (p.Identifier.Text, "호출");
                case BaseTypeDeclarationSyntax t: return (t.Identifier.Text, "멤버 밖");
                case BaseFieldDeclarationSyntax or IndexerDeclarationSyntax or OperatorDeclarationSyntax
                    or ConversionOperatorDeclarationSyntax or DestructorDeclarationSyntax:
                    return (OuterTypeName(ancestor) ?? TopLevel, "멤버 밖");
            }
        }
        return (TopLevel, "멤버 밖");
    }

    private static string? OuterTypeName(SyntaxNode node) => node.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault()?.Identifier.Text;

    private static IEnumerable<string> BaseNames(TypeDeclarationSyntax type) =>
        type.BaseList?.Types.Select(b => SimpleName(b.Type)) ?? [];

    private static string SimpleName(TypeSyntax t) => t switch
    {
        QualifiedNameSyntax q => SimpleName(q.Right),
        GenericNameSyntax g => g.Identifier.Text,
        IdentifierNameSyntax i => i.Identifier.Text,
        _ => t.ToString(),
    };

    internal static (List<Parsed> Files, bool Incomplete) Load(IFileSystem fs, Workspace ws, IReadOnlyList<string> projects, int maxDirectories)
    {
        var roots = projects.Count > 0
            ? projects.Select(p => fs.Path.GetDirectoryName(p) ?? ws.Root).ToList()
            : [ws.Root];
        var seen = new HashSet<string>(PathComparers.ForOs);
        var result = new List<Parsed>();
        var incomplete = false;
        foreach (var root in roots)
        {
            var walk = BoundedWalker.Collect(fs, root, "*.cs", maxDirectories);
            incomplete |= walk.Truncated || walk.Unreadable.Count > 0;
            foreach (var file in walk.Files.Order(StringComparer.Ordinal))
            {
                var relative = Relative(fs, ws, file);
                if (!seen.Add(relative)) continue;
                if (Read(fs, ws, relative) is { } parsed) result.Add(parsed);
                else incomplete = true;
            }
        }
        return (result, incomplete);
    }

    /// <summary>The parsed syntax root of a workspace-relative file; null when it is missing or unreadable.</summary>
    internal static SyntaxNode? ReadRoot(IFileSystem fs, Workspace ws, string relative) => Read(fs, ws, relative)?.Root;

    private static Parsed? Read(IFileSystem fs, Workspace ws, string relative)
    {
        var full = fs.Path.Combine(ws.Root, relative.Replace('/', fs.Path.DirectorySeparatorChar));
        try
        {
            if (!fs.File.Exists(full)) return null;
            var text = TextDecoding.Decode(fs.File.ReadAllBytes(full));
            return new Parsed(relative, CSharpSyntaxTree.ParseText(text).GetRoot());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null; // an unreadable file cannot be searched
        }
    }

    private static string Relative(IFileSystem fs, Workspace ws, string full) => Normalize(fs.Path.GetRelativePath(ws.Root, full));

    private static string Normalize(string path) => path.Replace('\\', '/');

    private sealed class KeyComparer : IEqualityComparer<(string, string)>
    {
        public bool Equals((string, string) x, (string, string) y) =>
            PathComparers.ForOs.Equals(x.Item1, y.Item1) && string.Equals(x.Item2, y.Item2, StringComparison.Ordinal);

        public int GetHashCode((string, string) obj) => HashCode.Combine(PathComparers.ForOs.GetHashCode(obj.Item1), obj.Item2);
    }
}
