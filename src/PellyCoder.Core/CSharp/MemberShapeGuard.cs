using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PellyCoder.Core.CSharp;

/// <summary>Result of <see cref="MemberShapeGuard.Check"/>.</summary>
/// <param name="AddedPrivateMethods">Displays (MemberLocator format) of private methods present after but not before.</param>
public sealed record ShapeCheck(bool Ok, string? Reason, IReadOnlyList<string> AddedPrivateMethods);

/// <summary>
/// Rejects edits that change the target's signature or attributes, change or remove any other member, add anything but
/// private methods to the target's type, or remove usings (design §8.7.1, D14). The build checks only the target project,
/// so this is what keeps an edit from breaking callers elsewhere.
/// </summary>
public static class MemberShapeGuard
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <param name="Scope">Containing namespace and type path; a partial type split within the file shares one scope.</param>
    private sealed record Entry(string Key, string Display, string Kind, string Scope, string Text, MemberDeclarationSyntax Node);

    /// <param name="beforeLf">The file before the edit. APPLY passes the run's original text, so helpers added by earlier
    /// attempts count as added (and stay editable) rather than as existing members.</param>
    /// <param name="targetDisplay">The target's display in <paramref name="beforeLf"/> (e.g. <c>Calc.Add(int, int)</c>).</param>
    public static ShapeCheck Check(string beforeLf, string afterLf, string targetDisplay, LanguageVersion version)
    {
        var options = new CSharpParseOptions(version);
        var before = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(beforeLf, options).GetRoot();
        var after = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(afterLf, options).GetRoot();

        var beforeEntries = Entries(before);
        var afterEntries = Entries(after);

        var target = beforeEntries.FirstOrDefault(e => e.Display == targetDisplay && IsLocatorKind(e.Node))
            ?? throw new ArgumentException($"Target '{targetDisplay}' is not in the original text.", nameof(targetDisplay));

        // The edited target: same key, else a member that did not exist before with the same name, kind and type.
        var beforeKeys = beforeEntries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var newTarget = afterEntries.FirstOrDefault(e => e.Key == target.Key)
            ?? afterEntries.FirstOrDefault(e => !beforeKeys.Contains(e.Key) && e.Kind == target.Kind
                && e.Scope == target.Scope && NameOf(e.Node) == NameOf(target.Node));

        if (newTarget is null)
            return Fail($"member {target.Display} was removed or changed. Only the target member may change.");
        if (SignatureOf(target.Node) != SignatureOf(newTarget.Node))
            return Fail($"signature of {target.Display} changed to {newTarget.Display}. Signature changes are not allowed.");
        if (AttributesOf(target.Node) != AttributesOf(newTarget.Node))
            return Fail($"attributes of {target.Display} changed. Attribute changes are not allowed.");

        // Every other member that existed before must still be there, with the same text (whitespace aside).
        var afterByKey = afterEntries.Where(e => e != newTarget).ToLookup(e => e.Key, StringComparer.Ordinal);
        foreach (var group in beforeEntries.Where(e => e != target).GroupBy(e => e.Key, StringComparer.Ordinal))
        {
            var was = group.Select(e => e.Text).Order(StringComparer.Ordinal);
            var now = afterByKey[group.Key].Select(e => e.Text).Order(StringComparer.Ordinal);
            if (!was.SequenceEqual(now, StringComparer.Ordinal))
                return Fail($"member {group.First().Display} was removed or changed. Only the target member may change.");
        }

        var added = new List<string>();
        foreach (var entry in afterEntries.Where(e => e != newTarget && !beforeKeys.Contains(e.Key)))
        {
            if (!IsPrivateMethod(entry.Node) || entry.Scope != target.Scope)
                return Fail($"added {entry.Kind} {entry.Display}. Only private helper methods may be added.");
            if (!added.Contains(entry.Display)) added.Add(entry.Display);
        }

        var afterUsings = Usings(after).Select(u => u.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, name) in Usings(before))
        {
            if (!afterUsings.Contains(key))
                return Fail($"using {name} was removed. Usings may only be added.");
        }

        return new ShapeCheck(true, null, added);
    }

    private static ShapeCheck Fail(string reason) => new(false, reason, []);

    private static bool IsLocatorKind(MemberDeclarationSyntax m) =>
        m is MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax;

    private static bool IsPrivateMethod(MemberDeclarationSyntax m) =>
        m is MethodDeclarationSyntax method
        && method.Modifiers.Any(SyntaxKind.PrivateKeyword)
        && !method.Modifiers.Any(SyntaxKind.ProtectedKeyword);

    /// <summary>Every member inside a type (and every type), keyed by containing namespace/type path, kind and display.</summary>
    private static List<Entry> Entries(CompilationUnitSyntax root)
    {
        var result = new List<Entry>();
        foreach (var member in root.DescendantNodes().OfType<MemberDeclarationSyntax>())
        {
            if (member is BaseNamespaceDeclarationSyntax) continue;

            var types = member.Ancestors().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text).ToList();
            var containing = string.Join('.', types);
            var namespaces = string.Join('.', member.Ancestors().OfType<BaseNamespaceDeclarationSyntax>().Reverse().Select(n => n.Name.ToString()));
            var local = LocalDisplay(member);
            var display = containing.Length == 0 ? local : containing + "." + local;
            var kind = KindWord(member);
            var scope = namespaces + "|" + containing;
            result.Add(new Entry($"{scope}|{member.Kind()}|{local}", display, kind, scope, Normalize(TextOf(member)), member));
        }
        return result;
    }

    /// <summary>Member text without its leading/trailing trivia; for a type only its header, since its members are entries.</summary>
    private static string TextOf(MemberDeclarationSyntax member)
    {
        if (member is BaseTypeDeclarationSyntax type && !type.OpenBraceToken.IsKind(SyntaxKind.None))
            return member.ToString()[..(type.OpenBraceToken.SpanStart - member.SpanStart)];
        return member.ToString();
    }

    private static string LocalDisplay(MemberDeclarationSyntax member) => MemberLocator.DisplayOf(member) ?? member switch
    {
        BaseTypeDeclarationSyntax t => t.Identifier.Text + Arity((t as TypeDeclarationSyntax)?.TypeParameterList),
        DelegateDeclarationSyntax d => d.Identifier.Text + Arity(d.TypeParameterList),
        BaseFieldDeclarationSyntax f => string.Join(", ", f.Declaration.Variables.Select(v => v.Identifier.Text)),
        EventDeclarationSyntax e => e.Identifier.Text,
        IndexerDeclarationSyntax i => "this[" + string.Join(", ", i.ParameterList.Parameters.Select(p => p.Type?.ToString())) + "]",
        OperatorDeclarationSyntax o => "operator " + o.OperatorToken.Text + Parameters(o.ParameterList),
        ConversionOperatorDeclarationSyntax c => "operator " + c.Type + Parameters(c.ParameterList),
        DestructorDeclarationSyntax d => "~" + d.Identifier.Text + "()",
        EnumMemberDeclarationSyntax e => e.Identifier.Text,
        _ => Normalize(member.ToString()),
    };

    private static string Arity(TypeParameterListSyntax? list) => list is null ? "" : "<" + new string(',', list.Parameters.Count - 1) + ">";

    private static string Parameters(ParameterListSyntax list) =>
        "(" + string.Join(", ", list.Parameters.Select(p => p.Type?.ToString() ?? "?")) + ")";

    private static string KindWord(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax => "method",
        ConstructorDeclarationSyntax => "constructor",
        PropertyDeclarationSyntax => "property",
        BaseFieldDeclarationSyntax f when f is EventFieldDeclarationSyntax => "event",
        BaseFieldDeclarationSyntax => "field",
        EventDeclarationSyntax => "event",
        IndexerDeclarationSyntax => "indexer",
        OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax => "operator",
        DestructorDeclarationSyntax => "finalizer",
        EnumMemberDeclarationSyntax => "enum member",
        DelegateDeclarationSyntax => "delegate",
        ClassDeclarationSyntax => "class",
        StructDeclarationSyntax => "struct",
        InterfaceDeclarationSyntax => "interface",
        RecordDeclarationSyntax r => r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) ? "record struct" : "record",
        EnumDeclarationSyntax => "enum",
        _ => "member",
    };

    private static string? NameOf(MemberDeclarationSyntax member) => member switch
    {
        MethodDeclarationSyntax m => m.Identifier.Text,
        ConstructorDeclarationSyntax c => c.Identifier.Text,
        PropertyDeclarationSyntax p => p.Identifier.Text,
        _ => null,
    };

    /// <summary>
    /// Everything callers depend on: modifiers except <c>async</c> (which may change only while the return type stays),
    /// return type, explicit interface, type parameters and constraints, parameters (type, modifiers, default; not names)
    /// and, for properties, the accessors and their modifiers.
    /// </summary>
    private static string SignatureOf(MemberDeclarationSyntax member)
    {
        var modifiers = string.Join(' ', member.Modifiers.Where(m => !m.IsKind(SyntaxKind.AsyncKeyword)).Select(m => m.Text).Order(StringComparer.Ordinal));
        var parts = member switch
        {
            MethodDeclarationSyntax m => new[]
            {
                m.ReturnType.ToString(), m.ExplicitInterfaceSpecifier?.ToString() ?? "", m.TypeParameterList?.ToString() ?? "",
                ParameterShape(m.ParameterList), string.Join(' ', m.ConstraintClauses.Select(c => c.ToString())),
            },
            ConstructorDeclarationSyntax c => new[] { ParameterShape(c.ParameterList) },
            PropertyDeclarationSyntax p => new[] { p.Type.ToString(), p.ExplicitInterfaceSpecifier?.ToString() ?? "", AccessorShape(p) },
            _ => [member.ToString()],
        };
        return Normalize(modifiers + " | " + string.Join(" | ", parts));
    }

    private static string ParameterShape(ParameterListSyntax list) => string.Join(", ", list.Parameters.Select(p =>
        string.Join(' ', p.Modifiers.Select(m => m.Text)) + " " + p.Type + (p.Default is null ? "" : " = " + p.Default.Value)));

    private static string AccessorShape(PropertyDeclarationSyntax p)
    {
        if (p.ExpressionBody is not null) return "get";
        return string.Join(' ', (p.AccessorList?.Accessors ?? default)
            .Select(a => string.Join(' ', a.Modifiers.Select(m => m.Text).Append(a.Keyword.Text))));
    }

    /// <summary>Attributes on the member and on its parameters.</summary>
    private static string AttributesOf(MemberDeclarationSyntax member)
    {
        var parameters = member switch
        {
            BaseMethodDeclarationSyntax m => m.ParameterList.Parameters.Select(p => string.Concat(p.AttributeLists.Select(a => a.ToString()))),
            _ => [],
        };
        var own = string.Concat(member.AttributeLists.Select(a => a.ToString()));
        return Normalize(own + " | " + string.Join(", ", parameters));
    }

    /// <summary>Using directives at compilation-unit and namespace level, keyed by their normalized text.</summary>
    private static IEnumerable<(string Key, string Name)> Usings(CompilationUnitSyntax root) =>
        root.DescendantNodes(n => n is CompilationUnitSyntax or BaseNamespaceDeclarationSyntax)
            .OfType<UsingDirectiveSyntax>()
            .Select(u => (Normalize(u.ToString()), Normalize(UsingName(u))));

    private static string UsingName(UsingDirectiveSyntax u)
    {
        var name = u.NamespaceOrType.ToString();
        if (u.Alias is not null) name = u.Alias.Name + " = " + name;
        if (!u.StaticKeyword.IsKind(SyntaxKind.None)) name = "static " + name;
        return name;
    }

    private static string Normalize(string text) => Whitespace.Replace(text, " ").Trim();
}
