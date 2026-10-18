using System.IO.Abstractions;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.CSharp;

/// <param name="MaxMembers">Per type: members past it are replaced by a trailing <c>…</c>.</param>
public sealed record DtoLimits(int MaxTypes = 5, int MaxMembers = 30, int MaxFiles = 5000, long MaxFileBytes = 1_048_576);

/// <summary>
/// Finds the project-declared types the target member mentions and renders their data members (properties, public fields,
/// enum members) as one line per type, for the read-only <c>dto</c> prompt part. Never throws: a file that cannot be read,
/// decoded or parsed is skipped, and a failure of the whole scan yields no part.
/// </summary>
public static class DtoCollector
{
    private static readonly string[] SkippedDirectories = ["bin", "obj", ".pelly", ".git"];

    public static string? Collect(IFileSystem fs, string searchRoot, string sourceLf, MemberCandidate target, LanguageVersion version, DtoLimits? limits = null)
    {
        try
        {
            limits ??= new DtoLimits();
            var tree = CSharpSyntaxTree.ParseText(sourceLf, new CSharpParseOptions(version));
            var member = FindMember(tree, target);
            if (member is null) return null;

            var (names, used) = CandidateNames(member);
            if (names.Count == 0) return null;

            var parseOptions = new CSharpParseOptions(version);
            var own = member.Ancestors().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.Text).ToHashSet(StringComparer.Ordinal);
            var found = new Dictionary<string, string?>(StringComparer.Ordinal);
            var searched = new HashSet<string>(StringComparer.Ordinal);

            // Round 1 looks for the names the target mentions. A type found there that holds no data (a repository interface) is
            // followed one hop: the types in the signatures of the methods the target calls on it (round 2).
            for (var round = 0; round < 2; round++)
            {
                var pending = names.Where(n => searched.Add(n)).ToList();
                if (pending.Count == 0) break;
                var pattern = new Regex(@"\b(class|record|struct|interface|enum)\s+(" + string.Join('|', pending.Select(Regex.Escape)) + @")\b", RegexOptions.CultureInvariant);
                var wanted = pending.ToHashSet(StringComparer.Ordinal);
                var scanned = 0;
                Scan(fs, searchRoot, limits, ref scanned, file =>
                {
                    var text = ReadText(fs, file, limits.MaxFileBytes);
                    if (text is null || !pattern.IsMatch(text)) return;
                    foreach (var d in Declarations(text, parseOptions, wanted, used, limits))
                    {
                        if (!found.TryAdd(d.Name, d.Line)) continue;
                        foreach (var extra in d.Expansion.Where(x => !own.Contains(x) && !names.Contains(x)))
                            names.Add(extra);
                    }
                });
            }

            var lines = names.Where(n => found.GetValueOrDefault(n) is not null).Take(limits.MaxTypes).Select(n => found[n]!).ToList();
            return lines.Count == 0 ? null : string.Join('\n', lines);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static MemberDeclarationSyntax? FindMember(SyntaxTree tree, MemberCandidate target) =>
        tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>()
            .LastOrDefault(m => MemberLocator.SpanOf(tree, m) == target.Span
                && MemberLocator.DisplayOf(m) is { } display
                && target.Display.EndsWith("." + display, StringComparison.Ordinal));

    /// <summary>
    /// Candidate type names (capitalised identifiers of the member in first-seen order, then the types of the containing type's
    /// fields and properties the member uses, minus its enclosing types) and every identifier the member mentions.
    /// </summary>
    private static (List<string> Names, HashSet<string> Used) CandidateNames(MemberDeclarationSyntax member)
    {
        var own = member.Ancestors().OfType<BaseTypeDeclarationSyntax>().Select(t => t.Identifier.Text).ToHashSet(StringComparer.Ordinal);
        var names = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        void Add(string name)
        {
            if (name.Length > 0 && char.IsUpper(name[0]) && !own.Contains(name) && !names.Contains(name)) names.Add(name);
        }

        foreach (var node in member.DescendantNodesAndSelf())
        {
            var name = node switch
            {
                IdentifierNameSyntax i => i.Identifier.ValueText,
                GenericNameSyntax g => g.Identifier.ValueText,
                _ => null,
            };
            if (name is null) continue;
            used.Add(name);
            Add(name);
        }

        if (member.Parent is TypeDeclarationSyntax type)
        {
            foreach (var m in type.Members)
            {
                if (m is FieldDeclarationSyntax f && f.Declaration.Variables.Any(v => used.Contains(v.Identifier.ValueText)))
                    foreach (var n in TypeNames(f.Declaration.Type)) Add(n);
                else if (m is PropertyDeclarationSyntax p && used.Contains(p.Identifier.ValueText))
                    foreach (var n in TypeNames(p.Type)) Add(n);
            }
        }

        return (names, used);
    }

    private static IEnumerable<string> TypeNames(SyntaxNode type) =>
        type.DescendantNodesAndSelf().Select(n => n switch
        {
            IdentifierNameSyntax i => i.Identifier.ValueText,
            GenericNameSyntax g => g.Identifier.ValueText,
            _ => null,
        }).OfType<string>();

    private static void Scan(IFileSystem fs, string dir, DtoLimits limits, ref int scanned, Action<string> visit)
    {
        string[] files, dirs;
        try
        {
            files = fs.Directory.GetFiles(dir, "*.cs");
            dirs = fs.Directory.GetDirectories(dir);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return;
        }

        Array.Sort(files, StringComparer.Ordinal);
        Array.Sort(dirs, StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (scanned >= limits.MaxFiles) return;
            scanned++;
            try
            {
                visit(file);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreadable or unparsable file never stops the scan.
            }
        }

        foreach (var sub in dirs)
        {
            if (scanned >= limits.MaxFiles) return;
            var name = fs.Path.GetFileName(sub.TrimEnd('/', '\\'));
            if (SkippedDirectories.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
            Scan(fs, sub, limits, ref scanned, visit);
        }
    }

    private static string? ReadText(IFileSystem fs, string file, long maxBytes)
    {
        if (fs.FileInfo.New(file).Length > maxBytes) return null;
        return TextDecoding.Decode(fs.File.ReadAllBytes(file));
    }

    private sealed record Declared(string Name, string? Line, IReadOnlyList<string> Expansion);

    private static IEnumerable<Declared> Declarations(string text, CSharpParseOptions options, HashSet<string> wanted, HashSet<string> used, DtoLimits limits)
    {
        var root = CSharpSyntaxTree.ParseText(text, options).GetRoot();
        foreach (var type in root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            if (!wanted.Contains(type.Identifier.ValueText) || type is not (EnumDeclarationSyntax or TypeDeclarationSyntax)) continue;
            // A body that never closes is a broken declaration; a positional record has no braces at all.
            var hasBraces = !type.OpenBraceToken.IsKind(SyntaxKind.None);
            if (hasBraces && (type.OpenBraceToken.IsMissing || type.CloseBraceToken.IsMissing)) continue;

            var line = type is EnumDeclarationSyntax e ? RenderEnum(e, limits) : RenderType((TypeDeclarationSyntax)type, limits);
            var expansion = line is not null || type is not TypeDeclarationSyntax td
                ? []
                : td.Members.OfType<MethodDeclarationSyntax>().Where(m => used.Contains(m.Identifier.ValueText))
                    .SelectMany(m => TypeNames(m.ReturnType).Concat(m.ParameterList.Parameters.Where(p => p.Type is not null).SelectMany(p => TypeNames(p.Type!))))
                    .Where(n => n.Length > 0 && char.IsUpper(n[0])).Distinct(StringComparer.Ordinal).ToList();
            yield return new Declared(type.Identifier.ValueText, line, expansion);
        }
    }

    private static string? RenderEnum(EnumDeclarationSyntax e, DtoLimits limits)
    {
        var members = e.Members.Select(m => m.Identifier.ValueText).ToList();
        return members.Count == 0 ? null : "enum " + e.Identifier.ValueText + ": " + Join(members, ", ", limits.MaxMembers);
    }

    private static string? RenderType(TypeDeclarationSyntax type, DtoLimits limits)
    {
        var members = new List<string>();
        if (type is RecordDeclarationSyntax { ParameterList: { } positional })
            members.AddRange(positional.Parameters.Where(p => p.Type is not null).Select(p => TypeText(p.Type!) + " " + p.Identifier.ValueText));

        var inInterface = type is InterfaceDeclarationSyntax;
        foreach (var m in type.Members)
        {
            switch (m)
            {
                case PropertyDeclarationSyntax p when IsVisibleInstance(p.Modifiers, inInterface):
                    members.Add(TypeText(p.Type) + " " + p.Identifier.ValueText);
                    break;
                case FieldDeclarationSyntax f when !inInterface && IsVisibleInstance(f.Modifiers, false)
                    && !f.Modifiers.Any(SyntaxKind.ConstKeyword):
                    members.AddRange(f.Declaration.Variables.Select(v => TypeText(f.Declaration.Type) + " " + v.Identifier.ValueText));
                    break;
            }
        }

        if (members.Count == 0) return null;
        var keyword = type switch
        {
            RecordDeclarationSyntax => "record",
            StructDeclarationSyntax => "struct",
            InterfaceDeclarationSyntax => "interface",
            _ => "class",
        };
        return keyword + " " + type.Identifier.ValueText + ": " + Join(members, "; ", limits.MaxMembers);
    }

    private static bool IsVisibleInstance(SyntaxTokenList modifiers, bool inInterface) =>
        !modifiers.Any(SyntaxKind.StaticKeyword)
        && (inInterface || modifiers.Any(SyntaxKind.PublicKeyword) || modifiers.Any(SyntaxKind.InternalKeyword));

    private static string TypeText(TypeSyntax type) => Regex.Replace(type.WithoutTrivia().ToString(), @"\s+", " ");

    private static string Join(List<string> members, string separator, int max) =>
        members.Count <= max ? string.Join(separator, members) : string.Join(separator, members.Take(max)) + "; …";
}
