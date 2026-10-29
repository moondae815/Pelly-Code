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
/// type, for the read-only <c>dependencies</c> part of the <c>cs-test</c> prompt, so the model can write fakes for them.
/// Uses the <see cref="DtoCollector"/> search and limits; never throws.
/// </summary>
public static class DependencySignatureCollector
{
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

            var names = CandidateNames(member, containing);
            if (names.Count == 0) return null;

            var wanted = names.ToHashSet(StringComparer.Ordinal);
            var pattern = new Regex(@"\b(class|interface)\s+(" + string.Join('|', names.Select(Regex.Escape)) + @")\b", RegexOptions.CultureInvariant);
            var found = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var file in BoundedWalker.Files(fs, searchRoot, "*.cs", limits.MaxDirectories, ct).Take(limits.MaxFiles))
            {
                if (found.Count == wanted.Count) break;
                try
                {
                    var text = DtoCollector.ReadText(fs, file, limits.MaxFileBytes);
                    if (text is null || !pattern.IsMatch(text)) continue;
                    foreach (var type in CSharpSyntaxTree.ParseText(text, options).GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
                    {
                        var name = type.Identifier.ValueText;
                        if (!wanted.Contains(name) || found.ContainsKey(name)) continue;
                        if (type.OpenBraceToken.IsMissing || type.CloseBraceToken.IsMissing) continue;
                        if (Render(type, limits) is { } line) found[name] = line;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // One unreadable or unparsable file never stops the scan.
                }
            }

            var lines = names.Where(found.ContainsKey).Take(limits.MaxTypes).Select(n => found[n]).ToList();
            return lines.Count == 0 ? null : string.Join('\n', lines);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Constructor parameter types first, then the types of the fields and properties the target mentions, in source order.</summary>
    private static List<string> CandidateNames(MemberDeclarationSyntax member, TypeDeclarationSyntax containing)
    {
        var own = member.Ancestors().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.Text).ToHashSet(StringComparer.Ordinal);
        var used = member.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>().Select(i => i.Identifier.ValueText).ToHashSet(StringComparer.Ordinal);
        var names = new List<string>();
        void Add(TypeSyntax type)
        {
            foreach (var n in DtoCollector.TypeNames(type))
                if (n.Length > 0 && char.IsUpper(n[0]) && !own.Contains(n) && !names.Contains(n)) names.Add(n);
        }

        if (containing is { ParameterList: { } primary })
            foreach (var p in primary.Parameters.Where(p => p.Type is not null)) Add(p.Type!);
        foreach (var ctor in containing.Members.OfType<ConstructorDeclarationSyntax>().Where(c => !c.Modifiers.Any(SyntaxKind.StaticKeyword)))
            foreach (var p in ctor.ParameterList.Parameters.Where(p => p.Type is not null)) Add(p.Type!);
        foreach (var m in containing.Members)
        {
            if (m is FieldDeclarationSyntax f && f.Declaration.Variables.Any(v => used.Contains(v.Identifier.ValueText)))
                Add(f.Declaration.Type);
            else if (m is PropertyDeclarationSyntax p && used.Contains(p.Identifier.ValueText))
                Add(p.Type);
        }
        return names;
    }

    private static string? Render(TypeDeclarationSyntax type, DtoLimits limits)
    {
        string keyword;
        bool inInterface;
        switch (type)
        {
            case InterfaceDeclarationSyntax:
                keyword = "interface";
                inInterface = true;
                break;
            case ClassDeclarationSyntax when type.Modifiers.Any(SyntaxKind.AbstractKeyword):
                keyword = "abstract class";
                inInterface = false;
                break;
            default:
                return null;
        }

        var members = new List<string>();
        foreach (var m in type.Members)
        {
            if (m.Modifiers.Any(SyntaxKind.StaticKeyword)) continue;
            if (m.Modifiers.Any(SyntaxKind.PrivateKeyword) && !m.Modifiers.Any(SyntaxKind.ProtectedKeyword)) continue;
            // In an abstract class only the members a fake can override matter.
            var prefix = "";
            if (!inInterface)
            {
                if (m.Modifiers.Any(SyntaxKind.AbstractKeyword)) prefix = "abstract ";
                else if (m.Modifiers.Any(SyntaxKind.VirtualKeyword)) prefix = "virtual ";
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
            if (signature is not null) members.Add(prefix + signature);
        }

        if (members.Count == 0) return null;
        var body = members.Count <= limits.MaxMembers
            ? string.Join(' ', members)
            : string.Join(' ', members.Take(limits.MaxMembers)) + " …";
        return keyword + " " + type.Identifier.ValueText + Text(type.TypeParameterList) + " { " + body + " }";
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
