using System.Globalization;
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
public sealed class MsBuildSemanticLoader : ISemanticLoader
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(120);

    private readonly IFileSystem _fs;
    private readonly TimeSpan _timeout;
    private readonly Func<MSBuildWorkspace> _createWorkspace;

    public MsBuildSemanticLoader(IFileSystem fs, TimeSpan? timeout = null)
        : this(fs, timeout, () => MSBuildWorkspace.Create())
    {
    }

    /// <param name="createWorkspace">Test seam: stands in for <see cref="MSBuildWorkspace.Create()"/>.</param>
    internal MsBuildSemanticLoader(IFileSystem fs, TimeSpan? timeout, Func<MSBuildWorkspace> createWorkspace)
    {
        _fs = fs;
        _timeout = timeout ?? DefaultTimeout;
        _createWorkspace = createWorkspace;
    }

    public async Task<SemanticLoad> LoadAsync(IReadOnlyList<string> projects, CancellationToken ct)
    {
        // Pure filesystem check first: an unrestored SDK project would otherwise spend the whole time limit in
        // MSBuild and come back as Timeout instead of the actionable NotRestored. JudgeAsync still checks again
        // for anything the build reports.
        if (projects.Any(p => SemanticHealth.IsUnrestoredSdkProject(_fs, p)))
            return new SemanticLoad(null, new SemanticFailure(SemanticFailureKind.NotRestored), null);

        using var english = EnglishBuildMessages.Pin();
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(_timeout);
        MSBuildWorkspace? workspace = null;
        try
        {
            workspace = _createWorkspace();
            var diagnostics = new List<WorkspaceDiagnostic>();
            using var _ = workspace.RegisterWorkspaceFailedHandler(e => { lock (diagnostics) diagnostics.Add(e.Diagnostic); });

            foreach (var project in projects)
            {
                var full = _fs.Path.GetFullPath(project);
                // A ProjectReference opened earlier is already in the solution; opening it again throws.
                if (workspace.CurrentSolution.Projects.Any(p => p.FilePath is not null
                        && PathComparers.ForOs.Equals(_fs.Path.GetFullPath(p.FilePath), full)))
                    continue;
                await workspace.OpenProjectAsync(full, cancellationToken: limit.Token);
            }

            var solution = workspace.CurrentSolution;
            foreach (var p in solution.Projects.Where(p => p.Language == LanguageNames.CSharp))
                await p.GetCompilationAsync(limit.Token);

            List<WorkspaceDiagnostic> all;
            lock (diagnostics) all = [.. workspace.Diagnostics.Concat(diagnostics).DistinctBy(d => (d.Kind, d.Message))];
            var failure = await SemanticHealth.JudgeAsync(solution, all, projects, _fs, limit.Token);
            if (failure is not null) return Fail(failure, workspace);
            return new SemanticLoad(solution, null, workspace);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // Once the time limit fired, any exception (the build host may surface it as a broken connection
            // rather than a cancellation) is the timeout. A missing build host throws instead of reporting a
            // diagnostic (S8 ExitCodeFor), so the message is classified like one; anything else is a load failure.
            var kind = limit.IsCancellationRequested ? SemanticFailureKind.Timeout
                : IsBuildToolMissing(ex) ? SemanticFailureKind.MsBuildNotFound
                : SemanticFailureKind.LoadFailed;
            return Fail(new SemanticFailure(kind), workspace);
        }
        catch
        {
            workspace?.Dispose();
            ct.ThrowIfCancellationRequested();
            throw;
        }
    }

    /// <summary>True when the exception or any exception inside it says the build host or MSBuild is missing.</summary>
    private static bool IsBuildToolMissing(Exception ex) => ex switch
    {
        AggregateException a => a.InnerExceptions.Any(IsBuildToolMissing),
        _ => SemanticHealth.Classify(ex.Message) == SemanticFailureKind.MsBuildNotFound
            || (ex.InnerException is { } inner && IsBuildToolMissing(inner)),
    };

    private static SemanticLoad Fail(SemanticFailure failure, MSBuildWorkspace? workspace)
    {
        workspace?.Dispose();
        return new SemanticLoad(null, failure, null);
    }
}

/// <summary>
/// Pins Roslyn's, the build host's and MSBuild's messages to English for the length of a semantic load (S8
/// <c>ForceEnglishMessages</c>): <see cref="SemanticHealth.Classify"/> matches English words, and on a Korean PC they
/// are localized. The UI culture is set for this async flow only; the environment variables reach the build host
/// process started during the load and are put back afterwards, so processes pelly starts later keep the user's
/// language. Overlapping loads share one pin; the last to leave restores.
/// </summary>
internal static class EnglishBuildMessages
{
    private static readonly string[] Variables = ["VSLANG", "DOTNET_CLI_UI_LANGUAGE"];
    private static readonly string[] English = ["1033", "en"];
    private static readonly object Gate = new();
    private static int _holders;
    private static string?[] _saved = [];

    public static IDisposable Pin()
    {
        var culture = CultureInfo.CurrentUICulture;
        CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;
        lock (Gate)
        {
            if (_holders++ == 0)
            {
                _saved = [.. Variables.Select(Environment.GetEnvironmentVariable)];
                for (var i = 0; i < Variables.Length; i++) Environment.SetEnvironmentVariable(Variables[i], English[i]);
            }
        }
        return new Release(culture);
    }

    private sealed class Release(CultureInfo culture) : IDisposable
    {
        private int _released;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 1) return;
            CultureInfo.CurrentUICulture = culture;
            lock (Gate)
            {
                if (--_holders == 0)
                    for (var i = 0; i < Variables.Length; i++) Environment.SetEnvironmentVariable(Variables[i], _saved[i]);
            }
        }
    }
}
