using System.IO.Abstractions;
using System.Xml;
using System.Xml.Linq;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Builds;

/// <summary>
/// The ProjectReference edges between the workspace's csproj files, and the projects a change can break:
/// the changed ones plus everything that references them, directly or through other projects.
/// </summary>
public static class ProjectReferenceGraph
{
    private const int MaxDirectories = 2000;

    /// <summary>The full paths a project's ProjectReference items point at; an unreadable project has none.</summary>
    public static IReadOnlyList<string> References(IFileSystem fs, string csprojPath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(fs.File.ReadAllText(csprojPath));
        }
        catch (XmlException)
        {
            return []; // an unreadable project cannot be matched
        }

        var dir = fs.Path.GetDirectoryName(csprojPath) ?? ".";
        var result = new List<string>();
        foreach (var include in doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference").Select(e => (string?)e.Attribute("Include")))
        {
            if (!string.IsNullOrWhiteSpace(include))
                result.Add(fs.Path.GetFullPath(fs.Path.Combine(dir, Slashes(include, fs))));
        }
        return result;
    }

    /// <summary>
    /// The changed projects (input order) followed by the workspace projects that reference them, directly or
    /// indirectly (ordinal path order). No duplicates; reference cycles terminate.
    /// </summary>
    public static IReadOnlyList<string> AffectedProjects(IFileSystem fs, string workspaceRoot, IReadOnlyCollection<string> changedProjects)
    {
        var comparer = PathComparers.ForOs;
        var candidates = BoundedWalker.Files(fs, workspaceRoot, "*.csproj", MaxDirectories)
            .Select(p => fs.Path.GetFullPath(p)).Distinct(comparer).Order(StringComparer.Ordinal).ToList();

        // dependency -> projects that reference it directly
        var dependents = new Dictionary<string, List<string>>(comparer);
        foreach (var project in candidates)
        {
            foreach (var dependency in References(fs, project))
            {
                if (!dependents.TryGetValue(dependency, out var list)) dependents[dependency] = list = [];
                list.Add(project);
            }
        }

        var affected = new List<string>();
        var seen = new HashSet<string>(comparer);
        foreach (var changed in changedProjects)
        {
            var full = fs.Path.GetFullPath(changed);
            if (seen.Add(full)) affected.Add(full);
        }

        var reached = new List<string>();
        var queue = new Queue<string>(affected);
        while (queue.Count > 0)
        {
            if (!dependents.TryGetValue(queue.Dequeue(), out var direct)) continue;
            foreach (var dependent in direct)
            {
                if (!seen.Add(dependent)) continue;
                reached.Add(dependent);
                queue.Enqueue(dependent);
            }
        }

        affected.AddRange(reached.Order(StringComparer.Ordinal));
        return affected;
    }

    // Legacy projects use backslashes even when read on another OS.
    private static string Slashes(string path, IFileSystem fs) => path.Replace('\\', fs.Path.DirectorySeparatorChar).Replace('/', fs.Path.DirectorySeparatorChar);
}
