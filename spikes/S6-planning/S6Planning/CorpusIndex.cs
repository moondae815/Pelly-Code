using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.SqlServer.TransactSql.ScriptDom;
using PellyCoder.Core.Files;

namespace S6Planning;

public sealed record IndexMember(string TypeName, string Name, string Signature);

public sealed record IndexFile(string Path, string Kind, IReadOnlyList<IndexMember> Members);

/// <summary>Signature-only view of the corpus given to the model, and the existence oracle for invented targets.</summary>
public sealed record CorpusIndex(IReadOnlyList<IndexFile> Files)
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

    public bool Exists(PlanTask t) => Exists(t.Kind, t.Path, t.Symbol);

    public bool Exists(string kind, string path, string? symbol)
    {
        var key = TargetKey.Of("sql", path, null);
        var file = Files.FirstOrDefault(f => TargetKey.Of("sql", f.Path, null) == key);
        if (file is null || file.Kind != kind)
            return false;
        if (kind == "cs")
        {
            var member = TargetKey.Member(symbol);
            return member.Length > 0 && file.Members.Any(m => m.Name == member);
        }
        if (string.IsNullOrEmpty(symbol))
            return true;
        var bare = StripSchema(symbol);
        return file.Members.Any(m => StripSchema(m.Name).Equals(bare, StringComparison.OrdinalIgnoreCase));
    }

    private static string StripSchema(string name) =>
        name.StartsWith("dbo.", StringComparison.OrdinalIgnoreCase) ? name[4..] : name;
}

public static class CorpusIndexBuilder
{
    public static CorpusIndex Build(string corpusRoot, IReadOnlyList<string> scope)
    {
        var files = new List<IndexFile>();
        foreach (var s in scope)
        {
            if (s == "Sql")
            {
                foreach (var p in Directory.GetFiles(Path.Combine(corpusRoot, "Sql", "objects"), "*.sql"))
                    files.Add(new IndexFile(Rel(corpusRoot, p), "sql", SqlMembers(p)));
            }
            else
            {
                foreach (var p in Directory.GetFiles(Path.Combine(corpusRoot, s), "*.cs", SearchOption.AllDirectories))
                {
                    var rel = Rel(corpusRoot, p);
                    if (rel.Contains("/obj/") || rel.Contains("/bin/") || rel.Contains("/TestResults/") || rel.EndsWith("/AssemblyInfo.cs", StringComparison.Ordinal))
                        continue;
                    files.Add(new IndexFile(rel, "cs", CsMembers(p)));
                }
            }
        }
        return new CorpusIndex(files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList());
    }

    private static string Rel(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

    private static IReadOnlyList<IndexMember> CsMembers(string path)
    {
        var tree = CSharpSyntaxTree.ParseText(TextDecoding.Decode(File.ReadAllBytes(path)));
        var result = new List<IndexMember>();
        foreach (var type in tree.GetRoot().DescendantNodes().OfType<TypeDeclarationSyntax>())
        {
            var typeName = string.Join(".", type.AncestorsAndSelf().OfType<TypeDeclarationSyntax>().Reverse().Select(t => t.Identifier.Text));
            foreach (var member in type.Members)
            {
                switch (member)
                {
                    case MethodDeclarationSyntax m:
                        result.Add(new IndexMember(typeName, m.Identifier.Text,
                            Join(m.Modifiers.ToString(), m.ReturnType.ToString(), m.Identifier.Text + m.TypeParameterList + Params(m.ParameterList))));
                        break;
                    case PropertyDeclarationSyntax p:
                        var accessors = p.AccessorList is null ? "{ get; }"
                            : "{ " + string.Join(" ", p.AccessorList.Accessors.Select(a => Join(a.Modifiers.ToString(), a.Keyword.Text) + ";")) + " }";
                        result.Add(new IndexMember(typeName, p.Identifier.Text, Join(p.Modifiers.ToString(), p.Type.ToString(), p.Identifier.Text, accessors)));
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

    private static IReadOnlyList<IndexMember> SqlMembers(string path)
    {
        var text = TextDecoding.Decode(File.ReadAllBytes(path));
        var fragment = new TSql170Parser(true).Parse(new StringReader(text), out var errors);
        if (errors.Count > 0)
            throw new InvalidDataException($"{path}: {errors[0].Message}");
        var result = new List<IndexMember>();
        foreach (var batch in ((TSqlScript)fragment).Batches)
        {
            foreach (var st in batch.Statements)
            {
                switch (st)
                {
                    case ProcedureStatementBody p:
                        result.Add(new IndexMember("", Name(p.ProcedureReference.Name), "PROCEDURE (" + string.Join(", ", p.Parameters.Select(x => Text(x))) + ")"));
                        break;
                    case FunctionStatementBody f:
                        result.Add(new IndexMember("", Name(f.Name), "FUNCTION (" + string.Join(", ", f.Parameters.Select(x => Text(x))) + ")"));
                        break;
                    case ViewStatementBody v:
                        result.Add(new IndexMember("", Name(v.SchemaObjectName), "VIEW"));
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
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}
