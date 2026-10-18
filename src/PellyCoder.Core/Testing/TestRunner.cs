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
/// </summary>
public sealed record TestRun(
    IReadOnlyList<TestCaseResult> Results,
    IReadOnlyList<Diagnostic> BuildErrors,
    int ExitCode,
    bool TimedOut,
    bool PerTestResults,
    string? Error,
    bool ReportMissing = false);

/// <summary>
/// Builds a test project and runs it: SDK projects through <c>dotnet test</c> with a TRX report, legacy projects
/// through the configured <c>test.command</c> (R7: without <c>{trx}</c> only the exit code is known).
/// </summary>
public sealed class TestRunner(IProcessRunner processes, BuildOptions build, TestOptions test)
{
    public const string LegacyCommandMissing = "레거시 테스트 프로젝트는 test.command 설정이 필요합니다";

    public async Task<TestRun> RunAsync(BuildTool tool, string testProject, string workDir, string trxPath, CancellationToken ct)
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
            built = await new BuildVerifier(processes, build).BuildAsync(tool, testProject, workDir, ct);
        }
        catch (ProcessStartException ex)
        {
            return Failed(ex.Message);
        }
        if (built.TimedOut) return new TestRun([], [], built.ExitCode, true, false, null);
        if (built.Errors.Count > 0) return new TestRun([], built.Errors, built.ExitCode, false, false, null);

        if (!DeleteStale(trxPath))
            return Failed("이전 테스트 결과 파일(TRX)을 지울 수 없습니다");
        var spec = legacy ? LegacySpec(testProject, workDir, trxPath) : SdkSpec(testProject, workDir, trxPath);
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

        var wantsTrx = legacy
            ? test.Command!.Contains("{trx}", StringComparison.Ordinal)
            : Kind(testProject) != TestProjectKind.OtherTestingPlatform;
        if (!wantsTrx) return new TestRun([], [], result.ExitCode, false, false, null);

        if (!File.Exists(trxPath))
            return new TestRun([], [], result.ExitCode, false, false, $"테스트 결과 파일(TRX)이 만들어지지 않았습니다 (종료 코드 {result.ExitCode})",
                ReportMissing: result.ExitCode != 0);
        try
        {
            return new TestRun(TrxParser.Parse(File.ReadAllText(trxPath)), [], result.ExitCode, false, true, null);
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

    private ProcessSpec SdkSpec(string testProject, string workDir, string trxPath)
    {
        var timeout = TimeSpan.FromSeconds(test.TimeoutSeconds);
        switch (Kind(testProject))
        {
            // One path per project kind, independent of global.json, so behavior is the same inside and outside a repo.
            // `dotnet test` cannot be used: on .NET 10 it refuses VSTest options for Testing Platform projects, and its
            // own Testing Platform mode needs a global.json opt-in.
            case TestProjectKind.XunitV3:
                return new ProcessSpec("dotnet", ["run", "--project", testProject, "--no-build", "--", "-result-trx", trxPath],
                    workDir, timeout, BuildVerifier.EnglishEnv);
            case TestProjectKind.OtherTestingPlatform:
                // Per-test TRX for these (MSTest.Sdk, plain Microsoft.Testing.Platform) is unverified: exit code only (R7).
                return new ProcessSpec("dotnet", ["run", "--project", testProject, "--no-build"],
                    workDir, timeout, BuildVerifier.EnglishEnv);
            default:
                return new ProcessSpec("dotnet",
                    ["test", testProject, "--no-build", "--nologo", "--logger", $"trx;LogFileName={trxPath}"],
                    workDir, timeout, BuildVerifier.EnglishEnv);
        }
    }

    internal enum TestProjectKind { VsTest, XunitV3, OtherTestingPlatform }

    private static readonly System.Text.RegularExpressions.Regex XunitV3Ref =
        new(@"<PackageReference\s+[^>]*Include\s*=\s*""xunit\.v3", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static readonly string[] TestingPlatformSignals =
    [
        "IsTestingPlatformApplication", "EnableMicrosoftTestingPlatform", "TestingPlatformDotnetTestSupport",
        "Include=\"xunit.v3.mtp", "Include=\"MSTest.Sdk", "Sdk=\"MSTest.Sdk", "Include=\"Microsoft.Testing.Platform",
    ];

    /// <summary>Classifies the project from its csproj and the nearest Directory.Build.props; global.json is an extra signal.</summary>
    internal static TestProjectKind Kind(string testProject)
    {
        var text = ReadSafe(testProject);
        for (var dir = Path.GetDirectoryName(Path.GetFullPath(testProject)); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            var props = Path.Combine(dir, "Directory.Build.props");
            if (!File.Exists(props)) continue;
            text += "\n" + ReadSafe(props);
            break;
        }

        if (XunitV3Ref.IsMatch(text)) return TestProjectKind.XunitV3;
        if (TestingPlatformSignals.Any(sig => text.Contains(sig, StringComparison.OrdinalIgnoreCase))
            || GlobalJsonOptsIntoTestingPlatform(Path.GetDirectoryName(Path.GetFullPath(testProject))))
            return TestProjectKind.OtherTestingPlatform;
        return TestProjectKind.VsTest;
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
        return new ProcessSpec(tokens[0], tokens.Skip(1).ToList(), workDir, TimeSpan.FromSeconds(test.TimeoutSeconds), BuildVerifier.EnglishEnv);
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
