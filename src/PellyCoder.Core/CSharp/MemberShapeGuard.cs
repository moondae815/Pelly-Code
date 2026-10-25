using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace PellyCoder.Core.CSharp;

/// <summary>Result of <see cref="MemberShapeGuard.Check"/>.</summary>
/// <param name="AddedPrivateMethods">Displays (MemberLocator format) of private methods present after but not before.</param>
/// <param name="NewTargetDisplay">On success, the target's display after the edit (differs from the input display when a
/// group edit changed its signature or name).</param>
public sealed record ShapeCheck(bool Ok, string? Reason, IReadOnlyList<string> AddedPrivateMethods, string? NewTargetDisplay = null);

/// <summary>What <see cref="MemberShapeGuard.Check"/> lets the target itself change.</summary>
public enum ShapeAllowance
{
    /// <summary>Body only: signature, attributes and name stay (v1, single-member runs).</summary>
    MemberBodyOnly,

    /// <summary>Signature, attributes and name may change in place (task group runs, verified by one build at the end).</summary>
    TargetSignatureAndName,
}

/// <summary>The target display passed to <see cref="MemberShapeGuard.Check"/> is not a member of the original text.</summary>
public sealed class ShapeTargetNotFoundException(string targetDisplay)
    : ArgumentException($"Target '{targetDisplay}' is not in the original text.", nameof(targetDisplay))
{
    public string TargetDisplay { get; } = targetDisplay;
}

