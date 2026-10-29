using System.IO.Abstractions;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.CharTests;

/// <summary>
/// Renders the member signatures (no bodies) of the workspace-declared interfaces and abstract classes the target depends on:
/// the containing type's constructor parameter types and the types of the fields and properties the target uses. One line per
/// type, prefixed with its namespace (<c>Shop.Api.Services: interface IOrderService { … }</c>), for the read-only
/// <c>dependencies</c> part of the <c>cs-test</c> prompt, so the model can write fakes for them. An interface lists the members it
/// inherits from workspace-declared interfaces too; an abstract class keeps each member's access modifiers. Constructor parameter
/// types are never cut at <see cref="DtoLimits.MaxMembers"/> (a fake must implement every member). Uses the
/// <see cref="DtoCollector"/> search and limits; never throws.
/// </summary>
public static class DependencySignatureCollector
{
    /// <summary>Rounds of following base interfaces (an interface's bases, their bases, ...).</summary>
    private const int BaseRounds = 3;

    public static string? Collect(IFileSystem fs, string searchRoot, string sourceLf, MemberCandidate target, LanguageVersion version, DtoLimits? limits = null,
        CancellationToken ct = default)
    {
        try
        {
            limits ??= new DtoLimits();
            var options = new CSharpParseOptions(version);
            var tree = CSharpSyntaxTree.ParseText(sourceLf, options);
            var member = DtoCollector.FindMember(tree, target);
            if (member?.Parent is not TypeDeclarationSyntax containing) return null;

            var (names, constructorTypes) = CandidateNames(member, containing);
            if (names.Count == 0) return null;

            var found = new Dictionary<string, TypeDeclarationSyntax>(StringComparer.Ordinal);
            var searched = new HashSet<string>(StringComparer.Ordinal);
            Find(fs, searchRoot, names.Where(searched.Add).ToList(), options, limits, found, ct);
            for (var round = 0; round < BaseRounds; round++)
            {
                var bases = found.Values.OfType<InterfaceDeclarationSyntax>().SelectMany(BaseInterfaces).Select(b => b.Name)
                    .Where(searched.Add).ToList();
                if (bases.Count == 0) break;
                Find(fs, searchRoot, bases, options, limits, found, ct);
            }

            var lines = names.Where(found.ContainsKey).Take(limits.MaxTypes)
                .Select(n => Render(found[n], found, limits, truncate: !constructorTypes.Contains(n)))
                .OfType<string>().ToList();
            return lines.Count == 0 ? null : string.Join('\n', lines);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>
    /// The namespaces that declare each of <paramref name="names"/> (class, record, struct, interface or enum) under
    /// <paramref name="searchRoot"/>, sorted; names declared only in the global namespace or not at all are absent. Never throws.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> NamespacesOf(IFileSystem fs, string searchRoot, IReadOnlyCollection<string> names,
        LanguageVersion version, DtoLimits? limits = null, CancellationToken ct = default)
    {
        var result = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        try
        {
            limits ??= new DtoLimits();
            var wanted = names.Where(n => n.Length > 0).ToHashSet(StringComparer.Ordinal);
            if (wanted.Count == 0) return new Dictionary<string, IReadOnlyList<string>>();
            var options = new CSharpParseOptions(version);
            var pattern = new Regex(@"\b(class|record|struct|interface|enum)\s+(" + string.Join('|', wanted.Select(Regex.Escape)) + @")\b", RegexOptions.CultureInvariant);
            foreach (var file in BoundedWalker.Files(fs, searchRoot, "*.cs", limits.MaxDirectories, ct).Take(limits.MaxFiles))
            {
                try
                {
                    var text = DtoCollector.ReadText(fs, file, limits.MaxFileBytes);
                    if (text is null || !pattern.IsMatch(text)) continue;
                    foreach (var type in CSharpSyntaxTree.ParseText(text, options).GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
                    {
                        var name = type.Identifier.ValueText;
                        if (!wanted.Contains(name) || DtoCollector.NamespaceOf(type) is not { } ns) continue;
                        if (!result.TryGetValue(name, out var set)) result[name] = set = new SortedSet<string>(StringComparer.Ordinal);
                        set.Add(ns);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One unreadable or unparsable file never stops the scan.
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A failed walk yields what was found so far.
        }
        return result.ToDictionary(kv => kv.Key, IReadOnlyList<string> (kv) => kv.Value.ToList(), StringComparer.Ordinal);
    }

    /// <summary>Adds the first complete interface or abstract class declaration of each name to <paramref name="found"/>.</summary>
    private static void Find(IFileSystem fs, string searchRoot, IReadOnlyList<string> names, CSharpParseOptions options, DtoLimits limits,
        Dictionary<string, TypeDeclarationSyntax> found, CancellationToken ct)
    {
        if (names.Count == 0) return;
        var wanted = names.ToHashSet(StringComparer.Ordinal);
        var pattern = new Regex(@"\b(class|interface)\s+(" + string.Join('|', names.Select(Regex.Escape)) + @")\b", RegexOptions.CultureInvariant);
        foreach (var file in BoundedWalker.Files(fs, searchRoot, "*.cs", limits.MaxDirectories, ct).Take(limits.MaxFiles))
        {
            if (wanted.All(found.ContainsKey)) break;
            try
            {
                var text = DtoCollector.ReadText(fs, file, limits.MaxFileBytes);
                if (text is null || !pattern.IsMatch(text)) continue;
                foreach (var type in CSharpSyntaxTree.ParseText(text, options).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
                {
                    var name = type.Identifier.ValueText;
                    if (!wanted.Contains(name) || found.ContainsKey(name)) continue;
                    if (type.OpenBraceToken.IsMissing || type.CloseBraceToken.IsMissing) continue;
                    if (type is InterfaceDeclarationSyntax || (type is ClassDeclarationSyntax && type.Modifiers.Any(SyntaxKind.AbstractKeyword)))
                        found[name] = type;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreadable or unparsable file never stops the scan.
            }
        }
    }

    /// <summary>Constructor parameter types first, then the types of the fields and properties the target mentions, in source order;
    /// and which of them are constructor parameter types.</summary>
    private static (List<string> Names, HashSet<string> ConstructorTypes) CandidateNames(MemberDeclarationSyntax member, TypeDeclarationSyntax containing)
    {
        var own = member.Ancestors().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.Text).ToHashSet(StringComparer.Ordinal);
        var used = member.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        var names = new List<string>();
        var constructorTypes = new HashSet<string>(StringComparer.Ordinal);
        void Add(TypeSyntax type, bool constructor)
        {
            foreach (var n in DtoCollector.TypeNames(type))
            {
                if (n.Length == 0 || !char.IsUpper(n[0]) || own.Contains(n)) continue;
                if (constructor) constructorTypes.Add(n);
                if (!names.Contains(n)) names.Add(n);
            }
        }

        if (containing is { ParameterList: { } primary })
            foreach (var p in primary.Parameters.Where(p => p.Type is not null)) Add(p.Type!, true);
        foreach (var ctor in containing.Members.OfType<ConstructorDeclarationSyntax>().Where(c => !c.Modifiers.Any(SyntaxKind.StaticKeyword)))
            foreach (var p in ctor.ParameterList.Parameters.Where(p => p.Type is not null)) Add(p.Type!, true);
        foreach (var m in containing.Members)
        {
            if (m is FieldDeclarationSyntax f && f.Declaration.Variables.Any(v => used.Contains(v.Identifier.ValueText)))
                Add(f.Declaration.Type, false);
            else if (m is PropertyDeclarationSyntax p && used.Contains(p.Identifier.ValueText))
                Add(p.Type, false);
        }
        return (names, constructorTypes);
    }

    /// <summary>The simple name and type arguments of each base type of an interface.</summary>
    private static IEnumerable<(string Name, IReadOnlyList<string> Arguments)> BaseInterfaces(InterfaceDeclarationSyntax type) =>
        (type.BaseList?.Types ?? default).Select(b => b.Type switch
        {
            QualifiedNameSyntax q => q.Right,
            AliasQualifiedNameSyntax a => a.Name,
            SimpleNameSyntax s => s,
            _ => null,
        }).OfType<SimpleNameSyntax>().Select(n => (n.Identifier.ValueText,
            n is GenericNameSyntax g ? (IReadOnlyList<string>)g.TypeArgumentList.Arguments.Select(a => Text(a)).ToList() : []));

    private static string? Render(TypeDeclarationSyntax type, IReadOnlyDictionary<string, TypeDeclarationSyntax> found, DtoLimits limits, bool truncate)
    {
        var keyword = type is InterfaceDeclarationSyntax ? "interface" : "abstract class";
        var members = Members(type, found, new HashSet<TypeDeclarationSyntax>());
        if (members.Count == 0) return null;
        var body = !truncate || members.Count <= limits.MaxMembers
            ? string.Join(' ', members)
            : string.Join(' ', members.Take(limits.MaxMembers)) + " …";
        var line = keyword + " " + type.Identifier.ValueText + Text(type.TypeParameterList) + " { " + body + " }";
        return DtoCollector.NamespaceOf(type) is { } ns ? ns + ": " + line : line;
    }

    /// <summary>The type's own member signatures, then (for an interface) those of its workspace-declared base interfaces with the
    /// type arguments filled in; each signature once.</summary>
    private static List<string> Members(TypeDeclarationSyntax type, IReadOnlyDictionary<string, TypeDeclarationSyntax> found, HashSet<TypeDeclarationSyntax> visited)
    {
        var members = new List<string>();
        if (!visited.Add(type)) return members;
        var inInterface = type is InterfaceDeclarationSyntax;
        foreach (var m in type.Members)
        {
            if (m.Modifiers.Any(SyntaxKind.StaticKeyword)) continue;
            if (m.Modifiers.Any(SyntaxKind.PrivateKeyword) && !m.Modifiers.Any(SyntaxKind.ProtectedKeyword)) continue;
            // In an abstract class only the members a fake can override matter, with the access a fake must repeat.
            var prefix = "";
            if (!inInterface)
            {
                var access = string.Concat(m.Modifiers.Where(t => t.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.ProtectedKeyword
                    or SyntaxKind.InternalKeyword or SyntaxKind.PrivateKeyword).Select(t => t.ValueText + " "));
                if (m.Modifiers.Any(SyntaxKind.AbstractKeyword)) prefix = access + "abstract ";
                else if (m.Modifiers.Any(SyntaxKind.VirtualKeyword)) prefix = access + "virtual ";
                else continue;
            }
            var signature = m switch
            {
                MethodDeclarationSyntax method => Text(method.ReturnType) + " " + method.Identifier.ValueText + Text(method.TypeParameterList)
                    + Text(method.ParameterList) + Text(method.ConstraintClauses) + ";",
                PropertyDeclarationSyntax p => Text(p.Type) + " " + p.Identifier.ValueText + " " + Accessors(p.AccessorList, p.ExpressionBody),
                IndexerDeclarationSyntax i => Text(i.Type) + " this" + Text(i.ParameterList) + " " + Accessors(i.AccessorList, i.ExpressionBody),
                EventFieldDeclarationSyntax e => "event " + Text(e.Declaration) + ";",
                _ => null,
            };
            if (signature is not null && !members.Contains(prefix + signature)) members.Add(prefix + signature);
        }

        if (type is InterfaceDeclarationSyntax iface)
        {
            foreach (var (name, arguments) in BaseInterfaces(iface))
            {
                if (!found.TryGetValue(name, out var baseType) || baseType is not InterfaceDeclarationSyntax) continue;
                var parameters = baseType.TypeParameterList?.Parameters.Select(p => p.Identifier.ValueText).ToList() ?? [];
                var map = parameters.Count == arguments.Count
                    ? parameters.Zip(arguments).ToDictionary(x => x.First, x => x.Second, StringComparer.Ordinal)
                    : new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var inherited in Members(baseType, found, visited))
                {
                    var filled = map.Count == 0 ? inherited
                        : Regex.Replace(inherited, @"\b(" + string.Join('|', map.Keys.Select(Regex.Escape)) + @")\b", x => map[x.Value]);
                    if (!members.Contains(filled)) members.Add(filled);
                }
            }
        }
        return members;
    }

    private static string Accessors(AccessorListSyntax? list, ArrowExpressionClauseSyntax? arrow)
    {
        if (list is null) return arrow is null ? "{ }" : "{ get; }";
        var accessors = list.Accessors
            .Where(a => !a.Modifiers.Any(SyntaxKind.PrivateKeyword))
            .Select(a => string.Concat(a.Modifiers.Select(t => t.ValueText + " ")) + a.Keyword.ValueText + ";");
        return "{ " + string.Join(' ', accessors) + " }";
    }

    private static string Text(SyntaxNode? node) =>
        node is null ? "" : Regex.Replace(node.WithoutTrivia().ToString(), @"\s+", " ");

    private static string Text(SyntaxList<TypeParameterConstraintClauseSyntax> clauses) =>
        clauses.Count == 0 ? "" : " " + string.Join(' ', clauses.Select(c => Text(c)));
}
