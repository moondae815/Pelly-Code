using System.IO.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCode.Core.CSharp;
using PellyCode.Core.Files;
using PellyCode.Core.Impact;

namespace PellyCode.McpServer.Impact;

/// <summary>
/// <c>pelly_impact</c> (design §6): picks the member at a line or by name, searches with
/// <see cref="SemanticImpactFinder"/> (name-based fallback inside it) and returns the tool's Korean text.
/// </summary>
public sealed class ImpactService(IFileSystem fs, ServerContext ctx, ISemanticLoader loader)
{
    private const string SeedId = "impact";

    public async Task<string> FindAsync(string path, int? line, string? name, CancellationToken ct)
    {
        var full = fs.Path.GetFullPath(fs.Path.IsPathRooted(path) ? path : fs.Path.Combine(ctx.Root, path));
        var rel = Relative(full);
        if (rel is null) return $"작업 공간 밖의 파일입니다: {path}";

        var extension = fs.Path.GetExtension(full).ToLowerInvariant();
        if (extension != ".cs") return $"지원하지 않는 파일 형식: {(extension.Length == 0 ? "(확장자 없음)" : extension)}";
        if (!fs.File.Exists(full)) return $"파일이 없습니다: {rel}";
        if (line is null && string.IsNullOrWhiteSpace(name)) return "line이나 name을 주세요";

        string text;
        try { text = TextFileCodec.Read(fs, full).Text; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return $"파일을 읽지 못했습니다: {rel}";
        }

        var version = LanguageVersionDetector.Detect(fs, ProjectFileLocator.FindNearestCsproj(fs, full)).Version;
        MemberCandidate? candidate;
        if (line is { } l)
        {
            candidate = MemberLocator.FindAtLine(text, version, l);
            if (candidate is null) return $"{rel}:{l} 줄에 메서드나 속성이 없습니다";
        }
        else
        {
            var given = name!.Trim();
            var dot = given.LastIndexOf('.');
            var className = dot < 0 ? null : given[..dot][(given[..dot].LastIndexOf('.') + 1)..];
            candidate = MemberLocator.FindByName(text, version, className, given[(dot + 1)..]).FirstOrDefault();
            if (candidate is null) return $"멤버를 찾지 못했습니다: {given} ({rel})";
        }

        var member = MemberName(candidate);
        var innermostType = candidate.ContainingType[(candidate.ContainingType.LastIndexOf('.') + 1)..];
        var declarationLine = DeclarationLine(text, version, candidate, member);
        var heading = $"대상: {candidate.ContainingType}.{member} ({rel}:{declarationLine})";
        if (member == innermostType || member.StartsWith("operator", StringComparison.Ordinal))
            return heading + "\n" + ImpactFormatter.Unsupported;

        var seed = new ImpactSeed(SeedId, declarationLine, rel, innermostType, member);
        var result = await new SemanticImpactFinder(fs, loader).FindAsync(ctx.Workspace, [seed], ct);
        var types = new Dictionary<string, SyntaxNode?>(StringComparer.Ordinal);
        return ImpactFormatter.Format(heading, result, t => TypeAt(types, t));
    }

    /// <summary>The member's own name: the display without its containing type and parameter list.</summary>
    private static string MemberName(MemberCandidate candidate)
    {
        var display = candidate.Display[(candidate.ContainingType.Length + 1)..];
        var paren = display.IndexOf('(');
        return paren >= 0 ? display[..paren] : display;
    }

    /// <summary>The 1-based line of the member's name (its doc comment and attributes come before it).</summary>
    private static int DeclarationLine(string text, LanguageVersion version, MemberCandidate candidate, string member)
    {
        var root = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(version)).GetRoot();
        foreach (var node in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            var identifier = node switch
            {
                MethodDeclarationSyntax m => m.Identifier,
                PropertyDeclarationSyntax p => p.Identifier,
                ConstructorDeclarationSyntax c => c.Identifier,
                _ => default,
            };
            if (identifier.Text != member) continue;
            var at = identifier.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
            if (candidate.Span.StartLine <= at && at <= candidate.Span.EndLine) return at;
        }
        return candidate.Span.StartLine;
    }

    /// <summary>The innermost type around a found place, read from its file (parsed once per call); null when unknown.</summary>
    private string? TypeAt(Dictionary<string, SyntaxNode?> roots, ImpactTarget target)
    {
        if (target.Line is not { } line || target.Path.Length == 0) return null;
        if (!roots.TryGetValue(target.Path, out var root))
        {
            var full = fs.Path.Combine(ctx.Root, target.Path.Replace('/', fs.Path.DirectorySeparatorChar));
            try { root = CSharpSyntaxTree.ParseText(TextFileCodec.Read(fs, full).Text).GetRoot(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { root = null; }
            roots[target.Path] = root;
        }
        if (root is null) return null;

        return root.DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Where(t =>
            {
                var span = t.GetLocation().GetLineSpan();
                return span.StartLinePosition.Line + 1 <= line && line <= span.EndLinePosition.Line + 1;
            })
            .LastOrDefault()?.Identifier.Text;
    }

    /// <summary>Workspace-relative path with '/', or null when the file lies outside the root.</summary>
    private string? Relative(string full)
    {
        var rel = fs.Path.GetRelativePath(ctx.Root, full).Replace('\\', '/');
        return rel == ".." || rel.StartsWith("../", StringComparison.Ordinal) || fs.Path.IsPathRooted(rel) ? null : rel;
    }
}
