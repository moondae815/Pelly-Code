using System.IO.Abstractions;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PellyCoder.Core.Builds;

/// <summary>The C# projects a solution lists: <c>Project(...) = "name", "path.csproj", ...</c> lines of a <c>.sln</c>, <c>&lt;Project Path&gt;</c> of a <c>.slnx</c>.</summary>
public static partial class SolutionProjects
{
    [GeneratedRegex(@"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""(?<path>[^""]+\.csproj)""", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SlnProject();

    /// <summary>
    /// Full paths of the solution's <c>.csproj</c> entries, resolved against the solution's folder, in file order; whether
    /// they exist is not checked. Throws what reading the file throws (and <see cref="System.Xml.XmlException"/> for a malformed <c>.slnx</c>).
    /// </summary>
    public static IReadOnlyList<string> Read(IFileSystem fs, string solutionPath)
    {
        var dir = fs.Path.GetDirectoryName(solutionPath) ?? ".";
        var text = fs.File.ReadAllText(solutionPath);
        IEnumerable<string> paths = solutionPath.EndsWith(".slnx", StringComparison.OrdinalIgnoreCase)
            ? XDocument.Parse(text).Descendants().Where(e => e.Name.LocalName == "Project")
                .Select(e => (string?)e.Attribute("Path") ?? "")
                .Where(p => p.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
            : SlnProject().Matches(text).Select(m => m.Groups["path"].Value);
        return paths.Select(p => fs.Path.GetFullPath(fs.Path.Combine(dir, Slashes(p, fs)))).ToList();
    }

    // Solutions written on Windows use backslashes even when read on another OS.
    private static string Slashes(string path, IFileSystem fs) => path.Replace('\\', fs.Path.DirectorySeparatorChar).Replace('/', fs.Path.DirectorySeparatorChar);
}
