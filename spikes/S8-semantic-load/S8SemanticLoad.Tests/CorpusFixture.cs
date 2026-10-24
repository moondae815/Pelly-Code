using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using PellyCoder.Core.Files;

/// <summary>
/// The Legacy corpus as an in-memory solution: sources and project references read from the csproj files,
/// .NET platform references instead of .NET Framework ones. Enough for symbol finding, not for a clean compile.
/// </summary>
public static class CorpusFixture
{
    public static Solution LegacyAsAdhoc()
    {
        var root = Path.Combine(TestPaths.Corpus, "Legacy");
        var order = new[] { "Shop.Data", "Shop.Web", "Shop.Batch", "Shop.Tests" };
        var projects = order.Select(name =>
        {
            var dir = Path.Combine(root, name);
            var xml = XDocument.Load(Path.Combine(dir, name + ".csproj"));
            XNamespace ns = xml.Root!.Name.Namespace;
            var docs = xml.Descendants(ns + "Compile").Select(c => (string)c.Attribute("Include")!)
                .Select(rel => Path.Combine(dir, rel.Replace('\\', Path.DirectorySeparatorChar)))
                .Select(path => (path, TextDecoding.Decode(File.ReadAllBytes(path)))).ToArray();
            var refs = xml.Descendants(ns + "ProjectReference")
                .Select(r => Path.GetFileNameWithoutExtension(((string)r.Attribute("Include")!).Replace('\\', '/'))).ToArray();
            return (name, refs, docs);
        }).ToArray();
        return AdhocFixture.Build(projects);
    }
}