/// <summary>
/// Rejects edits that change the target's signature or attributes, change or remove any other member, add anything but
/// private methods to the target's type, or remove usings (design §8.7.1, D14). The build checks only the target project,
/// so this is what keeps an edit from breaking callers elsewhere. A task group run (<see cref="ShapeAllowance.TargetSignatureAndName"/>)
/// lets the target's signature, attributes and name change in place, since the group's end build covers the affected projects.
/// </summary>
public static class MemberShapeGuard
{
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);

    /// <param name="Scope">Containing namespace and type path; a partial type split within the file shares one scope.</param>
    private sealed record Entry(string Key, string Display, string Kind, string Scope, string Text, MemberDeclarationSyntax Node);

    /// <param name="beforeLf">The file before the edit. APPLY passes the run's original text, so helpers added by earlier
    /// attempts count as added (and stay editable) rather than as existing members.</param>
    /// <param name="targetDisplay">The target's display in <paramref name="beforeLf"/> (e.g. <c>Calc.Add(int, int)</c>).</param>
    /// <param name="editableHelpers">Displays of private helpers in <paramref name="beforeLf"/> that may change or go (a group
    /// re-run's helpers from earlier runs). Entries that are not private methods of the target's type are ignored.</param>
    /// <exception cref="ShapeTargetNotFoundException">No method, constructor or property with that display in <paramref name="beforeLf"/>.</exception>
    public static ShapeCheck Check(string beforeLf, string afterLf, string targetDisplay, LanguageVersion version,
        ShapeAllowance allowance = ShapeAllowance.MemberBodyOnly, IReadOnlyCollection<string>? editableHelpers = null)
    {
        var options = new CSharpParseOptions(version);
        var before = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(beforeLf, options).GetRoot();
        var after = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(afterLf, options).GetRoot();

        var beforeEntries = Entries(before);
        var afterEntries = Entries(after);

        var target = beforeEntries.FirstOrDefault(e => e.Display == targetDisplay && IsLocatorKind(e.Node))
            ?? throw new ShapeTargetNotFoundException(targetDisplay);

        // The edited target: same key, else a member that did not exist before with the same kind, type and name (body only),
        // or, in a group run, the re-signed or renamed member GroupTarget pairs.
        var beforeKeys = beforeEntries.Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        var candidates = afterEntries.Where(e => !beforeKeys.Contains(e.Key) && e.Kind == target.Kind && e.Scope == target.Scope).ToList();
        IReadOnlyList<Entry> tied = [];
        var newTarget = afterEntries.FirstOrDefault(e => e.Key == target.Key);
        if (newTarget is null && allowance == ShapeAllowance.TargetSignatureAndName)
            (newTarget, tied) = GroupTarget(target, candidates, beforeEntries, afterEntries);
        else
            newTarget ??= candidates.FirstOrDefault(e => NameOf(e.Node) == NameOf(target.Node));

        // Several new members where the target was (tied) are reported after the other members are checked, since a
        // changed or removed member is the more basic fault.
        if (newTarget is null && tied.Count == 0)
            return Fail($"member {target.Display} was removed or changed. Only the target member may change.");
        if (allowance == ShapeAllowance.MemberBodyOnly && newTarget is not null)
        {
            if (SignatureOf(target.Node) != SignatureOf(newTarget.Node))
            {
                // Same display (return type, modifiers, defaults, ref/out, constraints): name what changed.
                return Fail(target.Display == newTarget.Display
                    ? $"signature of {target.Display} changed from `{HeaderOf(target.Node)}` to `{HeaderOf(newTarget.Node)}`. Signature changes are not allowed."
                    : $"signature of {target.Display} changed to {newTarget.Display}. Signature changes are not allowed.");
            }
            if (AttributesOf(target.Node) != AttributesOf(newTarget.Node))
                return Fail($"attributes of {target.Display} changed. Attribute changes are not allowed.");
        }

        // A group re-run may change or replace the private helpers its task's earlier runs added (they are in its original
        // text now); nothing else is opened by that list.
        var editable = beforeEntries
            .Where(e => e != target && editableHelpers?.Contains(e.Display) == true && IsPrivateMethod(e.Node) && e.Scope == target.Scope)
            .ToHashSet();

        // Every other member that existed before must still be there, with the same text (whitespace aside).
        var afterByKey = afterEntries.Where(e => e != newTarget).ToLookup(e => e.Key, StringComparer.Ordinal);
        foreach (var group in beforeEntries.Where(e => e != target && !editable.Contains(e)).GroupBy(e => e.Key, StringComparer.Ordinal))
        {
            var was = group.Select(e => e.Text).Order(StringComparer.Ordinal);
            var now = afterByKey[group.Key].Select(e => e.Text).Order(StringComparer.Ordinal);
            if (!was.SequenceEqual(now, StringComparer.Ordinal))
                return Fail($"member {group.First().Display} was removed or changed. Only the target member may change.");
        }

        if (newTarget is null)
        {
            // Telling them apart by signature or body would be a guess, and a wrong guess names a helper as the target.
            return Fail($"cannot tell which new method is the target {target.Display}: {string.Join(", ", tied.Select(e => e.Display))} " +
                "all sit where it was. Add new helpers away from the target (not directly above or below it), so only the target stays in its place.");
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

        return new ShapeCheck(true, null, added, newTarget.Display);
    }

    private static ShapeCheck Fail(string reason) => new(false, reason, []);

    private static Entry? SingleOrNull(List<Entry> entries) => entries.Count == 1 ? entries[0] : null;

    /// <summary>
    /// A group run's target after a rename or re-signing, among the new members of its kind and type. Candidates with the
    /// target's accessibility come first: a private helper added next to a public target is never the target. Among them the
    /// one with the target's name, else the one at the target's position. A position counts only members that are in both
    /// texts, so inserted helpers shift nothing. When several stay (a private target with a helper beside it), none is paired
    /// and they are returned as tied: an unchanged signature does not tell a pure rename from a re-signed target beside a new
    /// helper with the old signature, and pairing the wrong one names a helper as the target.
    /// </summary>
    private static (Entry? Target, IReadOnlyList<Entry> Tied) GroupTarget(Entry target, List<Entry> candidates, List<Entry> beforeEntries,
        List<Entry> afterEntries)
    {
        var access = AccessOf(target.Node);
        var pool = candidates.Where(e => AccessOf(e.Node) == access).ToList();
        if (pool.Count == 0)
        {
            // The target's accessibility changed too: anything but a helper when the target was not one.
            pool = IsPrivateMethod(target.Node) ? candidates : candidates.Where(e => !IsPrivateMethod(e.Node)).ToList();
        }

        if (SingleOrNull(pool.Where(e => NameOf(e.Node) == NameOf(target.Node)).ToList()) is { } named) return (named, []);

        // Keys and the target's position are computed once, not per candidate.
        var beforeKeyOf = beforeEntries.ToDictionary(e => e.Node, e => e.Key);
        var afterKeyOf = afterEntries.ToDictionary(e => e.Node, e => e.Key);
        var beforeKeys = beforeKeyOf.Values.ToHashSet(StringComparer.Ordinal);
        var afterKeys = afterKeyOf.Values.ToHashSet(StringComparer.Ordinal);
        int position = AnchorsBefore(target.Node, beforeKeyOf, afterKeys);
        var placed = pool.Where(e => AnchorsBefore(e.Node, afterKeyOf, beforeKeys) == position).ToList();
        return placed.Count == 1 ? (placed[0], []) : (null, placed);
    }

    /// <summary>
    /// The member's position among its parent's members (type, namespace or compilation unit), counting only the members
    /// whose key is in <paramref name="otherKeys"/>, i.e. those present before and after the edit.
    /// </summary>
    private static int AnchorsBefore(MemberDeclarationSyntax member, Dictionary<MemberDeclarationSyntax, string> keyOf, HashSet<string> otherKeys)
    {
        if (member.Parent is null) return -1;
        return member.Parent.ChildNodes().OfType<MemberDeclarationSyntax>()
            .TakeWhile(m => m != member)
            .Count(m => keyOf.TryGetValue(m, out var key) && otherKeys.Contains(key));
    }

    /// <summary>
    /// The member's declared access (sorted keywords), or its default: public in an interface, private elsewhere.
    /// </summary>
    private static string AccessOf(MemberDeclarationSyntax m)
    {
        var access = m.Modifiers.Where(t => t.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword
                or SyntaxKind.ProtectedKeyword or SyntaxKind.InternalKeyword or SyntaxKind.FileKeyword)
            .Select(t => t.Text).Order(StringComparer.Ordinal).ToList();
        if (access.Count > 0) return string.Join(' ', access);
        return m.Parent is InterfaceDeclarationSyntax ? "public" : "private";
    }

    /// <summary>The kinds <see cref="MemberLocator"/> can target (operators only through <c>--line</c>).</summary>
    private static bool IsLocatorKind(MemberDeclarationSyntax m) =>
        m is MethodDeclarationSyntax or ConstructorDeclarationSyntax or PropertyDeclarationSyntax
            or OperatorDeclarationSyntax or ConversionOperatorDeclarationSyntax;

    /// <summary>
    /// <c>private</c> (not <c>private protected</c>), or no access modifier in a class, struct or record, where the
    /// default is private. Interface members default to public; explicit interface implementations are not helpers.
    /// </summary>
    private static bool IsPrivateMethod(MemberDeclarationSyntax m)
    {
        if (m is not MethodDeclarationSyntax { ExplicitInterfaceSpecifier: null } method) return false;
        if (method.Modifiers.Any(SyntaxKind.PrivateKeyword)) return !method.Modifiers.Any(SyntaxKind.ProtectedKeyword);
        var hasAccess = method.Modifiers.Any(t => t.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.ProtectedKeyword
            or SyntaxKind.InternalKeyword or SyntaxKind.FileKeyword);
        return !hasAccess && method.Parent is ClassDeclarationSyntax or StructDeclarationSyntax or RecordDeclarationSyntax;
    }

    /// <summary>
    /// The declaration up to (not including) its body, <c>=&gt;</c> or <c>;</c> (constructor: also before <c>: base(...)</c>),
    /// without attributes, whitespace collapsed. Properties also show their accessor keywords, since those are signature.
    /// </summary>
    private static string HeaderOf(MemberDeclarationSyntax member)
    {
        var start = member.AttributeLists.Count > 0 ? member.AttributeLists[^1].GetLastToken().GetNextToken().SpanStart : member.SpanStart;
        var end = member switch
        {
            ConstructorDeclarationSyntax c => (SyntaxNode?)c.Initializer ?? (SyntaxNode?)c.Body ?? c.ExpressionBody,
            BaseMethodDeclarationSyntax m => (SyntaxNode?)m.Body ?? m.ExpressionBody,
            PropertyDeclarationSyntax p => (SyntaxNode?)p.AccessorList ?? p.ExpressionBody,
            _ => null,
        } is { } stop ? stop.SpanStart : member.Span.End;
        var text = member.SyntaxTree.GetText().ToString(new Microsoft.CodeAnalysis.Text.TextSpan(start, end - start));
        var header = Normalize(text).TrimEnd(';').TrimEnd();
        if (member is not PropertyDeclarationSyntax property) return header;
        var accessors = property.ExpressionBody is not null
            ? ["get;"]
            : (property.AccessorList?.Accessors ?? default).Select(a => string.Join(' ', a.Modifiers.Select(m => m.Text).Append(a.Keyword.Text)) + ";");
        return header + " { " + string.Join(' ', accessors) + " }";
    }

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
        OperatorDeclarationSyntax o => "operator " + o.OperatorToken.Text,
        ConversionOperatorDeclarationSyntax c => "operator " + c.Type,
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
            OperatorDeclarationSyntax o => new[]
            {
                o.ReturnType.ToString(), o.ExplicitInterfaceSpecifier?.ToString() ?? "", o.CheckedKeyword.Text, o.OperatorToken.Text,
                ParameterShape(o.ParameterList),
            },
            ConversionOperatorDeclarationSyntax c => new[]
            {
                c.ImplicitOrExplicitKeyword.Text, c.ExplicitInterfaceSpecifier?.ToString() ?? "", c.CheckedKeyword.Text, c.Type.ToString(),
                ParameterShape(c.ParameterList),
            },
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
