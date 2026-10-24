using System.Diagnostics;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;

namespace S8SemanticLoad;

public sealed record LoadOptions(bool Detail, bool CheckA3, string ToolForm, string? CorpusRoot = null, string? CasesDir = null);

/// <summary>
/// Opens a solution or project with MSBuildWorkspace once and measures it. The build host finds MSBuild by
/// itself, so "finding MSBuild" is inside OpenSeconds.
/// </summary>
public static class LoadRunner
{
    public static async Task<LoadReport> RunOnceAsync(string path, LoadOptions o, CancellationToken ct)
    {
        var total = Stopwatch.StartNew();
        var census = ProjectCensusReader.Read(path);
        using var workspace = MSBuildWorkspace.Create();
        var events = new List<WorkspaceDiagnostic>();
        using var _ = workspace.RegisterWorkspaceFailedHandler(e => { lock (events) events.Add(e.Diagnostic); });

        var sw = Stopwatch.StartNew();
        Solution solution = path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase)
            ? (await workspace.OpenProjectAsync(path, cancellationToken: ct)).Solution
            : await workspace.OpenSolutionAsync(path, cancellationToken: ct);
        var openSeconds = sw.Elapsed.TotalSeconds;

        var csharp = solution.Projects.Where(p => p.Language == LanguageNames.CSharp).ToList();
        sw.Restart();
        foreach (var p in csharp) await p.GetCompilationAsync(ct);
        var compileSeconds = sw.Elapsed.TotalSeconds;

        var diagnostics = workspace.Diagnostics.Concat(events)
            .Where(d => d.Kind == WorkspaceDiagnosticKind.Failure || d.Kind == WorkspaceDiagnosticKind.Warning)
            .DistinctBy(d => (d.Kind, d.Message)).ToList();
        var nonCsNames = census.Entries.Where(e => e.Kind == ProjectKind.NonCSharp).Select(e => Path.GetFileName(e.Path)).ToList();
        var issues = diagnostics
            .Select(d => (Kind: DiagnosticClassifier.Classify(d.Message), Cs: !nonCsNames.Any(n => d.Message.Contains(n, StringComparison.OrdinalIgnoreCase))))
            .GroupBy(x => x).Select(g => new IssueCount(g.Key.Kind, g.Key.Cs, g.Count())).ToList();

        var restored = census.Entries.Where(e => e.Restored).Select(e => Path.GetFullPath(e.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        bool IsRestored(Project p) => p.FilePath is null || census.Entries.Count == 0
            || restored.Contains(Path.GetFullPath(p.FilePath))
            || !census.Entries.Any(e => string.Equals(Path.GetFullPath(e.Path), Path.GetFullPath(p.FilePath), StringComparison.OrdinalIgnoreCase));

        var calls = await SemanticMetrics.MeasureCallsAsync(solution, IsRestored, ct);
        var health = await SemanticMetrics.MeasureHealthAsync(solution, census, ct);
        var totals = new HealthTotals(
            health.Where(h => h.Restored).Sum(h => h.CompileErrors),
            health.Where(h => !h.Restored).Sum(h => h.CompileErrors),
            health.Sum(h => h.MissingMetadataRefs));
        var probe = await ReferenceProbe.RunAsync(solution, ct: ct);
        A3Result? a3 = o.CheckA3
            ? await CorpusCheck.CheckA3Async(solution, o.CorpusRoot ?? throw new ArgumentException("corpus root needed"), o.CasesDir ?? throw new ArgumentException("cases dir needed"), ct)
            : null;

        var counts = new CensusCounts(census.SdkCs, census.LegacyCs, census.NonCs, census.UnrestoredSdk, csharp.Count);
        return new LoadReport(
            o.Detail ? "detail" : "field",
            OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux",
            o.ToolForm, RoslynVersion(), await MsBuildGuessAsync(ct),
            counts, issues, openSeconds, compileSeconds, total.Elapsed.TotalSeconds,
            PeakMemoryBytes(), calls, totals, probe, a3,
            o.Detail ? diagnostics.Select(d => $"{d.Kind}: {d.Message}").ToList() : null,
            o.Detail ? health : null);
    }

    /// <summary>This process only — the build host runs separately. macOS reports no peak, so fall back to the current working set.</summary>
    public static long PeakMemoryBytes()
    {
        using var p = Process.GetCurrentProcess();
        return p.PeakWorkingSet64 > 0 ? p.PeakWorkingSet64 : p.WorkingSet64;
    }

    private static string RoslynVersion() =>
        typeof(MSBuildWorkspace).Assembly.GetName().Version?.ToString() ?? "unknown";

    /// <summary>Best effort: the build host does not say which MSBuild it used, so report the newest Visual Studio vswhere sees.</summary>
    private static async Task<string?> MsBuildGuessAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsWindows()) return null;
        var vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(vswhere, ["-latest", "-property", "installationVersion"])
                { RedirectStandardOutput = true, UseShellExecute = false })!;
            var text = (await p.StandardOutput.ReadToEndAsync(ct)).Trim();
            await p.WaitForExitAsync(ct);
            return text.Length == 0 ? null : text;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }
}
