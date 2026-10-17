using System.IO.Abstractions;
using System.Xml.Linq;

namespace PellyCoder.Core.Builds;

public enum ProjectStyle { None, Sdk, Legacy }

/// <summary>Tells SDK-style projects (<c>Sdk</c> attribute or element) from legacy .NET Framework ones.</summary>
public static class ProjectStyleDetector
{
    /// <summary>XML errors propagate to the caller (LocatePhase maps them to an environment error).</summary>
    public static ProjectStyle Detect(IFileSystem fs, string? csprojPath)
    {
        if (csprojPath is null) return ProjectStyle.None;
        var root = XDocument.Parse(fs.File.ReadAllText(csprojPath)).Root;
        return root is not null && (root.Attribute("Sdk") is not null || root.Elements().Any(e => e.Name.LocalName == "Sdk"))
            ? ProjectStyle.Sdk
            : ProjectStyle.Legacy;
    }
}
