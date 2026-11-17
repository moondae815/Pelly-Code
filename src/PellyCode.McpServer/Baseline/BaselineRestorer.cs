using System.IO.Abstractions;
using System.Xml;
using PellyCode.Core.Abstractions;
using PellyCode.Core.Builds;
using PellyCode.Core.Config;

namespace PellyCode.McpServer.Baseline;

/// <summary>Restores one project in the HEAD mirror before its baseline build (the mirror never has obj/).</summary>
public interface IBaselineRestorer
{
    /// <summary>Null when the project is ready to build with <c>--no-restore</c>; otherwise why its restore failed.</summary>
    Task<string?> RestoreAsync(string project, CancellationToken ct);
}

/// <summary>
/// <c>dotnet restore &lt;project&gt;</c> for an SDK-style project (its project references are restored with it). A legacy
/// project needs nothing: its packages/ folder is copied into the mirror. An unreadable project is left to the build to report.
/// </summary>
public sealed class DotnetRestorer(IFileSystem fs, IProcessRunner processes, BuildOptions options) : IBaselineRestorer
{
    private const int TailLines = 5;

    /// <summary>The same child environment as Core's builds: English output, telemetry and first-run notices off (deploy/no-network.md §4).</summary>
    private static readonly IReadOnlyDictionary<string, string> Env = new Dictionary<string, string>
    {
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        ["VSLANG"] = "1033",
        ["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1",
        ["TESTINGPLATFORM_TELEMETRY_OPTOUT"] = "1",
        ["DOTNET_NOLOGO"] = "1",
        ["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "1",
    };

    public async Task<string?> RestoreAsync(string project, CancellationToken ct)
    {
        try
        {
            if (ProjectStyleDetector.Detect(fs, project) != ProjectStyle.Sdk) return null;
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return null;
        }

        var spec = new ProcessSpec("dotnet", ["restore", project, "--nologo", "-v", "q"],
            fs.Path.GetDirectoryName(project) ?? ".", TimeSpan.FromSeconds(options.TimeoutSeconds), Env);
        ProcessResult result;
        try
        {
            result = await processes.RunAsync(spec, ct);
        }
        catch (ProcessStartException ex)
        {
            return ex.Message;
        }
        if (result.TimedOut) return $"복원이 {options.TimeoutSeconds}초 안에 끝나지 않았습니다 (build.timeoutSeconds)";
        if (result.ExitCode == 0) return null;

        var tail = (result.StdOut + "\n" + result.StdErr).Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0)
            .TakeLast(TailLines);
        return $"복원 실패 (종료 코드 {result.ExitCode}): {string.Join(" | ", tail)}";
    }
}
