using System.IO.Abstractions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Models;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Specs;

/// <summary>
/// A group task's current target: its file (full path), the member display it edits now and the displays of the private
/// helpers its runs added to that file.
/// </summary>
public sealed record GroupMember(string TaskId, string FullPath, string TargetDisplay, IReadOnlyList<string>? Helpers = null);

/// <summary>
/// New group errors split by owner. <see cref="ByTask"/> holds only tasks that got errors; <see cref="Unattributed"/> are
/// "명세에 빠진 영향 대상", each with a location: <c>&lt;relative path&gt;#&lt;member&gt;</c>, <c>&lt;relative path&gt;:&lt;line&gt;</c>
/// when no member encloses the line or the file cannot be read, or the error code when the error has no file.
/// </summary>
public sealed record Attribution(
    IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> ByTask,
    IReadOnlyList<(string Location, Diagnostic Error)> Unattributed);

/// <summary>
/// Assigns each new group build error to the group task whose target member (or a private helper its runs added) encloses
/// it (design §6 "작업 그룹" 4). An error on a type's header or base list, outside any member, goes to every group task
/// whose target is in that type in that file.
/// </summary>
public static class GroupErrorAttribution
{
    public static Attribution Attribute(IFileSystem fs, Workspace ws, IReadOnlyList<Diagnostic> newErrors,
        IReadOnlyList<GroupMember> members, LanguageVersion version)
    {
        var byTask = new Dictionary<string, List<Diagnostic>>();
        var unattributed = new List<(string, Diagnostic)>();
        // One parse per file: several errors usually land in the same file.
        var sources = new Dictionary<string, string?>(PathComparers.ForOs);

        foreach (var error in newErrors)
        {
            if (string.IsNullOrEmpty(error.File))
            {
                unattributed.Add((error.Code, error));
                continue;
            }

            var fullPath = Path.GetFullPath(Path.Combine(ws.Root, error.File.Replace('/', Path.DirectorySeparatorChar)));
            if (!sources.TryGetValue(fullPath, out var source))
                sources[fullPath] = source = ReadCSharp(fs, fullPath);
            var enclosing = source is null ? null : MemberLocator.FindAtLine(source, version, error.Line);
            var inFile = members.Where(m => PathComparers.ForOs.Equals(Path.GetFullPath(m.FullPath), fullPath)).ToList();

            IReadOnlyList<GroupMember> owners;
            if (enclosing is not null)
            {
                // The target itself, or a private helper the task's runs added (a re-run may fix it).
                var owner = inFile.FirstOrDefault(m => string.Equals(m.TargetDisplay, enclosing.Display, StringComparison.Ordinal))
                    ?? inFile.FirstOrDefault(m => m.Helpers?.Contains(enclosing.Display, StringComparer.Ordinal) == true);
                owners = owner is null ? [] : [owner];
            }
            else
            {
                // A type's header or base list (CS0535, CS0738 after a signature change): every task whose target is in that type.
                var type = source is null ? null : TypeHeaderAtLine(source, version, error.Line);
                owners = type is null ? [] : inFile.Where(m => string.Equals(ContainingType(m.TargetDisplay), type, StringComparison.Ordinal)).ToList();
            }

            if (owners.Count > 0)
            {
                foreach (var owner in owners)
                {
                    if (!byTask.TryGetValue(owner.TaskId, out var list)) byTask[owner.TaskId] = list = [];
                    list.Add(error);
                }
                continue;
            }

            unattributed.Add((enclosing is null ? $"{error.File}:{error.Line}" : $"{error.File}#{MemberLocator.NameOf(enclosing.Display)}", error));
        }

        return new Attribution(
            byTask.ToDictionary(p => p.Key, p => (IReadOnlyList<Diagnostic>)p.Value),
            unattributed);
    }

    /// <summary>
    /// The dotted type path (<c>Outer.Inner</c>, as in member displays) of the innermost type whose header — from its first
    /// token (attributes included) to its open brace, or the whole declaration when it has none — holds the 1-based line.
    /// </summary>
    private static string? TypeHeaderAtLine(string source, LanguageVersion version, int line)
    {
        var tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(version));
        string? found = null;
        foreach (var type in tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>())
        {
            var start = tree.GetLineSpan(type.Span).StartLinePosition.Line + 1;
            var end = Microsoft.CodeAnalysis.CSharpExtensions.IsKind(type.OpenBraceToken, SyntaxKind.None)
                ? tree.GetLineSpan(type.Span).EndLinePosition.Line + 1
                : tree.GetLineSpan(type.OpenBraceToken.Span).StartLinePosition.Line + 1;
            if (line < start || line > end) continue;
            // Descendants come after their ancestors, so the last match is the innermost type.
            found = string.Join('.', type.AncestorsAndSelf().OfType<BaseTypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text));
        }
        return found;
    }

    /// <summary>The type path of a member display: <c>Outer.Inner</c> of <c>Outer.Inner.M(int)</c>.</summary>
    private static string ContainingType(string display)
    {
        var paren = display.IndexOf('(');
        var head = paren >= 0 ? display[..paren] : display;
        var dot = head.LastIndexOf('.');
        return dot < 0 ? "" : head[..dot];
    }

    /// <summary>The LF text of a C# file, or null when it is not C#, missing or unreadable.</summary>
    private static string? ReadCSharp(IFileSystem fs, string fullPath)
    {
        if (!fullPath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || !fs.File.Exists(fullPath)) return null;
        try
        {
            return TextFileCodec.Read(fs, fullPath).Text;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return null;
        }
    }
}
