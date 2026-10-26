using System.IO.Abstractions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Testing;

namespace PellyCoder.Eval;

/// <summary>Re-verification of a finished task; <see cref="IndependentVerifier"/> is the real one (tests substitute it).</summary>
public interface IIndependentVerifier
{
    /// <param name="corpusRoot">The copied suite root (<c>work/&lt;id&gt;/&lt;root&gt;</c>); test projects are searched here.</param>
    Task<VerificationResult> VerifyCsAsync(BenchmarkTask task, string file, string corpusRoot, CancellationToken ct);

    Task<VerificationResult> VerifySqlAsync(BenchmarkTask task, string file, CancellationToken ct);
}

/// <summary>
/// Per-test outcomes of a judge run by full name, or why the tests could not be run. <paramref name="ToolingError"/>: the
/// failure is the judge tooling's (restore, a test project that is not there, a host that could not start), not the edited
/// code's; the verdict is then Skipped, so a transient NuGet problem never counts as an invariant violation. Build errors,
/// timeouts and a run without a per-test report stay failures: the edited code can cause each of them.
/// </summary>
public sealed record JudgeTestRun(IReadOnlyDictionary<string, TestCaseResult> Results, string? Error, bool ToolingError = false);

/// <summary>
/// Re-verifies a finished task without the orchestrator: a fresh build (and the judge's tests) for C#, NOEXEC compile and
/// object existence for SQL. Its verdict is what the "검증 불변 조건" KPI compares the run's exit code against.
/// </summary>
/// <param name="suite">The task's suite; its <c>judges/&lt;root&gt;</c> projects are copied next to the work copy. Null: root tests only.</param>
public sealed class IndependentVerifier(IFileSystem fs, IProcessRunner processes, PellyOptions options, ISqlExecutor? sql,
    Func<bool>? isWindows = null, BenchmarkSuite? suite = null) : IIndependentVerifier
{
    /// <summary>MSBuild property of a judge project naming the root copy it is built against (the built-in default is <c>eval/corpus/&lt;folder&gt;</c>).</summary>
    public const string JudgeTargetRootProperty = "JudgeTargetRoot";

    private static readonly TimeSpan RestoreTimeout = TimeSpan.FromMinutes(5);
    private readonly Func<bool> _isWindows = isWindows ?? OperatingSystem.IsWindows;

    public async Task<VerificationResult> VerifyCsAsync(BenchmarkTask task, string file, string corpusRoot, CancellationToken ct)
    {
        var judge = task.Judge ?? new BenchmarkJudge();
        var tests = judge.Tests ?? [];
        var failing = judge.FailingTests ?? [];
        if (judge.Build != true && tests.Count == 0 && failing.Count == 0)
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
            return legacy.Status == VerificationResult.Passed && (tests.Count > 0 || failing.Count > 0)
                ? VerificationResult.Skip("빌드 통과, 레거시 테스트 판정 생략") with { Build = VerificationResult.Passed }
                : legacy;
        }

        var build = await BuildAsync(new BuildTool(BuildToolKind.Dotnet, "dotnet"), csproj, corpusRoot, ct);
        if (build.Status != VerificationResult.Passed || (tests.Count == 0 && failing.Count == 0))
            return build;

        // The edited project built: whatever the judge tests say, the invariant's build verdict is Passed.
        var run = await RunJudgeTestsAsync(tests, failing, corpusRoot, ct);
        var judged = run.Error is null ? Verdict(tests, failing, run.Results)
            : run.ToolingError ? VerificationResult.Skip("판정 도구 오류: " + run.Error)
            : VerificationResult.Fail(run.Error);
        return judged with { Build = VerificationResult.Passed };
    }

    /// <summary>
    /// <paramref name="tests"/> must all be Passed, <paramref name="failingTests"/> all Failed; a name missing from the
    /// results counts as failed either way (a typo or a deleted test must not pass the judge).
    /// </summary>
    internal static VerificationResult Verdict(IReadOnlyCollection<string> tests, IReadOnlyCollection<string> failingTests,
        IReadOnlyDictionary<string, TestCaseResult> results)
    {
        var failures = new List<string>();
        foreach (var name in tests)
        {
            if (!results.TryGetValue(name, out var r)) failures.Add($"판정 테스트가 결과에 없음: {name}");
            else if (!r.Passed) failures.Add($"판정 테스트 실패: {name}" + (r.Message is { Length: > 0 } m ? $" ({m})" : ""));
        }
        foreach (var name in failingTests)
        {
            if (!results.TryGetValue(name, out var r)) failures.Add($"수정 전 동작 테스트가 결과에 없음: {name}");
            else if (r.Passed) failures.Add($"수정 전 동작 테스트가 여전히 통과: {name}");
        }
        return failures.Count > 0
            ? VerificationResult.Fail(string.Join("; ", failures))
            : new VerificationResult(VerificationResult.Passed,
                $"판정 테스트 통과 {tests.Count}건, 수정 전 동작 테스트 실패 확인 {failingTests.Count}건");
    }

    /// <summary>
    /// Runs every test project that holds one of the names, once each, through <see cref="TestRunner"/> (build, then
    /// <c>--no-build</c> run with a TRX). <paramref name="tests"/> are looked up in the judge projects and the corpus,
    /// <paramref name="failingTests"/> in the corpus only (R3); the project is the one whose name is the longest prefix
    /// of the test name. Judge projects are copied to <c>&lt;work&gt;/judges/&lt;root&gt;</c>, then restored and built with
    /// <c>-p:JudgeTargetRoot=&lt;corpusRoot&gt;</c>; corpus projects are expected to be restored already (the runner restores the solution).
    /// </summary>
    /// <param name="corpusRoot">The copied suite root (<c>work/&lt;id&gt;/&lt;root&gt;</c>).</param>
    public async Task<JudgeTestRun> RunJudgeTestsAsync(IReadOnlyCollection<string> tests, IReadOnlyCollection<string> failingTests,
        string corpusRoot, CancellationToken ct)
    {
        var root = Path.GetFullPath(corpusRoot);
        var workRoot = Path.GetDirectoryName(root)!;
        // The work copy of a root is <work>/<root name>, so the folder name is the root.
        var suiteRoot = tests.Count > 0 ? suite?.Roots.FirstOrDefault(r => r.Name == Path.GetFileName(root)) : null;
        var judgeDir = suiteRoot is null ? null : WorkCopy.CopyJudges(suite!, suiteRoot, workRoot);
        var judgeProjects = judgeDir is null ? [] : Directory.GetFiles(judgeDir, "*.csproj", SearchOption.AllDirectories);
        // A test-first run's judge copies inside the workspace (<root>/JudgeTests/) are never corpus projects.
        var workspaceJudges = Path.Combine(root, WorkCopy.WorkspaceJudgesFolder) + Path.DirectorySeparatorChar;
        var corpusProjects = Directory.GetFiles(root, "*.csproj", SearchOption.AllDirectories)
            .Where(p => !p.StartsWith(workspaceJudges, StringComparison.Ordinal))
            .ToArray();

        var projects = new List<string>();
        foreach (var (name, candidates) in tests.Select(n => (n, judgeProjects.Concat(corpusProjects)))
                     .Concat(failingTests.Select(n => (n, corpusProjects.AsEnumerable()))))
        {
            var project = candidates
                .Where(p => name.StartsWith(Path.GetFileNameWithoutExtension(p) + ".", StringComparison.Ordinal))
                .OrderByDescending(p => Path.GetFileNameWithoutExtension(p).Length)
                .FirstOrDefault();
            if (project is null) return new JudgeTestRun(new Dictionary<string, TestCaseResult>(), $"테스트 프로젝트를 찾지 못함: {name}", ToolingError: true);
            if (!projects.Contains(project)) projects.Add(project);
        }

        var results = new Dictionary<string, TestCaseResult>(StringComparer.Ordinal);
        var dotnet = new BuildTool(BuildToolKind.Dotnet, "dotnet");
        foreach (var project in projects)
        {
            var name = Path.GetFileNameWithoutExtension(project);
            IReadOnlyDictionary<string, string>? properties = null;
            if (judgeProjects.Contains(project))
            {
                properties = new Dictionary<string, string> { [JudgeTargetRootProperty] = root };
                var restore = await processes.RunAsync(JudgeRestoreSpec(project, root), ct);
                if (restore.TimedOut || restore.ExitCode != 0)
                    return new JudgeTestRun(results, $"판정 프로젝트 복원 실패(종료 {restore.ExitCode}): {name}", ToolingError: true);
            }

            var trx = Path.Combine(workRoot, "judge-results", name + ".trx");
            var run = await new TestRunner(processes, options.Build, options.Test).RunAsync(dotnet, project, workRoot, trx, properties, ct);
            var error = run.TimedOut ? $"테스트 시간 초과: {name}"
                : run.BuildErrors.Count > 0 ? $"테스트 프로젝트 빌드 오류: {name}: "
                    + string.Join("; ", run.BuildErrors.Take(3).Select(e => $"{e.Code} {Path.GetFileName(e.File)}({e.Line}) {e.Message}"))
                : run.Error is not null ? $"{run.Error}: {name}"
                : !run.PerTestResults ? $"테스트별 결과를 얻지 못함(종료 {run.ExitCode}): {name}"
                : null;
            // A run-level error that is neither a build error nor a missing report (the host wrote no TRX: possibly the edited
            // code crashed it) is the tooling's: the host could not start, the old TRX could not be deleted or read.
            var tooling = !run.TimedOut && run.BuildErrors.Count == 0 && run.Error is not null && !RunWroteNoReport(run);
            if (error is not null) return new JudgeTestRun(results, error, tooling);
            foreach (var r in run.Results) results[r.FullName] = r;
        }
        return new JudgeTestRun(results, null);
    }

    /// <summary>Restore of a judge project built against <paramref name="corpusRoot"/> (here and in a <c>run --test-first</c> workspace).</summary>
    public static ProcessSpec JudgeRestoreSpec(string project, string corpusRoot) =>
        new("dotnet", ["restore", project, "--nologo", $"-p:{JudgeTargetRootProperty}={corpusRoot}"], Path.GetDirectoryName(project)!, RestoreTimeout);

    /// <summary>The test process ran but left no TRX (<see cref="TestRunner"/>'s missing-report error, whatever the exit code).</summary>
    private static bool RunWroteNoReport(TestRun run) =>
        run.ReportMissing || run.Error?.StartsWith(TestRunner.ReportNotWritten, StringComparison.Ordinal) == true;

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
}
