using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace S8SemanticLoad;

public enum ProjectKind { SdkCSharp, LegacyCSharp, NonCSharp }

/// <summary>One project listed by a solution. <c>Restored</c> matters only for SDK-style projects.</summary>
public sealed record CensusEntry(string Path, ProjectKind Kind, bool Restored);

public sealed record ProjectCensus(IReadOnlyList<CensusEntry> Entries)
{
    public int SdkCs => Entries.Count(e => e.Kind == ProjectKind.SdkCSharp);
    public int LegacyCs => Entries.Count(e => e.Kind == ProjectKind.LegacyCSharp);
    public int NonCs => Entries.Count(e => e.Kind == ProjectKind.NonCSharp);
    public int UnrestoredSdk => Entries.Count(e => e.Kind == ProjectKind.SdkCSharp && !e.Restored);
    public int CSharpTotal => SdkCs + LegacyCs;
}

/// <summary>Lists the projects of a .sln, .slnx or single .csproj without MSBuild, so the count is known even when loading fails.</summary>
public static partial class ProjectCensusReader
{
    private const string SolutionFolderGuid = "2150E333-8FDC-42A3-9474-1A3956D46DE8";

    [GeneratedRegex(""""^Project\("\{(?<type>[^}]+)\}"\)\s*=\s*"[^"]*"\s*,\s*"(?<path>[^"]+)"""", RegexOptions.Multiline)]
    private static partial Regex SlnProjectLine();

    public static ProjectCensus Read(string slnOrProjectPath)
    {
        var full = Path.GetFullPath(slnOrProjectPath);
        var dir = Path.GetDirectoryName(full)!;
        IEnumerable<string> relative = Path.GetExtension(full).ToLowerInvariant() switch
        {
            ".sln" => SlnProjectLine().Matches(File.ReadAllText(full))
                .Where(m => !m.Groups["type"].Value.Equals(SolutionFolderGuid, StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Groups["path"].Value),
            ".slnx" => XDocument.Load(full).Descendants("Project")
                .Select(p => (string?)p.Attribute("Path")).OfType<string>(),
            _ => [Path.GetFileName(full)],
        };
        return new ProjectCensus(relative.Select(r => Entry(Path.GetFullPath(Path.Combine(dir, Normalize(r))))).ToList());
    }

    private static string Normalize(string p) => p.Replace('\\', Path.DirectorySeparatorChar).Replace('/', Path.DirectorySeparatorChar);

    private static CensusEntry Entry(string path)
    {
        if (!path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            return new CensusEntry(path, ProjectKind.NonCSharp, true);
        var isSdk = File.Exists(path) && XDocument.Load(path).Root?.Attribute("Sdk") is not null;
        if (!isSdk)
            return new CensusEntry(path, ProjectKind.LegacyCSharp, true);
        var assets = Path.Combine(Path.GetDirectoryName(path)!, "obj", "project.assets.json");
        return new CensusEntry(path, ProjectKind.SdkCSharp, File.Exists(assets));
    }
}
