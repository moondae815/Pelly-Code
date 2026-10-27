using System.IO.Abstractions;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PellyCoder.Core.Files;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Specs.Drafting;

public sealed record DraftIndexMember(string TypeName, string Name, string Signature);

/// <summary>Kind is "cs" or "sql"; Path is relative to the workspace root and uses '/'.</summary>
public sealed record DraftIndexFile(string Path, string Kind, IReadOnlyList<DraftIndexMember> Members);

/// <summary>
/// The signature-only view of the scope given to the drafting model. Ported from the S6 planning probe
/// (spikes/S6-planning/S6Planning/CorpusIndex.cs, <c>CorpusIndex.Render</c>): the rendered text must stay byte-identical,
/// because the drafting conditions have to match what S6 measured. <see cref="Skipped"/> lists workspace-relative files that
/// could not be read into the index (a .sql file with parse errors, or a file that cannot be decoded).
/// </summary>
public sealed record DraftIndex(IReadOnlyList<DraftIndexFile> Files, IReadOnlyList<string> Skipped)
{
    public string Render()
    {
        var sb = new StringBuilder();
        foreach (var f in Files)
        {
            sb.Append("## ").Append(f.Path).Append(" (").Append(f.Kind).Append(")\n");
            foreach (var m in f.Members)
                sb.Append("- ").Append(f.Kind == "sql" ? m.Name : m.TypeName + "." + m.Name).Append(": ").Append(m.Signature).Append('\n');
            sb.Append('\n');
        }
        return sb.ToString().TrimEnd() + "\n";
    }
}

public sealed record DraftScopeError(string Message);

public static class DraftIndexBuilder
{
    /// <summary>
    /// Builds the index of the files under <paramref name="scopes"/> (paths as the user typed them: a file, a folder or a
    /// .csproj, resolved against the current directory). Every scope must exist inside the workspace.
    /// </summary>
    public static (DraftIndex? Index, DraftScopeError? Error) Build(IFileSystem fs, Workspace ws, IReadOnlyList<string> scopes, int sqlCompatLevel)
    {
        var root = fs.Path.TrimEndingDirectorySeparator(fs.Path.GetFullPath(ws.Root));
        var paths = new List<string>();
        var seen = new HashSet<string>(PathComparers.ForOs);
        var skipped = new List<string>();

        foreach (var scope in scopes)
        {
            string full;
            try { full = fs.Path.TrimEndingDirectorySeparator(fs.Path.GetFullPath(scope)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return (null, new DraftScopeError($"--scope 경로가 올바르지 않습니다: {scope}"));
            }

            if (!IsInside(fs, root, full))
                return (null, new DraftScopeError($"--scope 경로가 작업 공간 밖입니다: {scope}"));

            IEnumerable<string> found;
            if (fs.Directory.Exists(full))
            {
                var walked = Walk(fs, full);
                if (walked.Truncated)
                    return (null, new DraftScopeError($"범위가 너무 넓어 폴더를 다 읽지 못했습니다(폴더 {BoundedSources.MaxDirectories}개 초과). --scope를 좁히십시오: {scope}"));
                skipped.AddRange(walked.Unreadable.Select(d => Rel(fs, root, d)));
                found = walked.Files;
            }
            else if (fs.File.Exists(full))
            {
                var ext = fs.Path.GetExtension(full);
                if (ext.Equals(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    var walked = Walk(fs, fs.Path.GetDirectoryName(full)!);
                    if (walked.Truncated)
                        return (null, new DraftScopeError($"범위가 너무 넓어 폴더를 다 읽지 못했습니다(폴더 {BoundedSources.MaxDirectories}개 초과). --scope를 좁히십시오: {scope}"));
                    skipped.AddRange(walked.Unreadable.Select(d => Rel(fs, root, d)));
                    found = walked.Files;
                }
                else if (IsSource(full))
                    found = [full];
                else
                    return (null, new DraftScopeError($"--scope는 폴더, .csproj, .cs, .sql 파일만 받습니다: {scope}"));
            }
            else
                return (null, new DraftScopeError($"--scope 경로를 찾을 수 없습니다: {scope}"));

            foreach (var p in found)
                if (seen.Add(p))
                    paths.Add(p);
        }

        var files = new List<DraftIndexFile>();
        foreach (var p in paths)
        {
            var rel = Rel(fs, root, p);
            var isSql = p.EndsWith(".sql", StringComparison.OrdinalIgnoreCase);
            try
            {
                var text = TextDecoding.Decode(fs.File.ReadAllBytes(p));
                var members = isSql ? SqlMembers(text, sqlCompatLevel) : CsMembers(text);
                if (members is null) skipped.Add(rel);
                else files.Add(new DraftIndexFile(rel, isSql ? "sql" : "cs", members));
            }
            catch (Exception ex) when (ex is DecoderFallbackException or IOException or UnauthorizedAccessException)
            {
                skipped.Add(rel);
            }
        }

        return (new DraftIndex(
            files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList(),
            skipped.Distinct().Order(StringComparer.Ordinal).ToList()), null);
    }

    // Collect (not Files) so a walk the directory cap cut short is known; the index must never be silently partial.
    private static WalkResult Walk(IFileSystem fs, string dir)
    {
        var cs = BoundedWalker.Collect(fs, dir, "*.cs", BoundedSources.MaxDirectories);
        var sql = BoundedWalker.Collect(fs, dir, "*.sql", BoundedSources.MaxDirectories);
        return new WalkResult(cs.Files.Concat(sql.Files).Where(IsSource).ToList(), cs.Truncated || sql.Truncated,
            cs.Unreadable.Concat(sql.Unreadable).Distinct().ToList(), []);
    }

    // AssemblyInfo.cs is generated boilerplate; the S6 index left it out.
    private static bool IsSource(string path)
    {
        var name = path[(path.LastIndexOfAny(['/', '\\']) + 1)..];
        if (name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase)) return true;
        return name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) && !name.Equals("AssemblyInfo.cs", StringComparison.Ordinal);
    }

