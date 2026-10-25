using System.IO.Abstractions.TestingHelpers;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.Impact;

namespace PellyCoder.Core.Tests.Impact;

public class SemanticHealthTests
{
    // Cases ported from the S8 DiagnosticClassifierTests.
    [Theory]
    [InlineData("Mono MSBuild could not be found", SemanticFailureKind.MsBuildNotFound)]
    // A missing .NET SDK is missing build tooling too; the wrapper is stripped first.
    [InlineData("Msbuild failed when processing the file '/x/a.csproj' with message: The SDK 'Microsoft.NET.Sdk' specified could not be found.", SemanticFailureKind.MsBuildNotFound)]
    [InlineData("build host not found", SemanticFailureKind.MsBuildNotFound)]
    [InlineData("Build host not found at /x/BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll", SemanticFailureKind.MsBuildNotFound)]
    [InlineData("The build host could not be found at '/x/host.dll'", SemanticFailureKind.MsBuildNotFound)]
    [InlineData("Msbuild failed when processing the file '/x/Api.csproj' with message: /usr/share/dotnet/sdk/10.0.302/Sdks/Microsoft.NET.Sdk/targets/Microsoft.PackageDependencyResolution.targets: (266, 5): Assets file '/x/obj/project.assets.json' not found. Run a NuGet package restore to generate this file.", SemanticFailureKind.NotRestored)]
    [InlineData("Cannot open project '/x/Db.sqlproj' because the file extension '.sqlproj' is not associated with a language.", SemanticFailureKind.LoadFailed)]
    [InlineData("The reference assemblies for .NETFramework,Version=v4.5 were not found.", SemanticFailureKind.LoadFailed)]
    [InlineData("Msbuild failed when processing the file '/x/Jobs.csproj' with message: The reference assemblies for .NETFramework,Version=v4.8 were not found.", SemanticFailureKind.LoadFailed)]
    // The wrapper's (and the help link's) "msbuild" must not turn an inner error into MsBuildNotFound.
    [InlineData("Msbuild failed when processing the file '/x/Shop.Jobs.csproj' with message: The reference assemblies for .NETFramework,Version=v4.8 were not found. To resolve this, install the Developer Pack (SDK/Targeting Pack) for this framework version or retarget your application. You can download .NET Framework Developer Packs at https://aka.ms/msbuild/developerpacks", SemanticFailureKind.LoadFailed)]
    public void Classify_maps_message_to_kind(string message, SemanticFailureKind expected) =>
        Assert.Equal(expected, SemanticHealth.Classify(message));

    [Theory]
    [InlineData("Something else")]
    [InlineData("Msbuild failed when processing the file '/x/a.csproj' with message: Something else went wrong.")]
    public void Classify_returns_null_for_other_messages(string message) =>
        Assert.Null(SemanticHealth.Classify(message));

    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\src" : "/src";
    private static string Csproj => Path.Combine(Root, "App", "App.csproj");
    private static string Assets => Path.Combine(Root, "App", "obj", "project.assets.json");

    private static MockFileSystem RestoredSdkFs() => new(new Dictionary<string, MockFileData>
    {
        [Csproj] = new("<Project Sdk=\"Microsoft.NET.Sdk\" />"),
        [Assets] = new("{}"),
    });

