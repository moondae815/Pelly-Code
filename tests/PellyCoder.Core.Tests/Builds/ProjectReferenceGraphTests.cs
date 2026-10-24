using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Builds;

namespace PellyCoder.Core.Tests.Builds;

public class ProjectReferenceGraphTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";

    private static string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static void Proj(MockFileSystem fs, string name, params string[] refs)
    {
        var items = string.Concat(refs.Select(r => $"<ProjectReference Include=\"..\\{r}\\{r}.csproj\" />"));
        fs.AddFile(P($"{name}/{name}.csproj"), new MockFileData($"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>{items}</ItemGroup></Project>"));
    }

    [Fact]
    public void References_reads_includes_with_backslashes()
    {
        var fs = new MockFileSystem();
        Proj(fs, "App", "Lib");

        var refs = ProjectReferenceGraph.References(fs, P("App/App.csproj"));

        Assert.Equal([P("Lib/Lib.csproj")], refs);
    }

    [Fact]
    public void References_of_broken_xml_is_empty()
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("App/App.csproj"), new MockFileData("<Project><oops"));

        Assert.Empty(ProjectReferenceGraph.References(fs, P("App/App.csproj")));
    }

    [Fact]
    public void Affected_includes_transitive_dependents_only()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib");
        Proj(fs, "App", "Lib");
        Proj(fs, "Web", "App");
        Proj(fs, "Other");

        var affected = ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj")]);

        Assert.Equal([P("Lib/Lib.csproj"), P("App/App.csproj"), P("Web/Web.csproj")], affected);
    }

    [Fact]
    public void Affected_handles_reference_cycles()
    {
        var fs = new MockFileSystem();
        Proj(fs, "A", "B");
        Proj(fs, "B", "A");

        var affected = ProjectReferenceGraph.AffectedProjects(fs, Root, [P("A/A.csproj")]);

        Assert.Equal([P("A/A.csproj"), P("B/B.csproj")], affected);
    }
}

public class ProjectReferenceGraphScopeTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";

    private static string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    private static void Proj(MockFileSystem fs, string path, params string[] includes)
    {
        var items = string.Concat(includes.Select(r => $"<ProjectReference Include=\"{r}\" />"));
        fs.AddFile(P(path), new MockFileData($"<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup>{items}</ItemGroup></Project>"));
    }

    [Fact]
    public void Truncated_walk_makes_the_scope_unknown()
    {
        var fs = new MockFileSystem();
        Proj(fs, "A/Lib/Lib.csproj");
        Proj(fs, "Z/Web/Web.csproj", @"..\..\A\Lib\Lib.csproj");

        var ex = Assert.Throws<ProjectScopeException>(() =>
            ProjectReferenceGraph.AffectedProjects(fs, Root, [P("A/Lib/Lib.csproj")], maxDirectories: 3));

        Assert.Contains("폴더가 너무 많아", ex.Message);
    }

    [Fact]
    public void Property_in_a_project_reference_makes_the_scope_unknown()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib/Lib.csproj");
        Proj(fs, "Web/Web.csproj", @"$(SolutionDir)Lib\Lib.csproj");

        var ex = Assert.Throws<ProjectScopeException>(() => ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj")]));

        Assert.Contains("Web/Web.csproj", ex.Message);
        Assert.Contains("$(SolutionDir)", ex.Message);
    }

    [Fact]
    public void Malformed_project_makes_the_scope_unknown()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib/Lib.csproj");
        fs.AddFile(P("Web/Web.csproj"), new MockFileData("<Project><oops"));

        var ex = Assert.Throws<ProjectScopeException>(() => ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj")]));

        Assert.Contains("Web/Web.csproj", ex.Message);
    }

    [Fact]
    public void Project_listed_only_in_the_solution_is_in_scope()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib/Lib.csproj");
        // Outside the walked tree: only the solution names it.
        fs.AddFile(Path.Combine(Path.GetDirectoryName(Root)!, "Shared", "Web", "Web.csproj"),
            new MockFileData(@"<Project Sdk=""Microsoft.NET.Sdk""><ItemGroup><ProjectReference Include=""..\..\ws\Lib\Lib.csproj"" /></ItemGroup></Project>"));
        fs.AddFile(P("App.sln"), new MockFileData(
            "Project(\"{9A19103F-16F7-4668-BE54-9A1E7A4F7556}\") = \"Web\", \"..\\Shared\\Web\\Web.csproj\", \"{11111111-1111-1111-1111-111111111111}\"\nEndProject\n"));

        var affected = ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj")]);

        Assert.Equal([P("Lib/Lib.csproj"), Path.Combine(Path.GetDirectoryName(Root)!, "Shared", "Web", "Web.csproj")], affected);
    }

    [Fact]
    public void Project_listed_only_in_an_slnx_is_in_scope()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib/Lib.csproj");
        fs.AddFile(Path.Combine(Path.GetDirectoryName(Root)!, "Shared", "Web", "Web.csproj"),
            new MockFileData(@"<Project Sdk=""Microsoft.NET.Sdk""><ItemGroup><ProjectReference Include=""..\..\ws\Lib\Lib.csproj"" /></ItemGroup></Project>"));
        fs.AddFile(P("App.slnx"), new MockFileData("<Solution><Folder Name=\"/x/\"><Project Path=\"../Shared/Web/Web.csproj\" /></Folder></Solution>"));

        var affected = ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj")]);

        Assert.Contains(Path.Combine(Path.GetDirectoryName(Root)!, "Shared", "Web", "Web.csproj"), affected);
    }

    [Fact]
    public void Packages_folder_holding_a_project_is_walked()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib/Lib.csproj");
        Proj(fs, "packages/Web/Web.csproj", @"..\..\Lib\Lib.csproj");

        var affected = ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj")]);

        Assert.Equal([P("Lib/Lib.csproj"), P("packages/Web/Web.csproj")], affected);
    }
}
