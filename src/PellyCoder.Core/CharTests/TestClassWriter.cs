using System.IO.Abstractions;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.CharTests;

/// <summary>Text-level edits for the test class file and the legacy csproj. Every original line stays byte-identical.</summary>
public static class TestClassWriter
{
    /// <summary>
    /// Adds <paramref name="members"/> before the closing brace of <paramref name="className"/> (one blank line before the first)
    /// and the missing <paramref name="usings"/> (namespace names) after the existing using block. Keeps the file's line breaks.
    /// </summary>
    public static string Insert(string fileText, string className, IReadOnlyList<string> usings, IReadOnlyList<string> members)
    {
        var eol = fileText.Contains("\r\n") ? "\r\n" : "\n";
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(fileText).GetRoot();
        var cls = root.DescendantNodes().OfType<ClassDeclarationSyntax>().FirstOrDefault(c => c.Identifier.ValueText == className)
            ?? throw new ArgumentException($"클래스를 찾을 수 없습니다: {className}", nameof(className));

        var memberText = BuildMembers(fileText, cls, members, eol);
        var closePos = cls.CloseBraceToken.SpanStart;
        var lineStart = LineStart(fileText, closePos);
        var sb = new StringBuilder(fileText);

        if (IsBlank(fileText, lineStart, closePos))
        {
            var blank = cls.Members.Count > 0 && !PreviousLineBlank(fileText, lineStart);
            sb.Insert(lineStart, (blank ? eol : "") + memberText);
        }
        else
        {
            // "class C { }" on one line: break the line around the new members.
            var classIndent = LeadingWhitespace(fileText, LineStart(fileText, cls.SpanStart));
            sb.Insert(closePos, eol + memberText + classIndent);
        }

        InsertUsings(sb, fileText, root, usings, eol);
        return sb.ToString();
    }

    private static string BuildMembers(string text, ClassDeclarationSyntax cls, IReadOnlyList<string> members, string eol)
    {
        var classIndent = LeadingWhitespace(text, LineStart(text, cls.SpanStart));
        string indent;
        if (cls.Members.Count > 0)
            indent = LeadingWhitespace(text, LineStart(text, cls.Members[0].SpanStart));
        else
            indent = classIndent + (classIndent.Contains('\t') ? "\t" : "    ");

        var sb = new StringBuilder();
        for (var i = 0; i < members.Count; i++)
        {
            if (i > 0) sb.Append(eol);
            foreach (var line in members[i].Replace("\r\n", "\n").TrimEnd('\n').Split('\n'))
                sb.Append(line.Length == 0 ? "" : indent + line).Append(eol);
        }
        return sb.ToString();
    }

    private static void InsertUsings(StringBuilder sb, string text, CompilationUnitSyntax root, IReadOnlyList<string> usings, string eol)
    {
        // Keys: "Ns", "static Ns.Type" or "Alias = Ns.Type", whitespace normalized, so each using is written once.
        var existing = root.DescendantNodes().OfType<UsingDirectiveSyntax>()
            .Where(u => u.GlobalKeyword.IsKind(SyntaxKind.None))
            .Select(u => UsingKey((u.StaticKeyword.IsKind(SyntaxKind.None) ? "" : "static ")
                + (u.Alias is { } alias ? alias.Name.Identifier.ValueText + " = " : "") + u.NamespaceOrType.ToString()))
            .ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();
        foreach (var raw in usings)
        {
            var name = raw.Trim();
            if (name.StartsWith("using ", StringComparison.Ordinal)) name = name[6..];
            name = UsingKey(name.TrimEnd(';'));
            if (name.Length > 0 && existing.Add(name)) missing.Add(name);
        }
        if (missing.Count == 0) return;

        var all = root.DescendantNodes().OfType<UsingDirectiveSyntax>().ToList();
        if (all.Count > 0)
        {
            var last = root.Usings.Count > 0 ? root.Usings[^1] : all[^1];
            var indent = LeadingWhitespace(text, LineStart(text, last.SpanStart));
            var lineEnd = LineEnd(text, last.Span.End);
            var after = lineEnd < text.Length ? lineEnd + (text[lineEnd] == '\r' ? 2 : 1) : lineEnd;
            var block = string.Concat(missing.Select(m => indent + "using " + m + ";" + eol));
            if (lineEnd >= text.Length) block = eol + block.TrimEnd('\r', '\n'); // using on the last line without a break
            sb.Insert(after, block);
            return;
        }

        var first = root.Members.Count > 0 ? root.Members[0].SpanStart : 0;
        var at = LineStart(text, first);
        if (at == 0 && text.Length > 0 && text[0] == '﻿') at = 1;
        sb.Insert(at, string.Concat(missing.Select(m => "using " + m + ";" + eol)) + eol);
    }