    private static Solution Build(string code, bool withReferences = true, string? filePath = null)
    {
        var ws = new AdhocWorkspace();
        var refs = withReferences
            ? ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
                .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p)).ToArray()
            : [];
        var info = ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "App", "App", LanguageNames.CSharp,
            filePath: filePath ?? Csproj,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences: refs);
        var project = ws.AddProject(info);
        return ws.AddDocument(project.Id, "A.cs", Microsoft.CodeAnalysis.Text.SourceText.From(code)).Project.Solution;
    }

    private const string Healthy = """
        public class A
        {
            public int F() => G() + nameof(F).Length;
            private int G() => 1;
            public void D(dynamic d) { d.Anything(); }
        }
        """;

    private static Task<SemanticFailure?> Judge(Solution s, IReadOnlyList<WorkspaceDiagnostic>? diags = null,
        IReadOnlyCollection<string>? requested = null, MockFileSystem? fs = null) =>
        SemanticHealth.JudgeAsync(s, diags ?? [], requested ?? [Csproj], fs ?? RestoredSdkFs(), TestContext.Current.CancellationToken);

    [Fact]
    public async Task Judge_healthy_solution_is_null() =>
        Assert.Null(await Judge(Build(Healthy)));

    [Fact]
    public async Task Judge_msbuild_not_found_wins_over_everything()
    {
        var failure = await Judge(Build("class A { void F() { Missing(); } }"),
            [new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, "build host not found"),
             new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, "Assets file '/x/obj/project.assets.json' not found.")],
            fs: new MockFileSystem(new Dictionary<string, MockFileData> { [Csproj] = new("<Project Sdk=\"Microsoft.NET.Sdk\" />") }));
        Assert.Equal(SemanticFailureKind.MsBuildNotFound, failure?.Kind);
    }

    [Fact]
    public async Task Judge_sdk_project_without_assets_is_not_restored()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData> { [Csproj] = new("<Project Sdk=\"Microsoft.NET.Sdk\" />") });
        Assert.Equal(SemanticFailureKind.NotRestored, (await Judge(Build(Healthy), fs: fs))?.Kind);
    }

    [Fact]
    public async Task Judge_legacy_project_without_assets_is_not_judged_by_restore()
    {
        var fs = new MockFileSystem(new Dictionary<string, MockFileData> { [Csproj] = new("<Project ToolsVersion=\"15.0\" />") });
        Assert.Null(await Judge(Build(Healthy), fs: fs));
    }

    [Fact]
    public async Task Judge_not_restored_diagnostic_is_not_restored()
    {
        var failure = await Judge(Build(Healthy),
            [new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, "Assets file '/x/obj/project.assets.json' not found.")]);
        Assert.Equal(SemanticFailureKind.NotRestored, failure?.Kind);
    }

    [Fact]
    public async Task Judge_requested_project_not_opened_is_load_failed()
    {
        var other = Path.Combine(Root, "Other", "Other.csproj");
        var fs = RestoredSdkFs();
        fs.AddFile(other, new MockFileData("<Project Sdk=\"Microsoft.NET.Sdk\" />"));
        fs.AddFile(Path.Combine(Root, "Other", "obj", "project.assets.json"), new MockFileData("{}"));
        Assert.Equal(SemanticFailureKind.LoadFailed, (await Judge(Build(Healthy), requested: [Csproj, other], fs: fs))?.Kind);
    }

    [Fact]
    public async Task Judge_failure_diagnostic_is_load_failed()
    {
        var failure = await Judge(Build(Healthy),
            [new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Failure, "Something else")]);
        Assert.Equal(SemanticFailureKind.LoadFailed, failure?.Kind);
    }

    [Fact]
    public async Task Judge_warning_diagnostic_alone_is_healthy() =>
        Assert.Null(await Judge(Build(Healthy), [new WorkspaceDiagnostic(WorkspaceDiagnosticKind.Warning, "Something else")]));

    [Fact]
    public async Task Judge_unbound_calls_are_low_resolution_before_compile_errors()
    {
        // 1 of 2 calls resolves: rate 0.5, and the unbound call is also a compile error.
        var failure = await Judge(Build("public class A { void F() { G(); Missing(); } void G() { } }"));
        Assert.Equal(SemanticFailureKind.LowResolution, failure?.Kind);
        Assert.Equal(0.5, failure!.Rate);
    }

    [Fact]
    public async Task Judge_compile_errors_with_full_resolution_are_counted()
    {
        var failure = await Judge(Build("public class A { void F() { G(); } void G() { } int H() { return \"x\"; } }"));
        Assert.Equal(SemanticFailureKind.CompileErrors, failure?.Kind);
        Assert.Equal(1, failure!.Count);
    }

    [Fact]
    public async Task Judge_without_references_is_not_healthy() =>
        Assert.NotNull(await Judge(Build(Healthy, withReferences: false)));

    [Fact]
    public async Task Judge_no_calls_counts_as_full_resolution() =>
        Assert.Null(await Judge(Build("public class A { }")));
}
