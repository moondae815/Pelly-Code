using System.IO.Abstractions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.CharTests;

/// <summary>Where the characterization tests go. A non-null <see cref="Error"/> means exit 3.</summary>
public sealed record TestClassLocation(string FilePath, string ClassName, bool IsNew, string Namespace, string? Error);

public static class TestClassLocator
{
    public static TestClassLocation Find(IFileSystem fs, string testCsproj, string targetTypeName, LanguageVersion version)
    {
        var dir = fs.Path.GetDirectoryName(fs.Path.GetFullPath(testCsproj)) ?? ".";
        var className = targetTypeName + "Tests";
        var options = new CSharpParseOptions(version);

        var namespaceCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        var declaring = new List<(string Path, string? Namespace)>();
        foreach (var (path, text) in BoundedSources.Read(fs, dir, "*.cs"))
        {
            var root = CSharpSyntaxTree.ParseText(text, options).GetRoot();
            var ns = root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().FirstOrDefault()?.Name.ToString();
            if (!string.IsNullOrEmpty(ns)) namespaceCounts[ns] = namespaceCounts.GetValueOrDefault(ns) + 1;
            if (root.DescendantNodes().OfType<ClassDeclarationSyntax>().Any(c => c.Identifier.ValueText == className))
                declaring.Add((path, ns));
        }

        if (declaring.Count > 0)
        {
            var chosen = declaring.FirstOrDefault(d => fs.Path.GetFileName(d.Path) == className + ".cs", declaring[0]);
            return new TestClassLocation(chosen.Path, className, false, chosen.Namespace ?? Fallback(fs, testCsproj, namespaceCounts), null);
        }

        var newPath = fs.Path.Combine(dir, className + ".cs");
        if (fs.File.Exists(newPath))
            return new TestClassLocation(newPath, className, true, "", $"같은 이름의 파일이 있지만 {className} 클래스가 아닙니다: {newPath}");
        return new TestClassLocation(newPath, className, true, Fallback(fs, testCsproj, namespaceCounts), null);
    }

    private static string Fallback(IFileSystem fs, string testCsproj, Dictionary<string, int> counts)
    {
        if (counts.Count > 0)
            return counts.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;

        var root = ProjectProperty(fs, testCsproj, "RootNamespace");
        if (!string.IsNullOrWhiteSpace(root)) return root;
        var assembly = ProjectProperty(fs, testCsproj, "AssemblyName");
        if (!string.IsNullOrWhiteSpace(assembly)) return assembly;
        return fs.Path.GetFileNameWithoutExtension(testCsproj);
    }

    private static string? ProjectProperty(IFileSystem fs, string csproj, string name)
    {
        try
        {
            return XDocument.Parse(fs.File.ReadAllText(csproj)).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
        }
        catch (Exception ex) when (ex is System.Xml.XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