    private static string UsingKey(string text) =>
        Regex.Replace(Regex.Replace(text.Trim(), @"\s*=\s*", " = "), @"\s+", " ");

    /// <summary>
    /// A new test file in the legacy-compatible shape: LF, block namespace, public class. <paramref name="extraUsings"/> (namespace
    /// names, see <see cref="TargetUsings"/>) come before the framework's usings; duplicates are written once.
    /// </summary>
    public static string NewFile(string ns, string className, FrameworkInfo fw, IReadOnlyList<string>? extraUsings = null)
    {
        var sb = new StringBuilder();
        var usings = (extraUsings ?? []).Concat(fw.Usings).Distinct(StringComparer.Ordinal).ToList();
        foreach (var u in usings) sb.Append("using ").Append(u).Append(";\n");
        if (usings.Count > 0) sb.Append('\n');
        sb.Append("namespace ").Append(ns).Append('\n').Append("{\n");
        if (fw.ClassAttribute is { } attr) sb.Append("    [").Append(attr).Append("]\n");
        sb.Append("    public class ").Append(className).Append('\n').Append("    {\n").Append("    }\n").Append("}\n");
        return sb.ToString();
    }

    /// <summary>Core BCL namespaces kept from the target file even though no workspace file declares them.</summary>
    private static readonly string[] CoreNamespaces = ["System", "System.Collections.Generic", "System.Linq", "System.Threading.Tasks"];

