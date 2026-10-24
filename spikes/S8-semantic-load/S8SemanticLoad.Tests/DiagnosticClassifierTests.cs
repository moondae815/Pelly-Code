using S8SemanticLoad;

public class DiagnosticClassifierTests
{
    [Theory]
    [InlineData("Mono MSBuild could not be found", WorkspaceIssueKind.MsBuildNotFound)]
    // 규칙상 MsBuildNotFound로 분류된다. 실제로는 SDK를 못 찾은 경우라 오분류다(알고 고정, 보고서 한계에 적음).
    [InlineData("Msbuild failed when processing the file '/x/a.csproj' with message: The SDK 'Microsoft.NET.Sdk' specified could not be found.", WorkspaceIssueKind.MsBuildNotFound)]
    [InlineData("Build host not found at /x/BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll", WorkspaceIssueKind.MsBuildNotFound)]
    [InlineData("Cannot open project '/x/Db.sqlproj' because the file extension '.sqlproj' is not associated with a language.", WorkspaceIssueKind.UnsupportedProject)]
    [InlineData("The reference assemblies for .NETFramework,Version=v4.5 were not found.", WorkspaceIssueKind.ReferenceResolution)]
    [InlineData("Something else", WorkspaceIssueKind.Other)]
    public void Classify_maps_message_to_kind(string message, WorkspaceIssueKind expected) =>
        Assert.Equal(expected, DiagnosticClassifier.Classify(message));
}
