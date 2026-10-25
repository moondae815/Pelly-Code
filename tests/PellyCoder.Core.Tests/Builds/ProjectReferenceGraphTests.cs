using System.IO.Abstractions;
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

    [Fact]
    public void Affected_keeps_changed_projects_in_input_order_then_dependents_in_ordinal_order()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib");
        Proj(fs, "Core");
        // Discovered through Lib first, but listed by ordinal path: Alpha before Zeta.
        Proj(fs, "Zeta", "Lib");
        Proj(fs, "Alpha", "Core");
        Proj(fs, "Mid", "Zeta");

        var affected = ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj"), P("Core/Core.csproj")]);

        Assert.Equal([P("Lib/Lib.csproj"), P("Core/Core.csproj"), P("Alpha/Alpha.csproj"), P("Mid/Mid.csproj"), P("Zeta/Zeta.csproj")], affected);
    }

    [Fact]
    public void Affected_lists_a_changed_project_that_is_also_a_dependent_once_at_its_input_position()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib");
        Proj(fs, "App", "Lib");
        Proj(fs, "Web", "App", "Lib");

        var affected = ProjectReferenceGraph.AffectedProjects(fs, Root, [P("App/App.csproj"), P("Lib/Lib.csproj"), P("App/App.csproj")]);

        Assert.Equal([P("App/App.csproj"), P("Lib/Lib.csproj"), P("Web/Web.csproj")], affected);
    }

    [Fact]
    public void Affected_compares_paths_with_the_os_comparer()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib");
        Proj(fs, "App", "Lib");
        var otherCase = P("lib/LIB.csproj");

        var affected = ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj"), otherCase]);

        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
            // Case-insensitive file systems: the two spellings are one project, and App references it.
            Assert.Equal([P("Lib/Lib.csproj"), P("App/App.csproj")], affected);
        else
            Assert.Equal([P("Lib/Lib.csproj"), otherCase, P("App/App.csproj")], affected);

        // A dependent is found through the changed project spelled in another case only where paths ignore case.
        var viaOtherCase = ProjectReferenceGraph.AffectedProjects(fs, Root, [otherCase]);
        Assert.Equal(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? [otherCase, P("App/App.csproj")] : [otherCase], viaOtherCase);
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

    [Fact]
    public void Packages_folder_with_a_project_two_levels_down_is_walked()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib/Lib.csproj");
        Proj(fs, "packages/Web/src/Web.csproj", @"..\..\..\Lib\Lib.csproj");

        var affected = ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj")]);

        Assert.Equal([P("Lib/Lib.csproj"), P("packages/Web/src/Web.csproj")], affected);
    }

    [Fact]
    public void NuGet_packages_folder_is_not_walked()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib/Lib.csproj");
        fs.AddFile(P("packages/Newtonsoft.Json.13.0.1/Newtonsoft.Json.13.0.1.nupkg"), new MockFileData(""));
        fs.AddFile(P("packages/Sample.1.0.0/Sample.1.0.0.nupkg"), new MockFileData(""));
        // Walking into the package would make the scope unknown (a property-built reference).
        Proj(fs, "packages/Sample.1.0.0/content/Sample.csproj", @"$(SolutionDir)Lib\Lib.csproj");

        var affected = ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj")]);

        Assert.Equal([P("Lib/Lib.csproj")], affected);
    }

    [Fact]
    public void Packages_folder_with_a_subfolder_that_is_not_a_package_is_walked()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib/Lib.csproj");
        fs.AddFile(P("packages/Newtonsoft.Json.13.0.1/Newtonsoft.Json.13.0.1.nupkg"), new MockFileData(""));
        Proj(fs, "packages/Tools/Web/Web.csproj", @"..\..\..\Lib\Lib.csproj");

        var affected = ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj")]);

        Assert.Equal([P("Lib/Lib.csproj"), P("packages/Tools/Web/Web.csproj")], affected);
    }

    private static string Outside => Path.Combine(Path.GetDirectoryName(Root)!, "elsewhere");

    [Fact]
    public void Link_to_a_folder_outside_the_workspace_makes_the_scope_unknown()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib/Lib.csproj");
        fs.AddFile(Path.Combine(Outside, "Web", "Web.csproj"), new MockFileData(
            @"<Project Sdk=""Microsoft.NET.Sdk""><ItemGroup><ProjectReference Include=""..\..\ws\Lib\Lib.csproj"" /></ItemGroup></Project>"));
        fs.Directory.CreateSymbolicLink(P("linked"), Outside);
        Assert.NotNull(fs.DirectoryInfo.New(P("linked")).LinkTarget); // the mock models links, so the real check is exercised

        var ex = Assert.Throws<ProjectScopeException>(() => ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj")]));

        Assert.Contains("linked", ex.Message);
        Assert.Contains("링크", ex.Message);
    }

    [Fact]
    public void Link_to_a_folder_inside_the_workspace_is_skipped()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib/Lib.csproj");
        Proj(fs, "Web/Web.csproj", @"..\Lib\Lib.csproj");
        fs.Directory.CreateSymbolicLink(P("alias"), P("Web"));
        fs.Directory.CreateSymbolicLink(P("relative"), Path.Combine("..", "ws", "Web"));

        var affected = ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj")]);

        Assert.Equal([P("Lib/Lib.csproj"), P("Web/Web.csproj")], affected);
    }

    [Fact]
    public void Reparse_point_without_a_link_target_makes_the_scope_unknown()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib/Lib.csproj");
        fs.AddFile(P("junction/Web/Web.csproj"), new MockFileData("<Project />"));
        fs.DirectoryInfo.New(P("junction")).Attributes |= FileAttributes.ReparsePoint;

        var ex = Assert.Throws<ProjectScopeException>(() => ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj")]));

        Assert.Contains("junction", ex.Message);
    }

    [Fact]
    public void Folder_whose_attributes_cannot_be_read_makes_the_scope_unknown()
    {
        var mock = new MockFileSystem();
        Proj(mock, "Lib/Lib.csproj");
        Proj(mock, "locked/Web/Web.csproj", @"..\..\Lib\Lib.csproj");
        var fs = new DeniedInfoFileSystem(mock, P("locked"));

        var ex = Assert.Throws<ProjectScopeException>(() => ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj")]));

        Assert.Contains("폴더를 읽을 수 없어", ex.Message);
        Assert.Contains("locked", ex.Message);
    }

    [Fact]
    public void Missing_referenced_project_in_scope_makes_the_scope_unknown()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib/Lib.csproj");
        Proj(fs, "Web/Web.csproj", @"..\Lib\Lib.csproj", @"..\Gone\Gone.csproj");

        var ex = Assert.Throws<ProjectScopeException>(() => ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj")]));

        Assert.Contains("Web/Web.csproj", ex.Message);
        Assert.Contains("Gone/Gone.csproj", ex.Message);
    }

    [Fact]
    public void Missing_referenced_project_outside_the_scope_is_ignored()
    {
        var fs = new MockFileSystem();
        Proj(fs, "Lib/Lib.csproj");
        Proj(fs, "Tools/Tools.csproj", @"..\Gone\Gone.csproj");

        var affected = ProjectReferenceGraph.AffectedProjects(fs, Root, [P("Lib/Lib.csproj")]);

        Assert.Equal([P("Lib/Lib.csproj")], affected);
    }

    /// <summary>A file system that cannot read the attributes of one directory (its listing in the parent still works).</summary>
    private sealed class DeniedInfoFileSystem(MockFileSystem inner, string denied) : IFileSystem
    {
        private readonly IDirectoryInfoFactory _info = new DeniedInfoFactory(inner, denied);
        public IFile File => inner.File;
        public IDirectory Directory => inner.Directory;
        public IFileInfoFactory FileInfo => inner.FileInfo;
        public IFileStreamFactory FileStream => inner.FileStream;
        public IPath Path => inner.Path;
        public IDirectoryInfoFactory DirectoryInfo => _info;
        public IDriveInfoFactory DriveInfo => inner.DriveInfo;
        public IFileSystemWatcherFactory FileSystemWatcher => inner.FileSystemWatcher;
        public IFileVersionInfoFactory FileVersionInfo => inner.FileVersionInfo;
        public IRandomAccess RandomAccess => inner.RandomAccess;
    }

    private sealed class DeniedInfoFactory(MockFileSystem inner, string denied) : IDirectoryInfoFactory
    {
        public IFileSystem FileSystem => inner;
        public IDirectoryInfo New(string path) =>
            inner.Path.GetFullPath(path).TrimEnd('/', '\\') == denied ? throw new UnauthorizedAccessException("denied") : inner.DirectoryInfo.New(path);
        public IDirectoryInfo? Wrap(DirectoryInfo? directoryInfo) => inner.DirectoryInfo.Wrap(directoryInfo);
    }
}
