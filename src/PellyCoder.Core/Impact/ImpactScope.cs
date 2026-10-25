using System.IO.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Impact;

public static class ImpactScope
{
    /// <summary>
    /// The projects an impact search covers: the seeds' projects, everything they reference (upward closure), and every
    /// dependent of those. Full paths; seeds first, then upward references, then dependents in <see cref="ProjectReferenceGraph.AffectedProjects"/> order.
    /// Empty when no seed file has a project.
    /// </summary>
    /// <exception cref="ProjectScopeException">The dependents cannot be known for sure.</exception>
    public static IReadOnlyList<string> Projects(IFileSystem fs, Workspace ws, IReadOnlyCollection<string> seedFiles)
    {
        var comparer = PathComparers.ForOs;
        var seen = new HashSet<string>(comparer);
        var input = new List<string>();
        foreach (var file in seedFiles)
        {
            if (ProjectFileLocator.FindNearestCsproj(fs, file) is { } csproj && seen.Add(csproj))
                input.Add(csproj);
        }
        if (input.Count == 0) return [];

        // Upward closure: breadth-first over ProjectReference edges.
        var queue = new Queue<string>(input);
        while (queue.Count > 0)
        {
            foreach (var reference in ProjectReferenceGraph.References(fs, queue.Dequeue()))
            {
                if (seen.Add(reference))
                {
                    input.Add(reference);
                    queue.Enqueue(reference);
                }
            }
        }

        return ProjectReferenceGraph.AffectedProjects(fs, ws.Root, input);
    }
}
