using S8SemanticLoad;

public class DiagnosticClassifierTests
{
    [Theory]
    [InlineData("Mono MSBuild could not be found", WorkspaceIssueKind.MsBuildNotFound)]
    // SDK가 없는 것도 빌드 도구가 없는 것이다. 머리말을 벗긴 뒤 명시 규칙("the sdk '")으로 분류한다.
    [InlineData("Msbuild failed when processing the file '/x/a.csproj' with message: The SDK 'Microsoft.NET.Sdk' specified could not be found.", WorkspaceIssueKind.MsBuildNotFound)]
    [InlineData("Build host not found at /x/BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll", WorkspaceIssueKind.MsBuildNotFound)]
    [InlineData("Cannot open project '/x/Db.sqlproj' because the file extension '.sqlproj' is not associated with a language.", WorkspaceIssueKind.UnsupportedProject)]
    [InlineData("The reference assemblies for .NETFramework,Version=v4.5 were not found.", WorkspaceIssueKind.ReferenceResolution)]
    [InlineData("The build host could not be found at '/x/host.dll'", WorkspaceIssueKind.MsBuildNotFound)]
    // 최종 리뷰 I1: MSBuildWorkspace가 감싼 머리말("Msbuild failed when processing ...")을 벗기고 분류한다.
    [InlineData("Msbuild failed when processing the file '/x/Jobs.csproj' with message: The reference assemblies for .NETFramework,Version=v4.8 were not found.", WorkspaceIssueKind.ReferenceResolution)]
    [InlineData("Msbuild failed when processing the file '/x/Api.csproj' with message: /usr/share/dotnet/sdk/10.0.302/Sdks/Microsoft.NET.Sdk/targets/Microsoft.PackageDependencyResolution.targets: (266, 5): Assets file '/x/obj/project.assets.json' not found. Run a NuGet package restore to generate this file.", WorkspaceIssueKind.NotRestored)]
    // 실측 전문: 안내 링크(aka.ms/msbuild/...)의 "msbuild"가 빌드 도구 없음으로 오분류되면 안 된다.
    [InlineData("Msbuild failed when processing the file '/x/Shop.Jobs.csproj' with message: The reference assemblies for .NETFramework,Version=v4.8 were not found. To resolve this, install the Developer Pack (SDK/Targeting Pack) for this framework version or retarget your application. You can download .NET Framework Developer Packs at https://aka.ms/msbuild/developerpacks", WorkspaceIssueKind.ReferenceResolution)]
    [InlineData("Something else", WorkspaceIssueKind.Other)]
    public void Classify_maps_message_to_kind(string message, WorkspaceIssueKind expected) =>
        Assert.Equal(expected, DiagnosticClassifier.Classify(message));
}