    /// <summary>
    /// The namespaces a new test file can safely take from the target file: its top-level <c>using</c> directives (no alias,
    /// <c>static</c> or <c>global</c> ones; namespace-level usings may be relative and are left out) whose namespace, or a parent or
    /// child of it, is in <paramref name="declaredNamespaces"/>, or that are one of the core BCL namespaces; then the target's
    /// namespace. A third-party or framework using (Dapper, System.Web.Mvc) is dropped: a legacy test project does not inherit the
    /// production project's references, and the model can still add it under <c>### USINGS</c>.
    /// </summary>
    public static IReadOnlyList<string> TargetUsings(string targetSourceLf, LanguageVersion version, IReadOnlySet<string> declaredNamespaces)
    {
        var root = (CompilationUnitSyntax)CSharpSyntaxTree.ParseText(targetSourceLf, new CSharpParseOptions(version)).GetRoot();
        var names = root.Usings
            .Where(u => u.Alias is null && u.StaticKeyword.IsKind(SyntaxKind.None) && u.GlobalKeyword.IsKind(SyntaxKind.None) && u.Name is not null)
            .Select(u => u.Name!.ToString())
            .Where(n => CoreNamespaces.Contains(n, StringComparer.Ordinal) || declaredNamespaces.Any(d => d == n
                || d.StartsWith(n + ".", StringComparison.Ordinal) || n.StartsWith(d + ".", StringComparison.Ordinal)))
            .ToList();
        if (root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault() is { } ns) names.Add(ns.Name.ToString());
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// Every namespace declared in a <c>.cs</c> file under <paramref name="root"/> (nested ones joined), with the
    /// <see cref="DtoCollector"/> bounded walk and limits. Never throws: an unreadable file is skipped.
    /// </summary>
    public static IReadOnlySet<string> DeclaredNamespaces(IFileSystem fs, string root, DtoLimits? limits = null, CancellationToken ct = default)
    {
        limits ??= new DtoLimits();
        var result = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            foreach (var file in BoundedWalker.Files(fs, root, "*.cs", limits.MaxDirectories, ct).Take(limits.MaxFiles))
            {
                try
                {
                    var text = DtoCollector.ReadText(fs, file, limits.MaxFileBytes);
                    if (text is null || !text.Contains("namespace", StringComparison.Ordinal)) continue;
                    foreach (var ns in CSharpSyntaxTree.ParseText(text, cancellationToken: ct).GetRoot().DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>())
                        result.Add(DtoCollector.NamespaceOf(ns) is { } outer ? outer + "." + ns.Name : ns.Name.ToString());
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
        return result;
    }

    /// <summary>
    /// Adds a <c>Compile Include</c> item to a non-SDK csproj after the last one (same indent and line break). Null only for an SDK
    /// project (globbing needs no change); throws <see cref="InvalidOperationException"/> when there is no <c>&lt;/Project&gt;</c>. Without a placeable <c>Compile Include</c>, a new <c>ItemGroup</c> goes before <c>&lt;/Project&gt;</c>.
    /// </summary>
    public static string? AddCompileInclude(string csprojText, string relativePath)
    {
        if (Regex.IsMatch(csprojText, @"<Project\b[^>]*\bSdk\s*=")) return null;

        var eol = csprojText.Contains("\r\n") ? "\r\n" : "\n";
        var escaped = System.Security.SecurityElement.Escape(relativePath);
        var item = $"<Compile Include=\"{escaped}\" />";

        // Whole elements, so attributes may spread over lines; the last Include item that starts its own line is the anchor.
        var elements = Regex.Matches(csprojText, @"<Compile\b[^>]*?/>|<Compile\b[^>]*>[\s\S]*?</Compile>")
            .Where(m => Regex.IsMatch(m.Value, @"^<Compile\b[^>]*\bInclude\s*=")
                && IsBlank(csprojText, LineStart(csprojText, m.Index), m.Index))
            .ToList();
        if (elements.Count > 0)
        {
            var last = elements[^1];
            var indent = csprojText[LineStart(csprojText, last.Index)..last.Index];
            return csprojText.Insert(last.Index + last.Length, eol + indent + item);
        }

        // No Include item we can place safely: a new ItemGroup is always valid.
        var projectEnd = csprojText.LastIndexOf("</Project>", StringComparison.Ordinal);
        if (projectEnd < 0) throw new InvalidOperationException("csproj에 </Project>가 없습니다: " + relativePath);
        var at = LineStart(csprojText, projectEnd);
        var block = $"  <ItemGroup>{eol}    {item}{eol}  </ItemGroup>{eol}";
        return csprojText.Insert(at, block);
    }

    private static int LineStart(string text, int pos) => pos == 0 ? 0 : text.LastIndexOf('\n', pos - 1) + 1;

    /// <summary>Index of the line break that ends the line holding <paramref name="pos"/> (the \r of a CRLF), or the text length.</summary>
    private static int LineEnd(string text, int pos)
    {
        var nl = text.IndexOf('\n', pos);
        if (nl < 0) return text.Length;
        return nl > 0 && text[nl - 1] == '\r' ? nl - 1 : nl;
    }

    private static string LeadingWhitespace(string text, int lineStart)
    {
        var i = lineStart;
        if (i == 0 && text.Length > 0 && text[0] == '﻿') i = 1;
        var start = i;
        while (i < text.Length && (text[i] == ' ' || text[i] == '\t')) i++;
        return text[start..i];
    }

    private static bool IsBlank(string text, int from, int to)
    {
        for (var i = from; i < to; i++)
            if (text[i] != ' ' && text[i] != '\t') return false;
        return true;
    }

    private static bool PreviousLineBlank(string text, int lineStart)
    {
        if (lineStart == 0) return false;
        var prevEnd = lineStart - 1; // the \n
        if (prevEnd > 0 && text[prevEnd - 1] == '\r') prevEnd--;
        return IsBlank(text, LineStart(text, prevEnd), prevEnd) && prevEnd > 0;
    }
}
