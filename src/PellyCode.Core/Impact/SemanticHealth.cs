using System.IO.Abstractions;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using PellyCode.Core.Files;

namespace PellyCode.Core.Impact;

/// <summary>
/// Whether a loaded solution can be trusted for symbol search, by the criteria fixed in the S8 spike:
/// build tooling present, SDK projects restored, every requested project opened without a failure,
/// call resolution rate at least <see cref="MinCallRate"/> and no compile errors. A half-failed load opens
/// without exceptions but leaves calls unbound, so the rate is the main signal.
/// </summary>
public static partial class SemanticHealth
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
        // A NuGet warning (e.g. an audit NU1900-NU1905 a closed network always gets) arrives as a Failure but loads fine.
        HashSet<string>? assetWarnings = null;
        if (!requested.All(p => opened.Contains(fs.Path.GetFullPath(p)))
            || diagnostics.Any(d => d.Kind == WorkspaceDiagnosticKind.Failure
                && !IsNuGetWarning(d.Message, assetWarnings ??= NuGetWarningMessages(fs, requested))))
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

    /// <summary>
    /// True when a Failure diagnostic is only a NuGet warning (audit NU1900–NU1905, NU1701…), which leaves the project
    /// loaded. MSBuildWorkspace reports them as failures in two forms: with the code (<c>warning NU1903: …</c>), or as
    /// the bare message the restore wrote to project.assets.json (in the restore's language, without its code), which
    /// <paramref name="assetWarnings"/> holds. NuGet errors (<c>error NU1101</c>, Error-level logs) are not warnings.
    /// </summary>
    internal static bool IsNuGetWarning(string message, IReadOnlySet<string> assetWarnings)
    {
        if (NuGetWarningCode().IsMatch(message)) return true;
        var wrapped = message.IndexOf(WrapperMarker, StringComparison.OrdinalIgnoreCase);
        var inner = (wrapped >= 0 ? message[(wrapped + WrapperMarker.Length)..] : message).Trim();
        return assetWarnings.Contains(inner);
    }

    [GeneratedRegex(@"\bwarning NU\d{4}\b", RegexOptions.IgnoreCase)]
    private static partial Regex NuGetWarningCode();

    /// <summary>The Warning-level log messages in the requested projects' project.assets.json files.</summary>
    private static HashSet<string> NuGetWarningMessages(IFileSystem fs, IReadOnlyCollection<string> requested)
    {
        var messages = new HashSet<string>(StringComparer.Ordinal);
        foreach (var project in requested)
        {
            if (!fs.File.Exists(project)) continue;
            var full = fs.Path.GetFullPath(project);
            XDocument doc;
            try { doc = XDocument.Parse(fs.File.ReadAllText(full)); }
            catch (XmlException) { continue; }
            foreach (var obj in ObjFolders(fs, full, fs.Path.GetDirectoryName(full)!, doc))
            {
                var assets = fs.Path.Combine(obj, "project.assets.json");
                if (!fs.File.Exists(assets)) continue;
                try
                {
                    using var json = JsonDocument.Parse(fs.File.ReadAllText(assets));
                    if (!json.RootElement.TryGetProperty("logs", out var logs) || logs.ValueKind != JsonValueKind.Array) continue;
                    foreach (var log in logs.EnumerateArray())
                        if (log.TryGetProperty("level", out var level) && string.Equals(level.GetString(), "Warning", StringComparison.OrdinalIgnoreCase)
                            && log.TryGetProperty("message", out var text) && text.GetString() is { } m)
                            messages.Add(m.Trim());
                }
                catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException) { }
            }
        }
        return messages;
    }

    /// <summary>An SDK-style project (Sdk attribute on the root) with no project.assets.json in its obj folder.</summary>
    internal static bool IsUnrestoredSdkProject(IFileSystem fs, string projectPath)
    {
        if (!fs.File.Exists(projectPath)) return false;
        XDocument project;
        try
        {
            project = XDocument.Parse(fs.File.ReadAllText(projectPath));
            if (project.Root?.Attribute("Sdk") is null) return false;
        }
        catch (XmlException)
        {
            return false;
        }

        var full = fs.Path.GetFullPath(projectPath);
        var dir = fs.Path.GetDirectoryName(full)!;
        return !ObjFolders(fs, full, dir, project).Any(o => fs.File.Exists(fs.Path.Combine(o, "project.assets.json")));
    }

    /// <summary>
    /// Where restore may have written the assets file: the default <c>obj</c>, plus <c>BaseIntermediateOutputPath</c> and
    /// <c>UseArtifactsOutput</c> (<c>&lt;ArtifactsPath&gt;/obj/&lt;project&gt;</c>) read from the project and the nearest
    /// Directory.Build.props. A setting that cannot be read leaves only the default.
    /// </summary>
    private static IEnumerable<string> ObjFolders(IFileSystem fs, string projectPath, string dir, XDocument project)
    {
        yield return fs.Path.Combine(dir, "obj");

        // Evaluation order: the props first, the project last (the last definition wins).
        var sources = new List<(XDocument Doc, string Dir)> { (project, dir) };
        string? propsDir = null;
        for (var d = dir; d is not null; d = fs.Path.GetDirectoryName(d))
        {
            var props = fs.Path.Combine(d, "Directory.Build.props");
            if (!fs.File.Exists(props)) continue;
            try { sources.Insert(0, (XDocument.Parse(fs.File.ReadAllText(props)), d)); propsDir = d; }
            catch (XmlException) { }
            break;
        }

        string? Value(string name) => sources.SelectMany(s => s.Doc.Root!.Elements().Where(g => g.Name.LocalName == "PropertyGroup" && g.Attribute("Condition") is null)
                .SelectMany(g => g.Elements().Where(e => e.Name.LocalName == name && e.Attribute("Condition") is null)
                    .Select(e => (Text: e.Value.Trim(), s.Dir)))).Select(v => Expand(v.Text, v.Dir)).LastOrDefault(v => v.Length > 0);
        string Expand(string v, string home) => v
            .Replace("$(MSBuildThisFileDirectory)", home + fs.Path.DirectorySeparatorChar)
            .Replace("$(MSBuildProjectDirectory)", dir)
            .Replace("$(MSBuildProjectName)", fs.Path.GetFileNameWithoutExtension(projectPath));
        string Rooted(string v) => fs.Path.GetFullPath(fs.Path.IsPathRooted(v) ? v : fs.Path.Combine(dir, v));

        if (Value("BaseIntermediateOutputPath") is { } obj && !obj.Contains("$("))
            yield return Rooted(obj);

        if (string.Equals(Value("UseArtifactsOutput"), "true", StringComparison.OrdinalIgnoreCase))
        {
            var artifacts = Value("ArtifactsPath");
            if (artifacts is null)
            {
                // Default ArtifactsPath: an "artifacts" folder next to the Directory.Build.props.
                artifacts = propsDir is null ? null : fs.Path.Combine(propsDir, "artifacts");
            }

            if (artifacts is not null && !artifacts.Contains("$("))
                yield return fs.Path.Combine(Rooted(artifacts), "obj", fs.Path.GetFileNameWithoutExtension(projectPath));
        }
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
