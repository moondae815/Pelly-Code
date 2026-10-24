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
    /// indirectly (ordinal path order). No duplicates; reference cycles terminate. The candidates are the projects the
    /// workspace's solutions (<c>.sln</c>, <c>.slnx</c>, found by the same walk) list plus every <c>.csproj</c> the walk finds.
    /// </summary>
    /// <exception cref="ProjectScopeException">
    /// Some dependent could be missed: the walk hit <paramref name="maxDirectories"/> or could not list a folder, a solution
    /// or candidate project cannot be read, or a ProjectReference names its target through an MSBuild property (<c>$(</c>).
    /// </exception>
    public static IReadOnlyList<string> AffectedProjects(IFileSystem fs, string workspaceRoot, IReadOnlyCollection<string> changedProjects,
        int maxDirectories = MaxDirectories)
    {
        var comparer = PathComparers.ForOs;
        var candidates = Candidates(fs, workspaceRoot, maxDirectories).Order(StringComparer.Ordinal).ToList();

        // dependency -> projects that reference it directly
        var dependents = new Dictionary<string, List<string>>(comparer);
        foreach (var project in candidates)
        {
            foreach (var dependency in StrictReferences(fs, workspaceRoot, project))
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

    /// <summary>Every project the walk finds or a walked solution lists (existing files only), full paths, no duplicates.</summary>
    private static IEnumerable<string> Candidates(IFileSystem fs, string root, int maxDirectories)
    {
        // One walk: listing a folder costs the same whatever the pattern.
        var files = Walk(fs, root, maxDirectories);
        var projects = files.Where(p => HasExtension(p, ".csproj"));
        var solutions = files.Where(p => HasExtension(p, ".sln") || HasExtension(p, ".slnx"));

        var result = new HashSet<string>(projects.Select(p => fs.Path.GetFullPath(p)), PathComparers.ForOs);
        foreach (var solution in solutions)
        {
            IReadOnlyList<string> listed;
            try { listed = SolutionProjects.Read(fs, solution); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
            {
                throw new ProjectScopeException($"솔루션 파일을 읽을 수 없어 영향받는 프로젝트를 모두 찾을 수 없습니다: {Relative(fs, root, solution)} ({ex.Message})");
            }
            result.UnionWith(listed.Where(fs.File.Exists));
        }
        return result;
    }

    private static bool HasExtension(string path, string extension) => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> Walk(IFileSystem fs, string root, int maxDirectories)
    {
        var walk = BoundedWalker.Collect(fs, root, "*", maxDirectories, enterPackagesWithProjects: true);
        if (walk.Truncated)
            throw new ProjectScopeException($"작업 공간의 폴더가 너무 많아({maxDirectories}개 초과) 영향받는 프로젝트를 모두 찾을 수 없습니다");
        if (walk.Unreadable.Count > 0)
            throw new ProjectScopeException($"폴더를 읽을 수 없어 영향받는 프로젝트를 모두 찾을 수 없습니다: {Relative(fs, root, walk.Unreadable[0])}");
        return walk.Files;
    }

    /// <summary><see cref="References"/> for the scope: an unreadable project or a property-built include throws instead of hiding an edge.</summary>
    private static IReadOnlyList<string> StrictReferences(IFileSystem fs, string root, string csprojPath)
    {
        XDocument doc;
        try
        {
            doc = XDocument.Parse(fs.File.ReadAllText(csprojPath));
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            throw new ProjectScopeException($"프로젝트 파일을 읽을 수 없어 영향받는 프로젝트를 모두 찾을 수 없습니다: {Relative(fs, root, csprojPath)} ({ex.Message})");
        }

        var dir = fs.Path.GetDirectoryName(csprojPath) ?? ".";
        var result = new List<string>();
        foreach (var include in doc.Descendants().Where(e => e.Name.LocalName == "ProjectReference").Select(e => (string?)e.Attribute("Include")))
        {
            if (string.IsNullOrWhiteSpace(include)) continue;
            if (include.Contains("$(", StringComparison.Ordinal))
                throw new ProjectScopeException(
                    $"프로젝트 {Relative(fs, root, csprojPath)}의 ProjectReference \"{include}\"가 MSBuild 속성으로 경로를 만들어 참조 대상을 알 수 없습니다");
            result.Add(fs.Path.GetFullPath(fs.Path.Combine(dir, Slashes(include, fs))));
        }
        return result;
    }

    private static string Relative(IFileSystem fs, string root, string path) => fs.Path.GetRelativePath(root, path).Replace('\\', '/');

    // Legacy projects use backslashes even when read on another OS.
    private static string Slashes(string path, IFileSystem fs) => path.Replace('\\', fs.Path.DirectorySeparatorChar).Replace('/', fs.Path.DirectorySeparatorChar);
}

/// <summary>The affected projects of a change cannot be known for sure; the message (Korean, for the user) names the cause.</summary>
public sealed class ProjectScopeException(string message) : Exception(message);
