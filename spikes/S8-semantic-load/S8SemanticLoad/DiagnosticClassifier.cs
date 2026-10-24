namespace S8SemanticLoad;

public enum WorkspaceIssueKind { MsBuildNotFound, UnsupportedProject, ReferenceResolution, Other }

/// <summary>
/// Sorts workspace diagnostic messages into fixed kinds so field reports carry counts, never messages.
/// A missing MSBuild raises no exception — the build host silently falls back — so this is the only signal.
/// Known weakness: "SDK ... could not be found" also reads as MsBuildNotFound.
/// </summary>
public static class DiagnosticClassifier
{
    public static WorkspaceIssueKind Classify(string message)
    {
        var m = message.ToLowerInvariant();
        bool Has(params string[] any) => any.Any(m.Contains);

        if (m.Contains("build host not found")
            || (m.Contains("msbuild") && Has("not found", "could not be found", "cannot find", "no msbuild")))
            return WorkspaceIssueKind.MsBuildNotFound;
        if (Has("not supported", "unsupported", "cannot open project", "project file type"))
            return WorkspaceIssueKind.UnsupportedProject;
        if (m.Contains("reference") && Has("could not be resolved", "not found", "cannot resolve"))
            return WorkspaceIssueKind.ReferenceResolution;
        return WorkspaceIssueKind.Other;
    }
}
