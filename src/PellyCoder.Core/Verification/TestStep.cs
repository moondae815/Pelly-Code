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

    public string Name => "Test";

    public async Task<StepResult> VerifyAsync(RunContext ctx, CancellationToken ct)
    {
        if (ctx.TestProjects.Count == 0 || ctx.BuildTool is not { } tool) return StepResult.Pass;
        var baselines = ctx.BaselineTestRuns
            ?? throw new InvalidOperationException("APPLY must await the baseline tests before VERIFY runs them.");

        var failures = new List<TestCaseResult>();
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

            if (!run.PerTestResults || !before.PerTestResults)
            {
                // Exit code only (R7): a pass that turned into a failure fails; an original failure cannot be compared.
                if (before.ExitCode == 0 && run.ExitCode != 0)
                    return StepResult.Fail(feedback.ForTestExitCode(run.ExitCode),
                        [new Diagnostic("", 0, 0, "error", "TEST", Path.GetFileName(project))], []);
                if (before.ExitCode != 0 && !ctx.TestNotComparableShown)
                {
                    ctx.Messages.Add(NotComparable);
                    ctx.TestNotComparableShown = true;
                }
                continue;
            }

            var failedBefore = before.Results.Where(r => !r.Passed).Select(r => r.FullName).ToHashSet(StringComparer.Ordinal);
            failures.AddRange(run.Results.Where(r => !r.Passed && !failedBefore.Contains(r.FullName)));
        }

        if (failures.Count == 0) return StepResult.Pass;
        var unique = failures.DistinctBy(f => f.FullName).ToList();
        // R8: code TEST; the test name stays in the message, which the de-identified report never prints.
        var diagnostics = unique.Select(f => new Diagnostic("", 0, 0, "error", "TEST", f.FullName)).ToList();
        return StepResult.Fail(feedback.ForTests(unique), diagnostics, []);
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
