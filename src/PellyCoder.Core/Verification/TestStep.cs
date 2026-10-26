using System.IO.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Verification;

/// <summary>
/// Runs the target's test projects with the edit applied (<c>--test</c>, design §8.9 step 4) and fails only on tests that
/// newly fail: failing now, and not failing in the baseline run (passed there or absent). Runs after <see cref="BuildStep"/>.
/// A test-first run (<c>--verify-test</c>) also judges GREEN on the same runs, leaves the expected breaks out of the regressions
/// and checks that the files declaring the named tests did not change (design §5.4–§5.6).
/// </summary>
public sealed class TestStep(TestRunner runner, FeedbackBuilder feedback, TestOptions options, IFileSystem fs) : IVerifierStep
{
    public const string NotComparable = "테스트가 원래도 실패해 비교할 수 없습니다";

    /// <summary>Notice for a test project that did not build before the edit; followed by its file name.</summary>
    public const string BaselineNotBuilt = "원본 상태에서 테스트 프로젝트가 빌드되지 않아 테스트 판정을 건너뜁니다";

    /// <summary>
    /// Model-facing message of a test that passed in the baseline and is missing from a failed run
    /// (사용자 의미: 실행되지 않음 (테스트 호스트가 중단되었을 수 있음)).
    /// </summary>
    public const string NotRunFeedback = TestRegressions.NotRunFeedback;

    public string Name => "Test";

    public async Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        if (ctx.TestProjects.Count == 0 || ctx.BuildTool is not { } tool) return StepResult.Pass;
        var baselines = ctx.BaselineTestRuns
            ?? throw new InvalidOperationException("APPLY must await the baseline tests before VERIFY runs them.");

        var testFirst = ctx.Request.TestFirst ? ctx.TestFirst : null;
        var runs = new TestRun?[ctx.TestProjects.Count];     // this attempt's runs, for the GREEN verdict
        var excluded = new HashSet<string>(StringComparer.Ordinal); // expected breaks left out of the regressions
        var failures = new List<TestCaseResult>();          // named tests, across all projects
        var exitFailures = new List<(string Project, int ExitCode)>(); // projects that failed without a named test
        // Expected breaks (--expect-break) are left out of the regressions; their names are counted in `excluded`.
        IReadOnlyList<string> breaks = testFirst?.Breaks ?? [];
        void Collect(TestRun before, TestRun run, string project)
        {
            var (found, exitOnly) = TestRegressions.Find(before, run, breaks, excluded);
            failures.AddRange(found);
            if (exitOnly) exitFailures.Add((project, run.ExitCode));
        }
        for (var i = 0; i < ctx.TestProjects.Count; i++)
        {
            // Baseline tests that could not run were announced by APPLY; this project is judged by the build alone.
            if (baselines[i] is not { } before)
            {
                if (testFirst is not null)
                    throw new InvalidOperationException("A test-first run judges RED only when every baseline test run is usable.");
                continue;
            }

            var project = ctx.TestProjects[i];
            var run = await runner.RunAsync(tool, project, ctx.Workspace!.Root, TrxPath(ctx, $"verify-{ctx.Attempt}-{i + 1}"), ct);
            if (run.TimedOut)
                return StepResult.Environment(TimedOutMessage(options));
            runs[i] = run;
            if (run.ReportMissing && before.PerTestResults)
            {
                // A crashed test host (no report) is a test failure the model can fix, not a broken environment.
                Collect(before, run, project);
                continue;
            }
            if (run.Error is { } error)
                return StepResult.Environment($"테스트를 실행하지 못했습니다: {error}");

            if (run.BuildErrors.Count > 0)
            {
                var newErrors = BaselineDiff.New(before.BuildErrors, run.BuildErrors);
                if (newErrors.FirstOrDefault(e => !BuildMessages.IsCompilerError(e)) is { } blocker)
                    return StepResult.Environment(BuildMessages.Unverifiable(blocker, "테스트 프로젝트가 빌드되는지 먼저 확인하세요"), "BuildUnverifiable");
                if (newErrors.Count > 0)
                {
                    var target = Path.GetRelativePath(ctx.Workspace.Root, ctx.FilePath).Replace('\\', '/');
                    return StepResult.Fail(feedback.ForBuild(newErrors, target, ctx.Current.Text), newErrors, []);
                }
                continue; // fails to build exactly as before: no tests to compare
            }

            // A baseline that did not build was announced by APPLY (BaselineNotBuilt); there is nothing to compare against.
            if (before.BuildErrors.Count > 0) continue;

            // Exit code only (R7): an original failure cannot be compared.
            if ((!run.PerTestResults || !before.PerTestResults) && before.ExitCode != 0 && !ctx.TestNotComparableShown)
            {
                ctx.Messages.Add(NotComparable);
                ctx.TestNotComparableShown = true;
            }
            Collect(before, run, project);
        }

