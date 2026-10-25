using System.IO.Abstractions;
using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Tests.Impact;

public class ImpactScopeTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\ws" : "/ws";

    private static string P(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));

    [Fact]
    public void Projects_are_seed_projects_their_references_and_every_dependent_of_those()
    {
        var root = ImpactFixture.SameName();
        var ws = new Workspace(root, Path.Combine(root, ".pelly"));

        var projects = ImpactScope.Projects(new FileSystem(), ws, [Path.Combine(root, "Web", "OrderService.cs")]);

        Assert.Equal(Path.Combine(root, "Web", "Web.csproj"), projects[0]);
        Assert.Contains(Path.Combine(root, "Data", "Data.csproj"), projects);
        Assert.Contains(Path.Combine(root, "Web.Tests", "Web.Tests.csproj"), projects);
        Assert.Equal(3, projects.Count);
    }

    [Fact]
    public void Seed_file_outside_any_project_gives_no_project()
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("loose/A.cs"), new MockFileData("class A {}"));

        Assert.Empty(ImpactScope.Projects(fs, new Workspace(Root, P(".pelly")), [P("loose/A.cs")]));
    }

    [Fact]
    public void Scope_exception_propagates()
    {
        var fs = new MockFileSystem();
        fs.AddFile(P("App/App.csproj"), new MockFileData("<Project Sdk=\"Microsoft.NET.Sdk\"/>"));
        fs.AddFile(P("App/A.cs"), new MockFileData("class A {}"));
        fs.AddFile(P("Bad/Bad.csproj"), new MockFileData("<Project Sdk=\"Microsoft.NET.Sdk\"><ItemGroup><ProjectReference Include=\"$(Dir)\\X.csproj\" /></ItemGroup></Project>"));

        Assert.Throws<ProjectScopeException>(() => ImpactScope.Projects(fs, new Workspace(Root, P(".pelly")), [P("App/A.cs")]));
    }
}
