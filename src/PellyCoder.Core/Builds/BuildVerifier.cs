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

    private static readonly IReadOnlyDictionary<string, string> EnglishEnv = new Dictionary<string, string>
    {
        ["DOTNET_CLI_UI_LANGUAGE"] = "en",
        ["VSLANG"] = "1033",
    };

    public async Task<BuildRun> BuildAsync(BuildTool tool, string csprojPath, string workspaceRoot, CancellationToken ct)
    {
        var spec = new ProcessSpec(
            tool.Executable,
            ArgumentsFor(tool, csprojPath),
            Path.GetDirectoryName(csprojPath) ?? ".",
            TimeSpan.FromSeconds(options.TimeoutSeconds),
            EnglishEnv);

        var result = await processes.RunAsync(spec, ct);
        if (result.TimedOut)
            return new BuildRun([], [], result.ExitCode, true, result.Duration);

        var output = DecodeStdOut(result) + "\n" + result.StdErr;
        var all = BuildErrorParser.Parse(output, workspaceRoot, OperatingSystem.IsWindows());
        var errors = all.Where(d => d.Severity == "error").ToList();
        var warnings = all.Where(d => d.Severity == "warning").ToList();

        // A failed build whose output we could not parse must not look like a pass.
        if (errors.Count == 0 && result.ExitCode != 0)
            errors.Add(new Diagnostic("", 0, 0, "error", "BUILD", $"build failed with exit code {result.ExitCode}"));

        return new BuildRun(errors, warnings, result.ExitCode, false, result.Duration);
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

    // Legacy toolchains on Korean Windows may emit CP949 even with VSLANG=1033 for file paths / messages.
    private static string DecodeStdOut(ProcessResult r)
    {
        if (!r.StdOut.Contains('�')) return r.StdOut;
        var cp949 = Cp949.GetString(r.StdOutBytes);
        return cp949.Contains('�') ? r.StdOut : cp949;
    }
}