    private static bool IsInside(IFileSystem fs, string root, string full)
    {
        var comparison = PathComparers.ForOs == StringComparer.OrdinalIgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (string.Equals(full, root, comparison)) return true;
        if (!full.StartsWith(root, comparison)) return false;
        var rootEndsWithSeparator = root.Length > 0 && (root[^1] == fs.Path.DirectorySeparatorChar || root[^1] == fs.Path.AltDirectorySeparatorChar);
        return rootEndsWithSeparator || full[root.Length] == fs.Path.DirectorySeparatorChar || full[root.Length] == fs.Path.AltDirectorySeparatorChar;
    }

    private static string Rel(IFileSystem fs, string root, string path) => fs.Path.GetRelativePath(root, path).Replace('\\', '/');

    // Ported from S6 CorpusIndexBuilder.CsMembers: methods and properties only, constructors excluded.
    private static IReadOnlyList<DraftIndexMember> CsMembers(string text)
    {
        var tree = CSharpSyntaxTree.ParseText(text);
        var result = new List<DraftIndexMember>();
        foreach (var type in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var typeName = string.Join(".", type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text));
            foreach (var member in type.Members)
            {
                switch (member)
                {
                    case MethodDeclarationSyntax m:
                        result.Add(new DraftIndexMember(typeName, m.Identifier.Text,
                            Join(m.Modifiers.ToString(), m.ReturnType.ToString(), m.Identifier.Text + m.TypeParameterList + Params(m.ParameterList))));
                        break;
                    case PropertyDeclarationSyntax p:
                        var accessors = p.AccessorList is null ? "{ get; }"
                            : "{ " + string.Join(" ", p.AccessorList.Accessors.Select(a => Join(a.Modifiers.ToString(), a.Keyword.Text) + ";")) + " }";
                        result.Add(new DraftIndexMember(typeName, p.Identifier.Text, Join(p.Modifiers.ToString(), p.Type.ToString(), p.Identifier.Text, accessors)));
                        break;
                }
            }
        }
        return result;
    }

    private static string Params(ParameterListSyntax list) =>
        "(" + string.Join(", ", list.Parameters.Select(p =>
            Join(p.Modifiers.ToString(), p.Type?.ToString() ?? "", p.Identifier.Text) + (p.Default is null ? "" : " " + p.Default.ToString()))) + ")";

    private static string Join(params string[] parts) => string.Join(" ", parts.Where(x => x.Length > 0));

    // Ported from S6 CorpusIndexBuilder.SqlMembers; null when the file does not parse.
    private static IReadOnlyList<DraftIndexMember>? SqlMembers(string text, int compatLevel)
    {
        var fragment = SqlParsers.Create(compatLevel, true).Parse(new StringReader(text), out var errors);
        if (errors.Count > 0)
            return null;
        var result = new List<DraftIndexMember>();
        foreach (var batch in ((TSqlScript)fragment).Batches)
        {
            foreach (var st in batch.Statements)
            {
                switch (st)
                {
                    case ProcedureStatementBody p:
                        result.Add(new DraftIndexMember("", Name(p.ProcedureReference.Name), "PROCEDURE (" + string.Join(", ", p.Parameters.Select(x => Text(x))) + ")"));
                        break;
                    case FunctionStatementBody f:
                        result.Add(new DraftIndexMember("", Name(f.Name), "FUNCTION (" + string.Join(", ", f.Parameters.Select(x => Text(x))) + ")"));
                        break;
                    case ViewStatementBody v:
                        result.Add(new DraftIndexMember("", Name(v.SchemaObjectName), "VIEW"));
                        break;
                }
            }
        }
        return result;
    }

    private static string Name(SchemaObjectName n) => (n.SchemaIdentifier?.Value ?? "dbo") + "." + n.BaseIdentifier.Value;

    private static string Text(TSqlFragment f)
    {
        var sb = new StringBuilder();
        for (var i = f.FirstTokenIndex; i <= f.LastTokenIndex; i++)
            sb.Append(f.ScriptTokenStream[i].Text);
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
