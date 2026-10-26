using System.Text;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Testing;

/// <summary>
/// One test run: per-test results, or the build errors that kept the tests from running. <paramref name="ReportMissing"/>:
/// the test process ran and exited non-zero but wrote no TRX (an in-process host that was killed), as opposed to a process that never ran.
/// <paramref name="RunAborted"/>: the TRX run outcome says the run did not finish (Aborted, Error, …).
/// </summary>
public sealed record TestRun(
    IReadOnlyList<TestCaseResult> Results,
    IReadOnlyList<Diagnostic> BuildErrors,
    int ExitCode,
    bool TimedOut,
    bool PerTestResults,
    string? Error,
    bool ReportMissing = false,
    bool RunAborted = false,
    IReadOnlyList<string>? Skipped = null);

/// <summary>
/// Builds a test project and runs it: SDK projects through <c>dotnet test</c> with a TRX report, legacy projects
/// through the configured <c>test.command</c> (R7: without <c>{trx}</c> only the exit code is known).
/// </summary>
public sealed class TestRunner(IProcessRunner processes, BuildOptions build, TestOptions test)
{
    public const string LegacyCommandMissing = "레거시 테스트 프로젝트는 test.command 설정이 필요합니다";

    /// <summary>The test process ran but wrote no TRX; followed by the exit code.</summary>
    public const string ReportNotWritten = "테스트 결과 파일(TRX)이 만들어지지 않았습니다";

    /// <summary>A multi-target test project whose first target framework could not be launched; followed by <c>({tfm})</c>.</summary>
    public const string MultiTargetCannotRun = "멀티 타깃 테스트 프로젝트를 실행할 수 없습니다";

    /// <summary>
    /// Output of a launch that never reached the tests (measured on macOS with .NET 10 only; 「Windows 미확인」): the host found no runtime for the
    /// framework (exit 150), or <c>dotnet run</c> could not start the program (a .NET Framework target without mono).
    /// </summary>
    private static readonly string[] LaunchFailureSignals =
    [
        "You must install or update .NET to run this application",
        "An error occurred trying to start process",
    ];

    public Task<TestRun> RunAsync(BuildTool tool, string testProject, string workDir, string trxPath, CancellationToken ct) =>
        RunAsync(tool, testProject, workDir, trxPath, null, ct);

    /// <param name="properties">Global MSBuild properties for the build step only (the run itself is <c>--no-build</c>).</param>
    public async Task<TestRun> RunAsync(BuildTool tool, string testProject, string workDir, string trxPath,
        IReadOnlyDictionary<string, string>? properties, CancellationToken ct)
    {
        var legacy = tool.Kind == BuildToolKind.MsBuild;
        // 「Windows 미확인」: 레거시 경로(MSBuild 빌드와 test.command 실행)는 실기 Windows에서 재지 못했다.
        if (legacy && string.IsNullOrWhiteSpace(test.Command))
            return Failed(LegacyCommandMissing);

        if (legacy && (SplitCommand(test.Command!).FirstOrDefault() ?? "").Trim().Length == 0)
            return Failed("test.command에 실행 파일이 없습니다");

        BuildRun built;
        try
        {
            built = await new BuildVerifier(processes, build).BuildAsync(tool, testProject, workDir, properties, ct);
        }
        catch (ProcessStartException ex)
        {
            return Failed(ex.Message);
        }
        if (built.TimedOut) return new TestRun([], [], built.ExitCode, true, false, null);
        if (built.Errors.Count > 0) return new TestRun([], built.Errors, built.ExitCode, false, false, null);

        if (!DeleteStale(trxPath))
            return Failed("이전 테스트 결과 파일(TRX)을 지울 수 없습니다");
        // Testing Platform projects run through `dotnet run`, which needs one framework when the project targets several.
        var framework = !legacy && Kind(testProject) != TestProjectKind.VsTest ? FirstOfMultipleFrameworks(testProject) : null;
        var spec = legacy ? LegacySpec(testProject, workDir, trxPath) : SdkSpec(testProject, workDir, trxPath, framework);
        ProcessResult result;
        try
        {
            result = await processes.RunAsync(spec, ct);
        }
        catch (ProcessStartException ex)
        {
            return Failed(ex.Message);
        }
        if (result.TimedOut) return new TestRun([], [], result.ExitCode, true, false, null);
        if (framework is not null && result.ExitCode != 0
            && LaunchFailureSignals.Any(sig => result.StdOut.Contains(sig, StringComparison.Ordinal) || result.StdErr.Contains(sig, StringComparison.Ordinal)))
            return new TestRun([], [], result.ExitCode, false, false, $"{MultiTargetCannotRun} ({framework})");

        var wantsTrx = legacy
            ? test.Command!.Contains("{trx}", StringComparison.Ordinal)
            : Kind(testProject) != TestProjectKind.OtherTestingPlatform;
        if (!wantsTrx) return new TestRun([], [], result.ExitCode, false, false, null);

        if (!File.Exists(trxPath))
            return new TestRun([], [], result.ExitCode, false, false, $"{ReportNotWritten} (종료 코드 {result.ExitCode})",
                ReportMissing: result.ExitCode != 0);
        try
        {
            var report = TrxParser.ParseFull(File.ReadAllText(trxPath));
            return new TestRun(report.Results, [], result.ExitCode, false, true, null, RunAborted: report.RunAborted, Skipped: report.Skipped);
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
            return new TestRun([], [], result.ExitCode, false, false, "테스트 결과 파일(TRX)을 읽을 수 없습니다");
        }
    }

