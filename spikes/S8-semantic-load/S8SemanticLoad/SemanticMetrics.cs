using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace S8SemanticLoad;

public sealed record CallResolution(int Total, int Resolved, int Ambiguous, int Unresolved)
{
    public double Rate => Total == 0 ? 1.0 : (double)Resolved / Total;
}

public sealed record ProjectHealth(string Name, ProjectKind Kind, bool Restored, int CompileErrors, int MissingMetadataRefs);

/// <summary>
/// Whether a loaded solution actually carries type information. A half-failed load opens without
/// exceptions but leaves calls unbound, so the call-resolution rate is the main signal.
/// </summary>
public static class SemanticMetrics
{
    public static async Task<CallResolution> MeasureCallsAsync(Solution s, Func<Project, bool>? include = null, CancellationToken ct = default)
    {
        int total = 0, resolved = 0, ambiguous = 0;
        foreach (var project in s.Projects.Where(p => p.Language == LanguageNames.CSharp && (include?.Invoke(p) ?? true)))
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
                else if (info.CandidateSymbols.Length > 0) ambiguous++;
            }
        }
        return new CallResolution(total, resolved, ambiguous, total - resolved - ambiguous);
    }

    private static bool IsExcluded(InvocationExpressionSyntax call, SymbolInfo info, SemanticModel model, CancellationToken ct)
    {
        if (info.Symbol is null && call.Expression is IdentifierNameSyntax { Identifier.ValueText: "nameof" })
            return true;
        if (info.CandidateReason == CandidateReason.LateBound)
            return true;
        return call.Expression is MemberAccessExpressionSyntax ma
            && model.GetTypeInfo(ma.Expression, ct).Type?.TypeKind == TypeKind.Dynamic;
    }

    public static async Task<IReadOnlyList<ProjectHealth>> MeasureHealthAsync(Solution s, ProjectCensus census, CancellationToken ct = default)
    {
        var list = new List<ProjectHealth>();
        foreach (var project in s.Projects.Where(p => p.Language == LanguageNames.CSharp))
        {
            var entry = census.Entries.FirstOrDefault(e => project.FilePath is not null
                && string.Equals(Path.GetFullPath(e.Path), Path.GetFullPath(project.FilePath), StringComparison.OrdinalIgnoreCase));
            var compilation = await project.GetCompilationAsync(ct);
            var errors = compilation?.GetDiagnostics(ct).Count(d => d.Severity == DiagnosticSeverity.Error) ?? 0;
            var missing = project.MetadataReferences.OfType<PortableExecutableReference>()
                .Count(r => r.FilePath is not null && !File.Exists(r.FilePath));
            list.Add(new ProjectHealth(project.Name, entry?.Kind ?? ProjectKind.SdkCSharp, entry?.Restored ?? true, errors, missing));
        }
        return list;
    }
}
