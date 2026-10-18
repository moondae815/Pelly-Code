using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Builds;

/// <summary>Runs dotnet build / MSBuild on one project and parses the result (design §8.9).</summary>
public sealed class BuildVerifier(IProcessRunner processes, BuildOptions options)
{
    private static readonly Encoding Cp949;

    static BuildVerifier()
    {
        // Core tests do not go through the CLI startup, so register the code pages here (idempotent).
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Cp949 = Encoding.GetEncoding(949);
    }

    internal static readonly IReadOnlyDictionary<string, string> EnglishEnv = new Dictionary<string, string>
    {
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        ["VSLANG"] = "1033",
    };

    public Task<BuildRun> BuildAsync(BuildTool tool, string csprojPath, string workspaceRoot, CancellationToken ct) =>
        BuildAsync(tool, csprojPath, workspaceRoot, null, ct);

    /// <param name="properties">Global MSBuild properties (<c>-p:Name=Value</c>), e.g. the judge project's <c>JudgeTargetRoot</c>.</param>
    public async Task<BuildRun> BuildAsync(BuildTool tool, string csprojPath, string workspaceRoot,
        IReadOnlyDictionary<string, string>? properties, CancellationToken ct)
    {
        var spec = new ProcessSpec(
            tool.Executable,
            [.. ArgumentsFor(tool, csprojPath), .. PropertyArguments(tool, properties)],
            Path.GetDirectoryName(csprojPath) ?? ".",
            TimeSpan.FromSeconds(options.TimeoutSeconds),
            EnglishEnv);

        var result = await processes.RunAsync(spec, ct);
        if (result.TimedOut)
            return new BuildRun([], [], result.ExitCode, true, result.Duration);

        var output = DecodeStdOut(result) + "\n" + result.StdErr;
        var all = BuildErrorParser.Parse(output, [workspaceRoot, PhysicalPath(workspaceRoot)], OperatingSystem.IsWindows());
        var errors = all.Where(d => d.Severity == "error").ToList();
        var warnings = all.Where(d => d.Severity == "warning").ToList();

        // A failed build whose output we could not parse must not look like a pass.
        if (errors.Count == 0 && result.ExitCode != 0)
            errors.Add(new Diagnostic("", 0, 0, "error", "BUILD", $"build failed with exit code {result.ExitCode}"));

        return new BuildRun(errors, warnings, result.ExitCode, false, result.Duration);
    }

    /// <summary>
    /// <paramref name="path"/> with every symbolic link (or junction) along it resolved, because the compiler prints real
    /// paths (macOS <c>/var</c> → <c>/private/var</c>). Segments that do not exist are kept as given; any failure returns
    /// <paramref name="path"/> unchanged. Windows 미확인(실기): junction 경로와 subst 드라이브(링크가 아니라 해석하지 않음).
    /// </summary>
    internal static string PhysicalPath(string path)
    {
        try
        {
            return Resolve(Path.GetFullPath(path), depth: 0);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return path;
        }
    }

    private static string Resolve(string full, int depth)
    {
        const int MaxLinkHops = 32;
        var root = Path.GetPathRoot(full) ?? "";
        var segments = full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        var current = root;
        for (var i = 0; i < segments.Length; i++)
        {
            var next = Path.Combine(current, segments[i]);
            var info = new DirectoryInfo(next);
            if (!info.Exists) return Path.Combine([next, .. segments[(i + 1)..]]);
            if (info.LinkTarget is { } target && depth < MaxLinkHops)
            {
                // The target may itself sit under links, so resolve it again from its root.
                var absolute = Path.GetFullPath(target, current);
                return Resolve(Path.Combine([absolute, .. segments[(i + 1)..]]), depth + 1);
            }
            current = next;
        }
        return current;
    }

    private IReadOnlyList<string> ArgumentsFor(BuildTool tool, string csproj) => tool.Kind switch
    {
        BuildToolKind.Dotnet =>
        [
            "build", csproj, "--no-restore", "--nologo", "-v", "q",
            options.WarnAsError ? "-clp:NoSummary" : "-clp:NoSummary;ErrorsOnly",
        ],
        // Windows 미확인(실기): MSBuild 인자는 CI windows-integration 테스트로만 확인한다.
        _ => [csproj, "/t:Build", "/nologo", "/v:q", "/clp:NoSummary;ErrorsOnly", "/nr:true", "/m"],
    };

    private static IEnumerable<string> PropertyArguments(BuildTool tool, IReadOnlyDictionary<string, string>? properties) =>
        (properties ?? new Dictionary<string, string>())
            .Select(p => (tool.Kind == BuildToolKind.Dotnet ? "-p:" : "/p:") + p.Key + "=" + p.Value);

    // Legacy toolchains on Korean Windows may emit CP949 even with VSLANG=1033 for file paths / messages.
    private static string DecodeStdOut(ProcessResult r)
    {
        if (!r.StdOut.Contains('\uFFFD')) return r.StdOut;
        var cp949 = Cp949.GetString(r.StdOutBytes);
        return cp949.Contains('\uFFFD') ? r.StdOut : cp949;
    }
}
