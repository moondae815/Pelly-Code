using System.IO.Abstractions;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Models;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Specs;

/// <summary>A group task's current target: its file (full path) and the member display it edits now.</summary>
public sealed record GroupMember(string TaskId, string FullPath, string TargetDisplay);

/// <summary>
/// New group errors split by owner. <see cref="ByTask"/> holds only tasks that got errors; <see cref="Unattributed"/> are
/// "명세에 빠진 영향 대상", each with a location: <c>&lt;relative path&gt;#&lt;member&gt;</c>, <c>&lt;relative path&gt;:&lt;line&gt;</c>
/// when no member encloses the line or the file cannot be read, or the error code when the error has no file.
/// </summary>
public sealed record Attribution(
    IReadOnlyDictionary<string, IReadOnlyList<Diagnostic>> ByTask,
    IReadOnlyList<(string Location, Diagnostic Error)> Unattributed);

/// <summary>Assigns each new group build error to the group task whose target member encloses it (design §6 "작업 그룹" 4).</summary>
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

            var owner = enclosing is null
                ? null
                : members.FirstOrDefault(m =>
                    PathComparers.ForOs.Equals(Path.GetFullPath(m.FullPath), fullPath)
                    && string.Equals(m.TargetDisplay, enclosing.Display, StringComparison.Ordinal));
            if (owner is not null)
            {
                if (!byTask.TryGetValue(owner.TaskId, out var list)) byTask[owner.TaskId] = list = [];
                list.Add(error);
                continue;
            }

            unattributed.Add((enclosing is null ? $"{error.File}:{error.Line}" : $"{error.File}#{NameOf(enclosing.Display)}", error));
        }

        return new Attribution(
            byTask.ToDictionary(p => p.Key, p => (IReadOnlyList<Diagnostic>)p.Value),
            unattributed);
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

    /// <summary>The bare member name of a display: text before the parameter list, after the last dot.</summary>
    private static string NameOf(string display)
    {
        var paren = display.IndexOf('(');
        var head = paren >= 0 ? display[..paren] : display;
        return head[(head.LastIndexOf('.') + 1)..];
    }
}
