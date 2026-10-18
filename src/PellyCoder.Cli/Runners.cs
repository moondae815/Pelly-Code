using PellyCoder.Core.Config;
using PellyCoder.Core.Doctor;
using PellyCoder.Core.Fsm;

namespace PellyCoder.Cli;

/// <summary>Runs one <c>pelly cs</c> request. Options are passed per call because CLI flags override them.</summary>
public interface IRunner
{
    Task<int> RunAsync(RunRequest request, PellyOptions options, CancellationToken ct);
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
