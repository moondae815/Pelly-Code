using System.IO.Abstractions;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.CharTests;

/// <summary>Which methods a characterization test can target, and the body mutation that proves a test notices the target (design 4.1, 4.4).</summary>
public static class CharTestTarget
{
    private const string MutationThrow = "throw new System.NotImplementedException(\"pelly-mutation\");";
    private const string MutationThrowExpression = "throw new System.NotImplementedException(\"pelly-mutation\")";
    private const string Yield = "await System.Threading.Tasks.Task.Yield(); ";

    public static (string MethodName, string TypeName, string? Error) Check(
        string sourceLf, MemberCandidate target, LanguageVersion version, string? internalsVisibleToAssembly)
    {
        var name = MemberLocator.NameOf(target.Display);
        var typeName = target.ContainingType[(target.ContainingType.LastIndexOf('.') + 1)..];

        var tree = CSharpSyntaxTree.ParseText(sourceLf, new CSharpParseOptions(version));
        var node = FindNode(tree, target);
        if (node is not MethodDeclarationSyntax method)
            return (name, typeName, $"메서드만 대상으로 할 수 있습니다. 속성·생성자·연산자는 지원하지 않습니다: {target.Display}");

        var types = method.Ancestors().OfType<TypeDeclarationSyntax>().ToList();
        if (types.Any(t => t.Modifiers.Any(SyntaxKind.AbstractKeyword)))
            return (name, typeName, "추상 클래스의 메서드는 지원하지 않습니다. 구체 클래스의 public 메서드를 대상으로 하세요");
        if (types.Any(t => t.TypeParameterList is not null))
            return (name, typeName, "제네릭 타입의 메서드는 지원하지 않습니다. 제네릭이 아닌 타입의 public 메서드를 대상으로 하세요");

        var mods = method.Modifiers;
        var isPublic = mods.Any(SyntaxKind.PublicKeyword);
        var isInternal = mods.Any(SyntaxKind.InternalKeyword) && !mods.Any(SyntaxKind.PrivateKeyword);
        var hasIvt = internalsVisibleToAssembly is not null;
        var typesReachable = types.All(t => TypeReachable(t, hasIvt));
        if (!(isPublic || (isInternal && hasIvt)) || !typesReachable)
            return (name, typeName, "테스트 프로젝트에서 부를 수 없는 메서드입니다. 이 메서드를 부르는 public 메서드를 대상으로 하세요");

        if (mods.Any(SyntaxKind.ExternKeyword))
            return (name, typeName, "extern 메서드는 변이를 만들 수 없어 지원하지 않습니다");
        if (method.Body is null && method.ExpressionBody is null)
            return (name, typeName, "본문이 없는 메서드는 변이를 만들 수 없어 지원하지 않습니다");
        if (method.Body is not null && ContainsOwnYield(method.Body))
            return (name, typeName, "반복기(yield) 메서드는 변이를 만들 수 없어 지원하지 않습니다");

        return (name, typeName, null);
    }

    /// <summary>True when the production project declares InternalsVisibleTo for the test assembly (attribute in a .cs file or an SDK csproj item).</summary>
    public static bool HasInternalsVisibleTo(IFileSystem fs, string productionCsproj, string testAssemblyName)
    {
        var dir = fs.Path.GetDirectoryName(fs.Path.GetFullPath(productionCsproj)) ?? ".";

        try
        {
            var doc = XDocument.Parse(fs.File.ReadAllText(productionCsproj));
            if (doc.Descendants().Any(e => e.Name.LocalName == "InternalsVisibleTo" &&
                                           IsAssembly((string?)e.Attribute("Include"), testAssemblyName)))
                return true;
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException) { }

        var attribute = new Regex("InternalsVisibleTo(?:Attribute)?\\s*\\(\\s*\"([^\"]*)\"", RegexOptions.CultureInvariant);
        foreach (var (_, text) in BoundedSources.Read(fs, dir, "*.cs"))
        {
            if (!text.Contains("InternalsVisibleTo", StringComparison.Ordinal)) continue;
            if (attribute.Matches(text).Any(m => IsAssembly(m.Groups[1].Value, testAssemblyName)))
                return true;
        }

        return false;
    }

    /// <summary>The whole text with the target's body replaced by a throw (async methods yield first so the caller's await still compiles).</summary>
    public static string Mutate(string sourceLf, MemberCandidate target, LanguageVersion version)
    {
        var tree = CSharpSyntaxTree.ParseText(sourceLf, new CSharpParseOptions(version));
        var method = FindNode(tree, target) as MethodDeclarationSyntax
            ?? throw new InvalidOperationException($"Not a method: {target.Display}");
        var isAsync = method.Modifiers.Any(SyntaxKind.AsyncKeyword);

        if (method.Body is { } body)
            return Splice(sourceLf, body.Span, "{ " + (isAsync ? Yield : "") + MutationThrow + " }");

        var arrow = method.ExpressionBody ?? throw new InvalidOperationException($"No body: {target.Display}");
        if (!isAsync)
            return Splice(sourceLf, arrow.Expression.Span, MutationThrowExpression);

        var end = method.SemicolonToken.IsMissing ? arrow.Span.End : method.SemicolonToken.Span.End;
        return Splice(sourceLf, Microsoft.CodeAnalysis.Text.TextSpan.FromBounds(arrow.SpanStart, end),
            "{ " + Yield + MutationThrow + " }");
    }

    private static string Splice(string text, Microsoft.CodeAnalysis.Text.TextSpan span, string replacement) =>
        text[..span.Start] + replacement + text[span.End..];

    private static bool IsAssembly(string? declared, string assembly)
    {
        if (declared is null) return false;
        var comma = declared.IndexOf(',');
        var name = (comma >= 0 ? declared[..comma] : declared).Trim();
        return string.Equals(name, assembly, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Public types always; internal types (the default for top-level types) only with InternalsVisibleTo; private, protected and private protected never (nested types default to private).</summary>
    private static bool TypeReachable(TypeDeclarationSyntax type, bool hasIvt)
    {
        var m = type.Modifiers;
        if (m.Any(SyntaxKind.PrivateKeyword)) return false;
        if (m.Any(SyntaxKind.PublicKeyword)) return true;
        if (m.Any(SyntaxKind.InternalKeyword)) return hasIvt;
        if (m.Any(SyntaxKind.ProtectedKeyword)) return false;
        return type.Parent is TypeDeclarationSyntax { RawKind: not (int)SyntaxKind.InterfaceDeclaration } ? false
            : type.Parent is TypeDeclarationSyntax ? true : hasIvt;
    }

    private static MemberDeclarationSyntax? FindNode(SyntaxTree tree, MemberCandidate target)
    {
        foreach (var member in tree.GetRoot().DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            if (MemberLocator.DisplayOf(member) is not { } display) continue;
            var containing = string.Join('.', member.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text));
            if ($"{containing}.{display}" == target.Display && MemberLocator.SpanOf(tree, member) == target.Span)
                return member;
        }

        return null;
    }

    /// <summary>A yield statement that belongs to the body itself, not to a nested local function or lambda.</summary>
    private static bool ContainsOwnYield(SyntaxNode body) =>
        body.DescendantNodes(n => n == body || n is not (LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax))
            .Any(n => n is YieldStatementSyntax);
}
