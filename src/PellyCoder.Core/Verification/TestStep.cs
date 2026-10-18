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
/// </summary>
public sealed class TestStep(TestRunner runner, FeedbackBuilder feedback, TestOptions options) : IVerifierStep
{
    public const string NotComparable = "테스트가 원래도 실패해 비교할 수 없습니다";

    /// <summary>Notice for a test project that did not build before the edit; followed by its file name.</summary>
    public const string BaselineNotBuilt = "원본 상태에서 테스트 프로젝트가 빌드되지 않아 테스트 판정을 건너뜁니다";

    /// <summary>
    /// Model-facing message of a test that passed in the baseline and is missing from a failed run
    /// (사용자 의미: 실행되지 않음 (테스트 호스트가 중단되었을 수 있음)).
    /// </summary>
    public const string NotRunFeedback = "not run (test host may have crashed)";

    public string Name => "Test";

    public async Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        if (ctx.TestProjects.Count == 0 || ctx.BuildTool is not { } tool) return StepResult.Pass;
        var baselines = ctx.BaselineTestRuns
            ?? throw new InvalidOperationException("APPLY must await the baseline tests before VERIFY runs them.");

        var failures = new List<TestCaseResult>();          // named tests, across all projects
        var exitFailures = new List<(string Project, int ExitCode)>(); // projects that failed without a named test
        for (var i = 0; i < ctx.TestProjects.Count; i++)
        {
            // Baseline tests that could not run were announced by APPLY; this project is judged by the build alone.
            if (baselines[i] is not { } before) continue;

            var project = ctx.TestProjects[i];
            var run = await runner.RunAsync(tool, project, ctx.Workspace!.Root, TrxPath(ctx, $"verify-{ctx.Attempt}-{i + 1}"), ct);
            if (run.TimedOut)
                return StepResult.Environment(TimedOutMessage(options));
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

            var brokeExit = before.ExitCode == 0 && run.ExitCode != 0;
            if (!run.PerTestResults || !before.PerTestResults)
            {
                // Exit code only (R7): a pass that turned into a failure fails; an original failure cannot be compared.
                if (brokeExit)
                    exitFailures.Add((project, run.ExitCode));
                else if (before.ExitCode != 0 && !ctx.TestNotComparableShown)
                {
                    ctx.Messages.Add(NotComparable);
                    ctx.TestNotComparableShown = true;
                }
                continue;
            }

            var failedBefore = before.Results.Where(r => !r.Passed).Select(r => r.FullName).ToHashSet(StringComparer.Ordinal);
            var found = run.Results.Where(r => !r.Passed && !failedBefore.Contains(r.FullName)).ToList();
            if (run.ExitCode != 0)
            {
                // A crashed test host leaves a partial report: tests that passed before and are missing now did not pass.
                var ran = run.Results.Select(r => r.FullName).ToHashSet(StringComparer.Ordinal);
                found.AddRange(before.Results.Where(r => r.Passed && !ran.Contains(r.FullName))
                    .Select(r => new TestCaseResult(r.FullName, false, NotRunFeedback)));
            }
            failures.AddRange(found);
            // The exit code still counts when no test can be named (e.g. the host died before reporting anything).
            if (found.Count == 0 && brokeExit) exitFailures.Add((project, run.ExitCode));
        }

        if (failures.Count == 0 && exitFailures.Count == 0) return StepResult.Pass;
        var unique = failures.DistinctBy(f => f.FullName).ToList();
        // R8: code TEST; the test name stays in the message, which the de-identified report never prints.
        var diagnostics = unique.Select(f => new Diagnostic("", 0, 0, "error", "TEST", f.FullName))
            .Concat(exitFailures.Select(e => new Diagnostic("", 0, 0, "error", "TEST", Path.GetFileName(e.Project))))
            .ToList();
        var text = unique.Count > 0 ? feedback.ForTests(unique) : feedback.ForTestExitCode(exitFailures[0].ExitCode);
        return StepResult.Fail(text, diagnostics, []);
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
