using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.Reporting;

namespace PellyCoder.Core.CharTests;

public sealed record CharTestRejection(string Kind, string? TestName, string Reason);

/// <summary>Static verdicts 1-3 (shape, call and assertion, environment dependence); later verdicts are not run
/// when an earlier one rejects.</summary>
public static class CharTestStaticJudge
{
    private static readonly (string Type, string[]? Members)[] EnvMembers =
    [
        ("DateTime", ["Now", "UtcNow", "Today"]),
        ("DateTimeOffset", ["Now", "UtcNow"]),
        ("Guid", ["NewGuid"]),
        ("Random", ["Shared"]),
        ("Thread", ["Sleep"]),
        ("Task", ["Delay"]),
        ("File", null),
        ("Directory", null),
        ("Environment", null),
    ];

    private static readonly string[] EnvTypes = ["SqlConnection", "HttpClient"];

    public static IReadOnlyList<CharTestRejection> Judge(
        CharTestOutput output, FrameworkInfo fw, int count, string targetMethodName,
        IReadOnlySet<string> existingMemberNames, LanguageVersion version)
    {
        var tree = CSharpSyntaxTree.ParseText(
            "class __P {\n" + string.Join("\n", output.Members) + "\n}", new CSharpParseOptions(version));
        var cls = tree.GetCompilationUnitRoot().Members.OfType<ClassDeclarationSyntax>().First();

        var shape = JudgeShape(cls, fw, count, existingMemberNames);
        if (shape.Count > 0) return shape;

        var tests = cls.Members.OfType<MethodDeclarationSyntax>().Where(m => IsTest(m, fw)).ToList();
        var calls = JudgeCallsAndAsserts(cls, tests, fw, targetMethodName);
        if (calls.Count > 0) return calls;

        return JudgeEnvironment(cls);
    }

