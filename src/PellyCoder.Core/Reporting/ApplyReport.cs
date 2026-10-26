using PellyCoder.Core.Testing;
using PellyCoder.Core.Specs;

namespace PellyCoder.Core.Reporting;

public sealed record Ratio(int Ok, int Total)
{
    public double? Rate => Total == 0 ? null : (double)Ok / Total;
}

/// <param name="Completion">Fresh applies that archived with exit 0 over all fresh applies.</param>
/// <param name="Completion3To8">The same, for specs of 3 to 8 tasks.</param>
/// <param name="GroupBuild">Groups whose build passed over the groups that reached the build.</param>
/// <param name="GroupsUnverifiable">Groups the build could not judge (left out of <paramref name="GroupBuild"/>).</param>
/// <param name="Resumed"><c>--resume</c> applies (left out of the completion rates).</param>
public sealed record ApplyReportData(Ratio Completion, Ratio Completion3To8, Ratio GroupBuild, int GroupsUnverifiable, int Resumed, AcCounts? Criteria = null);

/// <summary>Acceptance-criterion counts from apply.json: Met of Judged, RedWarned of RedChecked, plus unverified criteria and <c>breaks:</c> tests left out of regression.</summary>
public sealed record AcCounts(int Met, int Judged, int RedWarned, int RedChecked, int Unverified, int BreaksExcluded);

/// <summary>Spec completion and group build pass rates from the apply records (counting rules fixed by ApplyReportTests).</summary>
public static class ApplyReport
{
    private enum GroupOutcome { Passed, Failed, Unverifiable, NotJudged }

    public static ApplyReportData Build(IReadOnlyList<ApplyRecord> applies)
    {
        var fresh = applies.Where(a => !a.Summary.Resumed).ToList();
        int Done(IEnumerable<ApplyRecord> xs) => xs.Count(a => a.Summary.ExitCode == 0 && a.Summary.Archived);
        var fresh3To8 = fresh.Where(a => a.Summary.TaskCount is >= 3 and <= 8).ToList();

        int passed = 0, failed = 0, unverifiable = 0;
        foreach (var a in applies)
        {
            foreach (var group in a.Summary.Tasks.Where(t => t.Group is not null).GroupBy(t => t.Group, StringComparer.Ordinal))
            {
                switch (Judge(group))
                {
                    case GroupOutcome.Passed: passed++; break;
                    case GroupOutcome.Failed: failed++; break;
                    case GroupOutcome.Unverifiable: unverifiable++; break;
                }
            }
        }

        return new ApplyReportData(
            new Ratio(Done(fresh), fresh.Count),
            new Ratio(Done(fresh3To8), fresh3To8.Count),
            new Ratio(passed, passed + failed),
            unverifiable,
            applies.Count - fresh.Count,
            CountCriteria(applies));
    }

    private static AcCounts? CountCriteria(IReadOnlyList<ApplyRecord> applies)
    {
        var acs = applies.Where(a => a.Summary.Criteria is not null).SelectMany(a => a.Summary.Criteria!).ToList();
        if (acs.Count == 0) return null;
        // Skipped (resumed), NotChecked and NotRun are counted nowhere.
        var judged = acs.Where(c => c.Red is RedState.Red or RedState.RedCompile && c.Outcome is AcOutcome.Met or AcOutcome.NotMet).ToList();
        return new AcCounts(
            judged.Count(c => c.Outcome == AcOutcome.Met),
            judged.Count,
            acs.Count(c => c.Red == RedState.AlreadyPassed),
            acs.Count(c => c.Red is RedState.Red or RedState.RedCompile or RedState.AlreadyPassed),
            acs.Count(c => c.Outcome == AcOutcome.Unverified),
            judged.Sum(c => c.BreaksExcluded));
    }

    /// <summary>Single --verify-test runs and acceptance criteria added together; null when neither has test-first data.</summary>
    public static TestFirstCounts? Combine(TestFirstCounts? single, AcCounts? ac) =>
        single is null && ac is null ? null
        : new TestFirstCounts(
            (single?.Met ?? 0) + (ac?.Met ?? 0), (single?.Judged ?? 0) + (ac?.Judged ?? 0),
            (single?.RedWarned ?? 0) + (ac?.RedWarned ?? 0), (single?.RedChecked ?? 0) + (ac?.RedChecked ?? 0),
            (single?.BreaksExcluded ?? 0) + (ac?.BreaksExcluded ?? 0));

    private static GroupOutcome Judge(IEnumerable<ApplyTaskResult> tasks)
    {
        var list = tasks.ToList();
        if (list.Any(t => t.FailureKind is GroupFailureKinds.BuildFailed or GroupFailureKinds.MissingTarget)) return GroupOutcome.Failed;
        if (list.Any(t => t.FailureKind == GroupFailureKinds.Unverifiable)) return GroupOutcome.Unverifiable;
        // A rejected group, or a task that failed before the group build, never reached a build verdict.
        if (list.Any(t => t.FailureKind == GroupFailureKinds.Rejected || t.ExitCode != 0)) return GroupOutcome.NotJudged;
        return GroupOutcome.Passed;
    }
}
