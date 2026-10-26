namespace PellyCoder.Core.Testing;

/// <summary>
/// The regression rules shared by <c>--test</c>, <c>--verify-test</c> and the group test-first check: which tests an edit newly
/// broke, compared with the baseline run of the same project (design §8.9 step 4, V2-2 §5.5).
/// </summary>
public static class TestRegressions
{
    /// <summary>
    /// Model-facing message of a test that passed in the baseline and is missing from a failed run
    /// (사용자 의미: 실행되지 않음 (테스트 호스트가 중단되었을 수 있음)).
    /// </summary>
    public const string NotRunFeedback = "not run (test host may have crashed)";

    /// <summary>
    /// Tests that newly fail in <paramref name="after"/>: failing now and not failing in <paramref name="before"/> (passed there or
    /// absent), plus the baseline passes a crashed test host left out. Results matching an <paramref name="excluded"/> name
    /// (<see cref="TestNames.Matches"/>) are left out. <c>ExitOnlyFailure</c>: the project failed by exit code with no test to name.
    /// The caller has already handled timed-out runs, runner errors and runs with build errors (except a missing report after a
    /// baseline with per-test results, which is a crashed host).
    /// </summary>
    public static (IReadOnlyList<TestCaseResult> Failures, bool ExitOnlyFailure) Find(
        TestRun before, TestRun after, IReadOnlyList<string> excluded) =>
        Find(before, after, excluded, null);

    /// <summary><see cref="Find(TestRun, TestRun, IReadOnlyList{string})"/> that also collects the excluded results' names (without arguments).</summary>
    public static (IReadOnlyList<TestCaseResult> Failures, bool ExitOnlyFailure) Find(
        TestRun before, TestRun after, IReadOnlyList<string> excluded, ISet<string>? excludedHits)
    {
        bool IsExcluded(TestCaseResult r)
        {
            if (!excluded.Any(e => TestNames.Matches(e, r.FullName))) return false;
            excludedHits?.Add(TestNames.WithoutArguments(r.FullName));
            return true;
        }

        if (after.ReportMissing && before.PerTestResults)
        {
            // The edit killed an in-process test host (stack overflow, fail-fast): no report exists, which is a test failure the
            // model can fix, not a broken environment. Every test that passed before did not pass now.
            var lost = before.Results.Where(r => r.Passed)
                .Select(r => new TestCaseResult(r.FullName, false, NotRunFeedback)).ToList();
            lost.RemoveAll(IsExcluded);
            return (lost, lost.Count == 0);
        }

        var brokeExit = before.ExitCode == 0 && after.ExitCode != 0;
        // Exit code only (R7): a pass that turned into a failure fails; an original failure cannot be compared.
        if (!after.PerTestResults || !before.PerTestResults) return ([], brokeExit);

        var failedBefore = before.Results.Where(r => !r.Passed).Select(r => r.FullName).ToHashSet(StringComparer.Ordinal);
        var found = after.Results.Where(r => !r.Passed && !failedBefore.Contains(r.FullName)).ToList();
        // A crashed test host leaves a partial report: tests that passed before and are missing now did not pass. When the
        // baseline already failed, a missing name alone is not enough (dynamic test names change between runs); the run must
        // also look cut short: an abnormal run outcome, or fewer executed results than the baseline (a VSTest host killed by
        // FailFast writes outcome Failed with zero results, measured 2026-10-19).
        var cutShort = brokeExit
            || (before.ExitCode != 0 && after.ExitCode != 0 && (after.RunAborted || after.Results.Count < before.Results.Count));
        if (cutShort)
        {
            var ran = after.Results.Select(r => r.FullName).ToHashSet(StringComparer.Ordinal);
            found.AddRange(before.Results.Where(r => r.Passed && !ran.Contains(r.FullName))
                .Select(r => new TestCaseResult(r.FullName, false, NotRunFeedback)));
        }
        var excludedHere = found.RemoveAll(IsExcluded);
        // The exit code still counts when no test can be named (e.g. the host died before reporting anything).
        return (found, found.Count == 0 && excludedHere == 0 && brokeExit);
    }

    /// <summary>
    /// Regressions of a project whose baseline did not compile (compile RED): every failing result not matching an
    /// <paramref name="excluded"/> name. Without per-test results nothing can be judged; that call is the caller's.
    /// </summary>
    public static IReadOnlyList<TestCaseResult> Strict(TestRun after, IReadOnlyList<string> excluded) =>
        after.Results.Where(r => !r.Passed && !excluded.Any(e => TestNames.Matches(e, r.FullName))).ToList();
}
