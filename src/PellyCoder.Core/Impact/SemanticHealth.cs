using System.IO.Abstractions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Impact;

/// <summary>
/// Whether a loaded solution can be trusted for symbol search, by the criteria fixed in the S8 spike:
/// build tooling present, SDK projects restored, every requested project opened without a failure,
/// call resolution rate at least <see cref="MinCallRate"/> and no compile errors. A half-failed load opens
/// without exceptions but leaves calls unbound, so the rate is the main signal.
/// </summary>
public static class SemanticHealth
{
    public const double MinCallRate = 0.99;

    private const string WrapperMarker = "with message:";

    /// <summary>
    /// Sorts a workspace diagnostic message (S8 DiagnosticClassifier). A missing MSBuild raises no exception —
    /// the build host silently falls back — so this is the only signal. Null for anything else.
    /// </summary>
    public static SemanticFailureKind? Classify(string workspaceDiagnosticMessage)
    {
        // MSBuildWorkspace wraps every design-time build error as
        // "Msbuild failed when processing the file '…' with message: <inner>". The wrapper's "msbuild"
        // would turn any inner "not found" into MsBuildNotFound, so classify the inner message only.
        var message = workspaceDiagnosticMessage;
        var wrapped = message.IndexOf(WrapperMarker, StringComparison.OrdinalIgnoreCase);
        var m = (wrapped >= 0 ? message[(wrapped + WrapperMarker.Length)..] : message).ToLowerInvariant();
        bool Has(params string[] any) => any.Any(m.Contains);

        if (Has("build host not found", "build host could not be found")
            || (m.Contains("the sdk '") && Has("not found", "could not be found"))
            || Has("msbuild could not be found", "msbuild was not found", "msbuild not found", "could not find msbuild",
                   "cannot find msbuild", "no msbuild", "msbuild.exe could not", "unable to locate msbuild"))
            return SemanticFailureKind.MsBuildNotFound;
        if (m.Contains("project.assets.json") || (m.Contains("assets file") && m.Contains("not found")))
            return SemanticFailureKind.NotRestored;
        // Unsupported project types and unresolved references both mean the project did not load usefully.
        if (Has("not supported", "unsupported", "cannot open project", "project file type"))
            return SemanticFailureKind.LoadFailed;
        if (m.Contains("reference") && Has("could not be resolved", "not found", "cannot resolve"))
            return SemanticFailureKind.LoadFailed;
        return null;
    }

    public static Task<SemanticFailure?> JudgeAsync(Solution s, IReadOnlyList<WorkspaceDiagnostic> diagnostics,
        IReadOnlyCollection<string> requested, CancellationToken ct) =>
        JudgeAsync(s, diagnostics, requested, new FileSystem(), ct);

    /// <param name="requested">Project files the caller asked to load; restore state is read from their <c>obj</c> folder.</param>
    public static async Task<SemanticFailure?> JudgeAsync(Solution s, IReadOnlyList<WorkspaceDiagnostic> diagnostics,
        IReadOnlyCollection<string> requested, IFileSystem fs, CancellationToken ct)
    {
        var kinds = diagnostics.Select(d => Classify(d.Message)).ToList();
        if (kinds.Contains(SemanticFailureKind.MsBuildNotFound))
            return new SemanticFailure(SemanticFailureKind.MsBuildNotFound);

        if (kinds.Contains(SemanticFailureKind.NotRestored) || requested.Any(p => IsUnrestoredSdkProject(fs, p)))
            return new SemanticFailure(SemanticFailureKind.NotRestored);

        var csharp = s.Projects.Where(p => p.Language == LanguageNames.CSharp).ToList();
        var opened = csharp.Where(p => p.FilePath is not null)
            .Select(p => fs.Path.GetFullPath(p.FilePath!)).ToHashSet(PathComparers.ForOs);
        if (!requested.All(p => opened.Contains(fs.Path.GetFullPath(p)))
            || diagnostics.Any(d => d.Kind == WorkspaceDiagnosticKind.Failure))
            return new SemanticFailure(SemanticFailureKind.LoadFailed);

        var rate = await CallResolutionRateAsync(csharp, ct);
        if (rate < MinCallRate)
            return new SemanticFailure(SemanticFailureKind.LowResolution, Rate: rate);

        var errors = 0;
        foreach (var project in csharp)
        {
            var compilation = await project.GetCompilationAsync(ct);
            errors += compilation?.GetDiagnostics(ct).Count(d => d.Severity == DiagnosticSeverity.Error) ?? 0;
        }

        return errors > 0 ? new SemanticFailure(SemanticFailureKind.CompileErrors, Count: errors) : null;
    }

    /// <summary>An SDK-style project (Sdk attribute on the root) with no obj/project.assets.json.</summary>
    internal static bool IsUnrestoredSdkProject(IFileSystem fs, string projectPath)
    {
        if (!fs.File.Exists(projectPath)) return false;
        try
        {
            if (XDocument.Parse(fs.File.ReadAllText(projectPath)).Root?.Attribute("Sdk") is null) return false;
        }
        catch (XmlException)
        {
            return false;
        }

        var dir = fs.Path.GetDirectoryName(fs.Path.GetFullPath(projectPath))!;
        return !fs.File.Exists(fs.Path.Combine(dir, "obj", "project.assets.json"));
    }

    /// <summary>Share of invocations bound to a symbol; 1.0 when there are none.</summary>
    private static async Task<double> CallResolutionRateAsync(IEnumerable<Project> projects, CancellationToken ct)
    {
        int total = 0, resolved = 0;
        foreach (var project in projects)
        foreach (var doc in project.Documents)
        {
            var root = await doc.GetSyntaxRootAsync(ct);
            var model = await doc.GetSemanticModelAsync(ct);
            if (root is null || model is null) continue;
            foreach (var call in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                var info = model.GetSymbolInfo(call, ct);
                if (IsExcluded(call, info, model, ct)) continue;
                total++;
                if (info.Symbol is not null) resolved++;
            }
        }

        return total == 0 ? 1.0 : (double)resolved / total;
    }

    /// <summary>nameof, late-bound and dynamic calls never bind to a symbol, so they leave the denominator (S8).</summary>
    private static bool IsExcluded(InvocationExpressionSyntax call, SymbolInfo info, SemanticModel model, CancellationToken ct)
    {
        if (info.Symbol is null && call.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" })
            return true;
        if (info.CandidateReason == CandidateReason.LateBound)
            return true;
        return call.Expression is MemberAccessExpressionSyntax ma
            && model.GetTypeInfo(ma.Expression, ct).Type?.TypeKind == TypeKind.Dynamic;
    }
}