    private static string AttrName(AttributeSyntax a)
    {
        var name = a.Name switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            AliasQualifiedNameSyntax q => q.Name.Identifier.Text,
            SimpleNameSyntax s => s.Identifier.Text,
            _ => a.Name.ToString(),
        };
        return name.EndsWith("Attribute", StringComparison.Ordinal) && name.Length > "Attribute".Length
            ? name[..^"Attribute".Length]
            : name;
    }

    private static IEnumerable<AttributeSyntax> Attrs(MemberDeclarationSyntax m) =>
        m.AttributeLists.SelectMany(l => l.Attributes);

    private static bool IsTest(MethodDeclarationSyntax m, FrameworkInfo fw) =>
        Attrs(m).Any(a =>
        {
            var n = AttrName(a);
            return n == fw.TestAttribute || fw.DataTestAttributes.Contains(n);
        });

    private static bool IsPrivate(MemberDeclarationSyntax m) =>
        m.Modifiers.Any(SyntaxKind.PrivateKeyword) && !m.Modifiers.Any(SyntaxKind.ProtectedKeyword);

    private static List<CharTestRejection> JudgeShape(
        ClassDeclarationSyntax cls, FrameworkInfo fw, int count, IReadOnlySet<string> existing)
    {
        var result = new List<CharTestRejection>();
        void Add(string? name, string reason) => result.Add(new(FailureKinds.CharTestShape, name, reason));

        var seen = new HashSet<string>();
        var testCount = 0;
        foreach (var member in cls.Members)
        {
            string? name = member switch
            {
                MethodDeclarationSyntax m => m.Identifier.Text,
                ClassDeclarationSyntax c => c.Identifier.Text,
                _ => null,
            };

            if (member is MethodDeclarationSyntax method && IsTest(method, fw))
            {
                testCount++;
            }
            else if (member is MethodDeclarationSyntax or ClassDeclarationSyntax)
            {
                if (!IsPrivate(member)) Add(name, "테스트 외의 메서드·중첩 클래스는 private이어야 합니다");
            }
            else
            {
                Add(null, "테스트, private 도우미 메서드, private 중첩 클래스만 허용됩니다");
            }

            if (name is not null)
            {
                if (existing.Contains(name)) Add(name, "기존 멤버와 이름이 겹칩니다");
                else if (!seen.Add(name)) Add(name, "새 멤버끼리 이름이 겹칩니다");
            }
        }

        if (testCount == 0) Add(null, "테스트 메서드가 없습니다");
        else if (testCount > count) Add(null, $"테스트 메서드가 {testCount}개입니다 (최대 {count}개)");

        foreach (var (owner, kind) in SkipMarks(cls, fw))
            Add(owner, $"건너뛰기 표시가 있습니다: {kind}");
        return result;
    }

    private static IEnumerable<(string? Owner, string Marker)> SkipMarks(ClassDeclarationSyntax cls, FrameworkInfo fw)
    {
        foreach (var marker in fw.SkipMarkers)
        {
            if (marker.Contains('.'))
            {
                foreach (var inv in cls.DescendantNodes().OfType<InvocationExpressionSyntax>())
                    if (NoSpace(inv.Expression) == marker) yield return (OwnerName(inv), marker);
            }
            else
            {
                var name = marker.Replace("=", "").Trim();
                foreach (var attr in cls.DescendantNodes().OfType<AttributeSyntax>())
                {
                    if (AttrName(attr) == name) yield return (OwnerName(attr), marker);
                    else if (attr.ArgumentList?.Arguments.Any(a =>
                                 a.NameEquals?.Name.Identifier.Text == name || a.NameColon?.Name.Identifier.Text == name) == true)
                        yield return (OwnerName(attr), marker);
                }
            }
        }
    }

    private static string? OwnerName(SyntaxNode node) =>
        node.AncestorsAndSelf().OfType<MethodDeclarationSyntax>().FirstOrDefault()?.Identifier.Text
        ?? node.AncestorsAndSelf().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.Text != "__P")?.Identifier.Text;

    private static string NoSpace(SyntaxNode n) =>
        string.Concat(n.ToString().Where(c => !char.IsWhiteSpace(c)));

    private static string? InvokedName(InvocationExpressionSyntax inv) => inv.Expression switch
    {
        IdentifierNameSyntax i => i.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
        MemberBindingExpressionSyntax b => b.Name.Identifier.Text,
        _ => null,
    };

    private static IEnumerable<SyntaxNode> Bodies(MethodDeclarationSyntax m) =>
        m.Body is not null ? [m.Body] : m.ExpressionBody is not null ? [m.ExpressionBody] : [];

    /// <summary>The method body plus the bodies of same-class private helpers it calls (one level).</summary>
    private static List<SyntaxNode> Reachable(ClassDeclarationSyntax cls, MethodDeclarationSyntax test)
    {
        var nodes = Bodies(test).ToList();
        var helpers = cls.Members.OfType<MethodDeclarationSyntax>()
            .Where(m => m != test && IsPrivate(m)).ToList();
        var called = nodes.SelectMany(n => n.DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Select(InvokedName).OfType<string>().ToHashSet();
        foreach (var h in helpers.Where(h => called.Contains(h.Identifier.Text)))
            nodes.AddRange(Bodies(h));
        return nodes;
    }

    private static bool IsAssertionInvocation(InvocationExpressionSyntax inv, FrameworkInfo fw)
    {
        var text = NoSpace(inv.Expression);
        var plain = System.Text.RegularExpressions.Regex.Replace(text, "<[^<>]*>", "");
        var segments = plain.Split('.');
        if (segments.Length >= 2 && segments[..^1].Any(s => s is "Assert" or "CollectionAssert" or "StringAssert"))
            return true;
        return fw.ThrowsAssertions.Any(t => t.Contains('.') && plain.StartsWith(t, StringComparison.Ordinal));
    }

    private static bool IsLiteral(ExpressionSyntax e) => e switch
    {
        LiteralExpressionSyntax => true,
        PrefixUnaryExpressionSyntax p => IsLiteral(p.Operand),
        ParenthesizedExpressionSyntax p => IsLiteral(p.Expression),
        _ => false,
    };

    private static List<CharTestRejection> JudgeCallsAndAsserts(
        ClassDeclarationSyntax cls, List<MethodDeclarationSyntax> tests, FrameworkInfo fw, string target)
    {
        var result = new List<CharTestRejection>();
        foreach (var test in tests)
        {
            var nodes = Reachable(cls, test);
            var invocations = nodes.SelectMany(n => n.DescendantNodes().OfType<InvocationExpressionSyntax>()).ToList();
            var name = test.Identifier.Text;

            if (!invocations.Any(i => InvokedName(i) == target))
            {
                result.Add(new(FailureKinds.CharTestNoAssert, name, $"테스트가 대상 메서드 {target}을(를) 호출하지 않습니다"));
                continue;
            }

            var attrAssert = Attrs(test).Any(a => fw.ThrowsAssertions.Contains(AttrName(a)));
            var asserts = invocations.Where(i => IsAssertionInvocation(i, fw)).ToList();
            if (!attrAssert && asserts.Count == 0)
                result.Add(new(FailureKinds.CharTestNoAssert, name, "테스트에 단언이 없습니다"));
            else if (!attrAssert && asserts.All(a => a.ArgumentList.Arguments.All(x => IsLiteral(x.Expression))))
                result.Add(new(FailureKinds.CharTestNoAssert, name, "단언의 인자가 모두 리터럴입니다 (대상의 결과를 단언해야 함)"));
        }

        return result;
    }

    private static List<CharTestRejection> JudgeEnvironment(ClassDeclarationSyntax cls)
    {
        var found = new List<(string? Owner, string Token)>();

        foreach (var ma in cls.DescendantNodes().OfType<MemberAccessExpressionSyntax>())
        {
            if (!IsSimpleTypeRef(ma.Expression, out var typeName)) continue;
            foreach (var (type, members) in EnvMembers)
                if (type == typeName && (members is null || members.Contains(ma.Name.Identifier.Text)))
                    found.Add((OwnerName(ma), $"{type}.{ma.Name.Identifier.Text}"));
        }

        foreach (var oc in cls.DescendantNodes().OfType<ObjectCreationExpressionSyntax>())
            if (TypeSimpleName(oc.Type) == "Random") found.Add((OwnerName(oc), "new Random"));

        foreach (var id in cls.DescendantNodes().OfType<IdentifierNameSyntax>())
            if (EnvTypes.Contains(id.Identifier.Text)) found.Add((OwnerName(id), id.Identifier.Text));

        return found.Distinct()
            .Select(f => new CharTestRejection(FailureKinds.CharTestEnvDependent, f.Owner,
                $"테스트가 시간·파일·DB·네트워크에 기대면 안 됩니다: {f.Token}"))
            .ToList();
    }

    private static string? TypeSimpleName(TypeSyntax t) => t switch
    {
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        SimpleNameSyntax s => s.Identifier.Text,
        _ => null,
    };

    /// <summary>True for a bare identifier or a System-rooted qualified name (so <c>order.File.Name</c> is not a type).</summary>
    private static bool IsSimpleTypeRef(ExpressionSyntax e, out string name)
    {
        name = "";
        if (e is IdentifierNameSyntax id) { name = id.Identifier.Text; return true; }
        if (e is MemberAccessExpressionSyntax m)
        {
            var root = m.Expression;
            while (root is MemberAccessExpressionSyntax inner) root = inner.Expression;
            if (root is AliasQualifiedNameSyntax) { name = m.Name.Identifier.Text; return true; }
            if (root is IdentifierNameSyntax r && r.Identifier.Text == "System") { name = m.Name.Identifier.Text; return true; }
        }

        return false;
    }
}