    private static TestRun Failed(string error) => new([], [], -1, false, false, error);

    /// <summary>False when an old report is still there afterwards (it could be mistaken for this run's).</summary>
    private static bool DeleteStale(string trxPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(trxPath))!);
            File.Delete(trxPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // checked below
        }
        return !File.Exists(trxPath);
    }

    private ProcessSpec SdkSpec(string testProject, string workDir, string trxPath, string? framework)
    {
        var timeout = TimeSpan.FromSeconds(test.TimeoutSeconds);
        List<string> Run(params string[] tail) =>
            [.. new[] { "run", "--project", testProject, "--no-build" },
             .. framework is null ? [] : new[] { "--framework", framework }, .. tail];
        switch (Kind(testProject))
        {
            // One path per project kind, independent of global.json, so behavior is the same inside and outside a repo.
            // `dotnet test` cannot be used: on .NET 10 it refuses VSTest options for Testing Platform projects, and its
            // own Testing Platform mode needs a global.json opt-in.
            case TestProjectKind.XunitV3:
                return new ProcessSpec("dotnet", Run("--", "-result-trx", trxPath), workDir, timeout, BuildVerifier.DotnetChildEnv);
            case TestProjectKind.XunitV3Native:
                // The plain xunit.v3 package's in-process console runner rejects -result-trx ("unknown option", exit 3) and takes -trx.
                return new ProcessSpec("dotnet", Run("--", "-trx", trxPath), workDir, timeout, BuildVerifier.DotnetChildEnv);
            case TestProjectKind.OtherTestingPlatform:
                // Per-test TRX for these (MSTest.Sdk, plain Microsoft.Testing.Platform) is unverified: exit code only (R7).
                return new ProcessSpec("dotnet", Run(), workDir, timeout, BuildVerifier.DotnetChildEnv);
            default:
                return new ProcessSpec("dotnet",
                    ["test", testProject, "--no-build", "--nologo", "--logger", $"trx;LogFileName={trxPath}"],
                    workDir, timeout, BuildVerifier.DotnetChildEnv);
        }
    }

    /// <summary>
    /// <see cref="TestProjectKind.XunitV3"/>: xunit.v3.mtp-* (Microsoft.Testing.Platform, <c>-result-trx</c>).
    /// <see cref="TestProjectKind.XunitV3Native"/>: the plain xunit.v3 package (in-process console runner, <c>-trx</c>).
    /// </summary>
    internal enum TestProjectKind { VsTest, XunitV3, XunitV3Native, OtherTestingPlatform }

    private static readonly System.Text.RegularExpressions.Regex XunitV3Ref =
        new(@"<PackageReference\s+[^>]*Include\s*=\s*""xunit\.v3", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly System.Text.RegularExpressions.Regex XunitV3MtpRef =
        new(@"<PackageReference\s+[^>]*Include\s*=\s*""xunit\.v3\.mtp", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly string[] TestingPlatformSignals =
    [
        "IsTestingPlatformApplication", "EnableMicrosoftTestingPlatform", "TestingPlatformDotnetTestSupport",
        "Include=\"xunit.v3.mtp", "Include=\"MSTest.Sdk", "Sdk=\"MSTest.Sdk", "Include=\"Microsoft.Testing.Platform",
    ];

    /// <summary>Classifies the project from its csproj and the nearest Directory.Build.props; global.json is an extra signal.</summary>
    internal static TestProjectKind Kind(string testProject)
    {
        var text = ReadSafe(testProject);
        if (NearestBuildProps(testProject) is { } props) text += "\n" + ReadSafe(props);

        if (XunitV3MtpRef.IsMatch(text)) return TestProjectKind.XunitV3;
        if (XunitV3Ref.IsMatch(text)) return TestProjectKind.XunitV3Native;
        if (TestingPlatformSignals.Any(sig => text.Contains(sig, StringComparison.OrdinalIgnoreCase))
            || GlobalJsonOptsIntoTestingPlatform(Path.GetDirectoryName(Path.GetFullPath(testProject))))
            return TestProjectKind.OtherTestingPlatform;
        return TestProjectKind.VsTest;
    }

    private static string? NearestBuildProps(string testProject)
    {
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(testProject)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var props = Path.Combine(dir, "Directory.Build.props");
            if (File.Exists(props)) return props;
        }
        return null;
    }

    /// <summary>
    /// The first entry of <c>TargetFrameworks</c> when it lists more than one: from the csproj, or from the nearest
    /// Directory.Build.props when the csproj sets neither <c>TargetFramework</c> nor <c>TargetFrameworks</c>. Null for a
    /// single-target project or one whose list cannot be read literally (an MSBuild property reference).
    /// </summary>
    internal static string? FirstOfMultipleFrameworks(string testProject)
    {
        var (single, multiple) = Frameworks(testProject);
        if (single is null && multiple is null && NearestBuildProps(testProject) is { } props)
            (single, multiple) = Frameworks(props);
        if (!string.IsNullOrWhiteSpace(single) || multiple is null) return null;
        var entries = multiple.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return entries.Length > 1 && !entries[0].Contains('$', StringComparison.Ordinal) ? entries[0] : null;
    }

    private static (string? Single, string? Multiple) Frameworks(string projectFile)
    {
        try
        {
            if (!File.Exists(projectFile)) return (null, null);
            var elements = XDocument.Parse(File.ReadAllText(projectFile)).Descendants().ToList();
            string? Value(string name) => elements.LastOrDefault(e => e.Name.LocalName == name)?.Value.Trim();
            return (Value("TargetFramework"), Value("TargetFrameworks"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            return (null, null); // unreadable: run as before, without a framework choice
        }
    }

    private static string ReadSafe(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : "";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static bool GlobalJsonOptsIntoTestingPlatform(string? startDir)
    {
        try
        {
            for (var dir = startDir; dir is not null; dir = Path.GetDirectoryName(dir))
            {
                var global = Path.Combine(dir, "global.json");
                if (!File.Exists(global)) continue;
                using var doc = JsonDocument.Parse(File.ReadAllText(global), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                return doc.RootElement.TryGetProperty("test", out var t) && t.TryGetProperty("runner", out var r)
                    && r.ValueKind == JsonValueKind.String
                    && string.Equals(r.GetString(), "Microsoft.Testing.Platform", StringComparison.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            // unreadable global.json: no signal
        }
        return false;
    }

    private ProcessSpec LegacySpec(string testProject, string workDir, string trxPath)
    {
        var dll = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(testProject))!, "bin", "Debug", AssemblyName(testProject) + ".dll");
        var tokens = SplitCommand(test.Command!)
            .Select(t => t.Replace("{project}", testProject).Replace("{dll}", dll).Replace("{trx}", trxPath))
            .ToList();
        return new ProcessSpec(tokens[0], tokens.Skip(1).ToList(), workDir, TimeSpan.FromSeconds(test.TimeoutSeconds), BuildVerifier.DotnetChildEnv);
    }

    private static string AssemblyName(string testProject)
    {
        try
        {
            var name = XDocument.Parse(File.ReadAllText(testProject)).Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "AssemblyName")?.Value.Trim();
            if (!string.IsNullOrEmpty(name)) return name;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or XmlException)
        {
            // fall back to the file name
        }
        return Path.GetFileNameWithoutExtension(testProject);
    }

    /// <summary>Whitespace separates tokens; double quotes group (and are dropped); backslashes are literal.</summary>
    internal static List<string> SplitCommand(string command)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var has = false;
        foreach (var c in command)
        {
            if (c == '"') { inQuotes = !inQuotes; has = true; }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (has) { tokens.Add(current.ToString()); current.Clear(); has = false; }
            }
            else { current.Append(c); has = true; }
        }
        if (has) tokens.Add(current.ToString());
        return tokens;
    }
}