        if (testFirst is not null) return JudgeTestFirst(ctx, testFirst, runs, failures, exitFailures, excluded);
        if (failures.Count == 0 && exitFailures.Count == 0) return StepResult.Pass;
        var unique = failures.DistinctBy(f => f.FullName).ToList();
        // R8: code TEST; the test name stays in the message, which the de-identified report never prints.
        var diagnostics = unique.Select(f => new Diagnostic("", 0, 0, "error", "TEST", f.FullName))
            .Concat(exitFailures.Select(e => new Diagnostic("", 0, 0, "error", "TEST", Path.GetFileName(e.Project))))
            .ToList();
        var text = unique.Count > 0 ? feedback.ForTests(unique) : feedback.ForTestExitCode(exitFailures[0].ExitCode);
        return StepResult.Fail(text, diagnostics, []);
    }

    /// <summary>Notice for an expected break (<c>--expect-break</c>) that still passes after the edit; followed by its name.</summary>
    public const string BreakStillPasses = "깨질 것으로 정한 테스트가 여전히 통과합니다";

    /// <summary>
    /// GREEN and regression verdict of a test-first run: GREEN failures first (<c>TestGreenFailed</c>), then regressions
    /// (<c>TestRegression</c>), then the test file guard and the caller's expected declaration hashes (<c>TestFileChanged</c>,
    /// exit 4). The first attempt's GREEN verdict is kept in <see cref="TestFirstState.FirstAttemptGreen"/>.
    /// </summary>
    private StepResult JudgeTestFirst(RunContext ctx, TestFirstState state, IReadOnlyList<TestRun?> runs,
        List<TestCaseResult> failures, List<(string Project, int ExitCode)> exitFailures, HashSet<string> excluded)
    {
        var red = state.Red ?? throw new InvalidOperationException("APPLY must judge RED before VERIFY judges GREEN.");
        foreach (var name in state.Breaks)
        {
            var rows = runs.Where(r => r is not null).SelectMany(r => r!.Results).Where(r => TestNames.Matches(name, r.FullName)).ToList();
            var notice = $"{BreakStillPasses}: {name}";
            if (rows.Count > 0 && rows.All(r => r.Passed) && !ctx.Messages.Contains(notice)) ctx.Messages.Add(notice);
        }

        // GREEN judges only the names with the GREEN role. Such a test that passed in the baseline and fails now is reported
        // once, as a GREEN failure; every other named test stays in the regression check (one failing in the baseline never counts).
        var greenTests = red.Tests.Where(t => ctx.Request.CheckOf(t.Name).HasFlag(TestCheck.Green)).ToList();
        var named = greenTests.Select(t => t.FullName).ToHashSet(StringComparer.Ordinal);
        var regressions = failures.Where(f => !named.Contains(TestNames.WithoutArguments(f.FullName)))
            .DistinctBy(f => f.FullName).ToList();
        var green = TestFirstJudge.Green(greenTests, runs);
        state.BreaksExcluded = excluded.Count;
        state.ExcludedNames = excluded.Order(StringComparer.Ordinal).ToList();

        // R8: code TEST; the test name stays in the message, which the de-identified report never prints.
        static Diagnostic Test(string message) => new("", 0, 0, "error", "TEST", message);
        if (green.Count > 0)
        {
            state.Green = GreenState.Failed;
            if (ctx.Attempt == 1) state.FirstAttemptGreen = state.Green;
            state.Regressions = regressions.Count;
            var target = Path.GetRelativePath(ctx.Workspace!.Root, ctx.FilePath).Replace('\\', '/');
            var diagnostics = green.Select(g => Test(g.FullName)).Concat(regressions.Select(r => Test(r.FullName))).ToList();
            return StepResult.Fail(feedback.ForGreen(green, regressions, target), diagnostics, [], "TestGreenFailed");
        }

        state.Green = greenTests.Count > 0 ? GreenState.Passed : GreenState.NotChecked;
        if (ctx.Attempt == 1) state.FirstAttemptGreen = state.Green;
        state.Regressions = regressions.Count + exitFailures.Count;
        if (state.Regressions > 0)
        {
            var diagnostics = regressions.Select(r => Test(r.FullName))
                .Concat(exitFailures.Select(e => Test(Path.GetFileName(e.Project)))).ToList();
            var text = regressions.Count > 0 ? feedback.ForTests(regressions) : feedback.ForTestExitCode(exitFailures[0].ExitCode);
            return StepResult.Fail(text, diagnostics, [], "TestRegression");
        }

        foreach (var (file, hash) in state.TestFileHashes)
        {
            string? now;
            try
            {
                now = TestFirstState.HashOf(fs, file, ctx.FilePath, state.DeclaredNames);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                now = null; // deleted or unreadable: not the file RED was judged on
            }
            if (now != hash)
                return StepResult.Environment(
                    $"지정한 테스트 파일이 실행 중에 바뀌었습니다: {Path.GetRelativePath(ctx.Workspace!.Root, file)}", "TestFileChanged");
        }

        // The caller's hashes (pelly apply): the named test's declaration must still be the one the caller recorded, which
        // also catches a change made before this run started (an earlier task or group of the same apply).
        foreach (var (name, expected) in ctx.Request.ExpectedTestHashes ?? new Dictionary<string, string>())
        {
            var root = ctx.Workspace!.Root;
            if (TestDeclarationHash.Of(fs, root, name) == expected) continue;
            // Name the declaring file found now; a vanished declaration falls back to a file LOCATE recorded.
            var file = TestMethodIndex.Find(fs, root, name).Select(d => d.File).FirstOrDefault()
                ?? state.TestFileHashes.Keys.Order(StringComparer.Ordinal).FirstOrDefault()
                ?? ctx.FilePath;
            return StepResult.Environment(
                $"지정한 테스트 파일이 실행 중에 바뀌었습니다: {Path.GetRelativePath(root, file)}", "TestFileChanged");
        }
        return StepResult.Pass;
    }

    public static string TimedOutMessage(TestOptions options) =>
        $"테스트가 {options.TimeoutSeconds}초 안에 끝나지 않았습니다 (test.timeoutSeconds)";

    /// <summary>
    /// Absolute TRX path inside this run's folder (<c>.pelly/runs/&lt;runId&gt;/tests/&lt;name&gt;.trx</c>); baseline and
    /// verify runs use different names, so a report can never be mistaken for another run's.
    /// </summary>
    public static string TrxPath(RunContext ctx, string name) =>
        Path.GetFullPath(Path.Combine(ctx.Workspace!.PellyDir, "runs", ctx.RunId, "tests", name + ".trx"));
}
