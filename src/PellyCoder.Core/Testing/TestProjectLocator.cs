using System.IO.Abstractions;
using System.Text.RegularExpressions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Testing;

/// <summary>
/// Finds the test projects that exercise a target project (design D18): the solution's projects (or, without a
/// solution, every csproj under the workspace, R6) whose name contains "Test" and that reference the target.
/// </summary>
public static partial class TestProjectLocator
{
    [GeneratedRegex(@"^Project\(""\{[^}]+\}""\)\s*=\s*""[^""]*""\s*,\s*""(?<path>[^""]+\.csproj)""", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SlnProject();

    public static IReadOnlyList<string> Find(IFileSystem fs, string workspaceRoot, string targetCsproj, TestOptions options)
    {
        if (options.Projects.Count > 0)
        {
            var explicitProjects = new List<string>();
            foreach (var rel in options.Projects)
            {
                var full = fs.Path.GetFullPath(fs.Path.Combine(workspaceRoot, Slashes(rel, fs)));
                if (!fs.File.Exists(full)) throw new FileNotFoundException($"test.projects에 적힌 파일이 없습니다: {rel}", full);
                explicitProjects.Add(full);
            }
            return explicitProjects;
        }

        // Windows paths compare ignoring case; elsewhere exactly.
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var target = fs.Path.GetFullPath(targetCsproj);
        var found = new List<string>();
        foreach (var candidate in Candidates(fs, workspaceRoot))
        {
            var name = fs.Path.GetFileNameWithoutExtension(candidate);
            if (!name.Contains("Test", StringComparison.OrdinalIgnoreCase)) continue;
            if (ProjectReferenceGraph.References(fs, candidate).Any(r => comparer.Equals(r, target))) found.Add(candidate);
        }
        return found.Distinct(comparer).ToList();
    }

    private const int MaxDirectories = 2000;

    private static IEnumerable<string> Candidates(IFileSystem fs, string root)
    {
        var slns = fs.Directory.EnumerateFiles(root, "*.sln").Order(StringComparer.Ordinal).ToList();
        if (slns.Count == 0)
        {
            // The same bounded walk the DTO scan uses: no build or tool folders, no links, a capped directory count.
            foreach (var p in BoundedWalker.Files(fs, root, "*.csproj", MaxDirectories))
                yield return fs.Path.GetFullPath(p);
            yield break;
        }

        foreach (var sln in slns)
        {
            foreach (Match m in SlnProject().Matches(fs.File.ReadAllText(sln)))
            {
                var full = fs.Path.GetFullPath(fs.Path.Combine(fs.Path.GetDirectoryName(sln) ?? root, Slashes(m.Groups["path"].Value, fs)));
                if (fs.File.Exists(full)) yield return full;
            }
        }
    }

    // Legacy projects and solutions use backslashes even when read on another OS.
    private static string Slashes(string path, IFileSystem fs) => path.Replace('\\', fs.Path.DirectorySeparatorChar).Replace('/', fs.Path.DirectorySeparatorChar);
}
