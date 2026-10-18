using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Sql;

namespace PellyCoder.Eval;

/// <summary>Re-verification of a finished task; <see cref="IndependentVerifier"/> is the real one (tests substitute it).</summary>
public interface IIndependentVerifier
{
    /// <param name="corpusRoot">The copied top-level corpus folder (<c>work/&lt;id&gt;/Modern</c>); test projects are searched here.</param>
    Task<VerificationResult> VerifyCsAsync(BenchmarkTask task, string file, string corpusRoot, CancellationToken ct);

    Task<VerificationResult> VerifySqlAsync(BenchmarkTask task, string file, CancellationToken ct);
}

/// <summary>
/// Re-verifies a finished task without the orchestrator: a fresh build (and the judge's tests) for C#, NOEXEC compile and
/// object existence for SQL. Its verdict is what the "검증 불변 조건" KPI compares the run's exit code against.
/// </summary>
public sealed class IndependentVerifier(IFileSystem fs, IProcessRunner processes, PellyOptions options, ISqlExecutor? sql,
    Func<bool>? isWindows = null) : IIndependentVerifier
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromMinutes(5);
    private readonly Func<bool> _isWindows = isWindows ?? OperatingSystem.IsWindows;

    public async Task<VerificationResult> VerifyCsAsync(BenchmarkTask task, string file, string corpusRoot, CancellationToken ct)
    {
        var judge = task.Judge ?? new BenchmarkJudge();
        var tests = judge.Tests ?? [];
        if (judge.Build != true && tests.Count == 0)
            return VerificationResult.Skip("판정 요구 없음");

        var csproj = ProjectFileLocator.FindNearestCsproj(fs, file);
        if (csproj is null)
            return VerificationResult.Skip("csproj를 찾지 못함");
        var style = ProjectStyleDetector.Detect(fs, csproj);

        if (style == ProjectStyle.Legacy)
        {
            if (!_isWindows())
                return VerificationResult.Skip(VerificationResult.LegacyBuildSkipped);
            // Windows 미확인(실기): Windows + LLM 러너가 없어 이 경로는 실제 벤치마크로 돌려 보지 못했다.
            var resolved = await new BuildToolResolver(fs, processes, options.Build, _isWindows).ResolveAsync(style, ct);
            if (resolved.Tool is null)
                return VerificationResult.Skip(resolved.Error ?? "MSBuild를 찾지 못함");
            var legacy = await BuildAsync(resolved.Tool, csproj, corpusRoot, ct);
            return legacy.Status == VerificationResult.Passed && tests.Count > 0
                ? VerificationResult.Skip("빌드 통과, 레거시 테스트 판정 생략")
                : legacy;
        }

        var build = await BuildAsync(new BuildTool(BuildToolKind.Dotnet, "dotnet"), csproj, corpusRoot, ct);
        if (build.Status != VerificationResult.Passed)
            return build;
        foreach (var name in tests)
        {
            var failure = await RunTestAsync(name, corpusRoot, ct);
            if (failure is not null) return VerificationResult.Fail(failure);
        }
        return VerificationResult.Pass;
    }

    public async Task<VerificationResult> VerifySqlAsync(BenchmarkTask task, string file, CancellationToken ct)
    {
        if (sql is null)
            return VerificationResult.Skip("SQL 연결 없음");
        var text = TextFileCodec.Read(fs, file).Text;
        if (TargetText.SqlTarget(text, task.Object, options.Sql.CompatLevel) is not var (script, batch))
            return VerificationResult.Fail("대상 객체 배치를 찾지 못함");

        try
        {
            var settings = script.SettingsBefore(batch.Index);
            var errors = await sql.CompileWithNoExecAsync(batch.Text, settings, ct);
            if (errors.Count > 0)
                return VerificationResult.Fail("컴파일 오류: " + string.Join("; ", errors.Take(3).Select(e => $"Msg{e.Number} {e.Message}")));

            var refs = SqlReferenceCollector.Collect(batch.Text, batch.ObjectName!, options.Sql.CompatLevel, settings.QuotedIdentifier);
            var missing = await sql.FindMissingAsync([.. refs.Objects.Select(o => o.Name).DistinctBy(n => n.Display.ToUpperInvariant())], ct);
            return missing.Count > 0
                ? VerificationResult.Fail("없는 객체: " + string.Join(", ", missing.Select(m => m.Display)))
                : VerificationResult.Pass;
        }
        catch (SqlUnavailableException ex)
        {
            return VerificationResult.Skip($"SQL 서버를 사용할 수 없음: {ex.Message}");
        }
    }

    private async Task<VerificationResult> BuildAsync(BuildTool tool, string csproj, string root, CancellationToken ct)
    {
        var run = await new BuildVerifier(processes, options.Build).BuildAsync(tool, csproj, root, ct);
        if (run.TimedOut) return VerificationResult.Fail($"빌드 시간 초과 ({options.Build.TimeoutSeconds}초)");
        return run.Errors.Count == 0
            ? VerificationResult.Pass
            : VerificationResult.Fail("빌드 오류: " + string.Join("; ", run.Errors.Take(3).Select(e => $"{e.Code} {Path.GetFileName(e.File)}({e.Line}) {e.Message}")));
    }

    /// <summary>Runs one test by full name in the test project whose name is the longest prefix of it; null when it passed.</summary>
    private async Task<string?> RunTestAsync(string name, string corpusRoot, CancellationToken ct)
    {
        var project = Directory.GetFiles(corpusRoot, "*.csproj", SearchOption.AllDirectories)
            .Where(p => name.StartsWith(Path.GetFileNameWithoutExtension(p) + ".", StringComparison.Ordinal))
            .OrderByDescending(p => Path.GetFileNameWithoutExtension(p).Length)
            .FirstOrDefault();
        if (project is null) return $"테스트 프로젝트를 찾지 못함: {name}";

        var result = await processes.RunAsync(new ProcessSpec("dotnet",
            ["test", project, "--no-restore", "--filter", $"FullyQualifiedName={name}"],
            Path.GetDirectoryName(project)!, TestTimeout), ct);
        if (result.TimedOut) return $"테스트 시간 초과: {name}";
        return result.ExitCode == 0 ? null : $"테스트 실패(종료 {result.ExitCode}): {name}";
    }
}
