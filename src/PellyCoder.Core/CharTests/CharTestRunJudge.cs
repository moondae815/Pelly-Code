using PellyCoder.Core.Files;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Testing;

namespace PellyCoder.Core.CharTests;

/// <summary>Outcome of one run judgment; <paramref name="FailureKind"/> null means the run passed the check.</summary>
public sealed record RunVerdict(string? FailureKind, IReadOnlyList<string> FailingTests, IReadOnlyList<TestCaseResult> Details)
{
    public static readonly RunVerdict Ok = new(null, [], []);
}

/// <summary>Run verdicts 4-7 of the characterization-test spec (§4.4): current code, stability, mutation, usable baseline.</summary>
public static class CharTestRunJudge
{
    /// <summary>Verdicts 4 and 5: build, new tests pass on the current code, existing tests did not newly fail.</summary>
    public static RunVerdict OnCurrent(TestRun baseline, TestRun after, IReadOnlyList<string> newTestFullNames, string testFileFullPath)
    {
        if (after.BuildErrors.Count > 0)
        {
            var inTestFile = after.BuildErrors.Any(e => IsFile(e.File, testFileFullPath));
            return new RunVerdict(inTestFile ? FailureKinds.CharTestBuild : FailureKinds.BuildUnverifiable, [], []);
        }

        var failing = new List<TestCaseResult>();
        foreach (var name in newTestFullNames)
        {
            var rows = Rows(after, name);
            if (rows.Count == 0) failing.Add(new TestCaseResult(name, false, "not run (no result for this test)"));
            else failing.AddRange(rows.Where(r => !r.Passed));
        }
        if (failing.Count > 0)
            return new RunVerdict(FailureKinds.CharTestFailsOnCurrent, failing.Select(f => f.FullName).Distinct().ToList(), failing);

        var (regressions, exitOnly) = TestRegressions.Find(baseline, after, newTestFullNames);
        if (regressions.Count > 0 || exitOnly)
            return new RunVerdict(FailureKinds.CharTestRegression, regressions.Select(r => r.FullName).ToList(), regressions);
        return RunVerdict.Ok;
    }

    /// <summary>Verdict 6: a new test whose pass state differs between two runs is flaky.</summary>
    public static RunVerdict Stable(TestRun first, TestRun second, IReadOnlyList<string> newTestFullNames)
    {
        var flaky = newTestFullNames.Where(n => Passed(first, n) != Passed(second, n)).ToList();
        if (flaky.Count == 0) return RunVerdict.Ok;
        var details = flaky.Select(n => new TestCaseResult(n, false, "passed in one run and failed in the other")).ToList();
        return new RunVerdict(FailureKinds.CharTestFlaky, flaky, details);
    }

    /// <summary>Verdict 7: with the target throwing, every new test must fail.</summary>
    public static RunVerdict UnderMutation(TestRun mutated, IReadOnlyList<string> newTestFullNames)
    {
        if (mutated.BuildErrors.Count > 0 || newTestFullNames.Any(n => Rows(mutated, n).Count == 0))
            return new RunVerdict(FailureKinds.CharTestMutationUnbuildable, [], []);
        var survivors = newTestFullNames.Where(n => Passed(mutated, n)).ToList();
        if (survivors.Count == 0) return RunVerdict.Ok;
        return new RunVerdict(FailureKinds.CharTestSurvivesMutation, survivors,
            survivors.Select(n => new TestCaseResult(n, true, null)).ToList());
    }

    /// <summary>Why the baseline run cannot be compared with, or null (failing existing tests alone are fine).</summary>
    public static string? BaselineUsable(TestRun baseline)
    {
        if (baseline.BuildErrors.Count > 0) return "the test project does not build before the change";
        if (baseline.ReportMissing) return "the baseline run wrote no test report";
        if (baseline.RunAborted) return "the baseline run was aborted";
        if (baseline.TimedOut) return "the baseline run timed out";
        if (baseline.Error is not null) return "the baseline run failed: " + baseline.Error;
        return null;
    }

    private static List<TestCaseResult> Rows(TestRun run, string name) =>
        run.Results.Where(r => TestNames.Matches(name, r.FullName)).ToList();

    private static bool Passed(TestRun run, string name)
    {
        var rows = Rows(run, name);
        return rows.Count > 0 && rows.All(r => r.Passed);
    }

    private static bool IsFile(string diagnosticFile, string fullPath)
    {
        if (diagnosticFile.Length == 0) return false;
        var cmp = PathComparers.ForOs;
        string Norm(string p) => p.Replace('\\', '/');
        var d = Norm(diagnosticFile);
        var f = Norm(fullPath);
        if (cmp.Equals(d, f)) return true;
        if (Path.IsPathRooted(diagnosticFile)) return cmp.Equals(Norm(Path.GetFullPath(diagnosticFile)), Norm(Path.GetFullPath(fullPath)));
        // A workspace-relative diagnostic path: the test file ends with it at a segment boundary.
        return f.EndsWith("/" + d.TrimStart('.', '/'), OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }
}
