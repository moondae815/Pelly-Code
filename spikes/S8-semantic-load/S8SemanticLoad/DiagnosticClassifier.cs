namespace S8SemanticLoad;

public enum WorkspaceIssueKind { MsBuildNotFound, UnsupportedProject, ReferenceResolution, NotRestored, Other }

/// <summary>
/// Sorts workspace diagnostic messages into fixed kinds so field reports carry counts, never messages.
/// A missing MSBuild raises no exception — the build host silently falls back — so this is the only signal.
/// A missing .NET SDK ("The SDK '…' specified could not be found") counts as missing build tooling too.
/// </summary>
public static class DiagnosticClassifier
{
    private const string WrapperMarker = "with message:";

    public static WorkspaceIssueKind Classify(string message)
    {
        // MSBuildWorkspace wraps every design-time build error as
        // "Msbuild failed when processing the file '…' with message: <inner>". The wrapper's "msbuild"
        // would turn any inner "not found" into MsBuildNotFound, so classify the inner message only.
        var wrapped = message.IndexOf(WrapperMarker, StringComparison.OrdinalIgnoreCase);
        var m = (wrapped >= 0 ? message[(wrapped + WrapperMarker.Length)..] : message).ToLowerInvariant();
        bool Has(params string[] any) => any.Any(m.Contains);

        if (Has("build host not found", "build host could not be found")
            || (m.Contains("the sdk '") && Has("not found", "could not be found"))
            || Has("msbuild could not be found", "msbuild was not found", "msbuild not found", "could not find msbuild",
                   "cannot find msbuild", "no msbuild", "msbuild.exe could not", "unable to locate msbuild"))
            return WorkspaceIssueKind.MsBuildNotFound;
        if (m.Contains("project.assets.json") || (m.Contains("assets file") && m.Contains("not found")))
            return WorkspaceIssueKind.NotRestored;
        if (Has("not supported", "unsupported", "cannot open project", "project file type"))
            return WorkspaceIssueKind.UnsupportedProject;
        if (m.Contains("reference") && Has("could not be resolved", "not found", "cannot resolve"))
            return WorkspaceIssueKind.ReferenceResolution;
        return WorkspaceIssueKind.Other;
    }
}
