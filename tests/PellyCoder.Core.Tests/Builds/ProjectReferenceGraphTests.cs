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
