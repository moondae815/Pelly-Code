using System.IO.Abstractions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using PellyCoder.Core.Files;

namespace PellyCoder.Core.Impact;

/// <summary>
/// The outcome of a semantic load: a healthy <see cref="Solution"/>, or the <see cref="Failure"/> that makes it
/// unusable. Disposing releases the workspace (and its build host process) behind the solution.
/// </summary>
public sealed class SemanticLoad(Solution? solution, SemanticFailure? failure, IDisposable? owner) : IDisposable
{
    public Solution? Solution { get; } = solution;
    public SemanticFailure? Failure { get; } = failure;

    public void Dispose() => owner?.Dispose();
}

public interface ISemanticLoader
{
    /// <summary>Loads the given project files. Never throws for load problems; caller cancellation throws <see cref="OperationCanceledException"/>.</summary>
    Task<SemanticLoad> LoadAsync(IReadOnlyList<string> projects, CancellationToken ct);
}

/// <summary>
/// Opens projects with <see cref="MSBuildWorkspace"/> and judges them with <see cref="SemanticHealth"/>.
/// No MSBuildLocator: the build host shipped next to the app finds MSBuild by itself (S8).
/// </summary>
public sealed class MsBuildSemanticLoader(IFileSystem fs, TimeSpan? timeout = null) : ISemanticLoader
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly TimeSpan _timeout = timeout ?? DefaultTimeout;

    public async Task<SemanticLoad> LoadAsync(IReadOnlyList<string> projects, CancellationToken ct)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_timeout);
        MSBuildWorkspace? workspace = null;
        try
        {
            workspace = MSBuildWorkspace.Create();
            var diagnostics = new List<WorkspaceDiagnostic>();
            using var _ = workspace.RegisterWorkspaceFailedHandler(e => { lock (diagnostics) diagnostics.Add(e.Diagnostic); });

            foreach (var project in projects)
            {
                var full = fs.Path.GetFullPath(project);
                // A ProjectReference opened earlier is already in the solution; opening it again throws.
                if (workspace.CurrentSolution.Projects.Any(p => p.FilePath is not null
                        && PathComparers.ForOs.Equals(fs.Path.GetFullPath(p.FilePath), full)))
                    continue;
                await workspace.OpenProjectAsync(full, cancellationToken: limit.Token);
            }

            var solution = workspace.CurrentSolution;
            foreach (var p in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
                await p.GetCompilationAsync(limit.Token);

            List<WorkspaceDiagnostic> all;
            lock (diagnostics) all = [.. workspace.Diagnostics.Concat(diagnostics).DistinctBy(d => (d.Kind, d.Message))];
            var failure = await SemanticHealth.JudgeAsync(solution, all, projects, fs, limit.Token);
            if (failure is not null) return Fail(failure, workspace);
            return new SemanticLoad(solution, null, workspace);
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            // Once the time limit fired, any exception (the build host may surface it as a broken connection
            // rather than a cancellation) is the timeout; otherwise it is a load failure.
            return Fail(new SemanticFailure(limit.IsCancellationRequested ? SemanticFailureKind.Timeout : SemanticFailureKind.LoadFailed), workspace);
        }
        catch
        {
            workspace?.Dispose();
            ct.ThrowIfCancellationRequested();
            throw;
        }
    }

    private static SemanticLoad Fail(SemanticFailure failure, MSBuildWorkspace? workspace)
    {
        workspace?.Dispose();
        return new SemanticLoad(null, failure, null);
    }
}
