using PellyCoder.Core.CharTests;
using PellyCoder.Core.Config;
using PellyCoder.Core.Doctor;
using PellyCoder.Core.Fsm;

namespace PellyCoder.Cli;

/// <summary>Runs one <c>pelly cs</c> or <c>pelly sql</c> request. Options are passed per call because CLI flags override them.</summary>
public interface IRunner
{
    /// <summary>Runs the request and reports how it left the target file (used by <c>pelly apply</c>).</summary>
    Task<RunResult> RunDetailedAsync(RunRequest request, PellyOptions options, CancellationToken ct);

    /// <summary>The exit code of <see cref="RunDetailedAsync"/>.</summary>
    async Task<int> RunAsync(RunRequest request, PellyOptions options, CancellationToken ct) =>
        (await RunDetailedAsync(request, options, ct)).ExitCode;
}

/// <summary>Runs one <c>pelly test</c> request. Options are passed per call because CLI flags override them.</summary>
public interface ICharTestRunner
{
    Task<int> RunAsync(CharTestRequest request, PellyOptions options, CancellationToken ct);
}

/// <summary>Runs the <c>pelly doctor</c> checks for the given options.</summary>
public interface IDoctorRunner
{
    Task<IReadOnlyList<DoctorCheck>> RunAsync(PellyOptions options, DoctorContext context, CancellationToken ct);
}

/// <summary>Where configuration values come from besides the workspace file: the user config path and the environment.</summary>
public sealed record ConfigSources(string? UserConfigPath, IReadOnlyDictionary<string, string?> Environment)
{
    public static ConfigSources FromProcess()
    {
        var env = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (System.Collections.DictionaryEntry e in System.Environment.GetEnvironmentVariables())
            env[(string)e.Key] = e.Value as string;
        return new ConfigSources(PellyPaths.UserConfigPath(), env);
    }
}
