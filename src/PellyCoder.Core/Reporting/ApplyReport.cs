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
public sealed record ApplyReportData(Ratio Completion, Ratio Completion3To8, Ratio GroupBuild, int GroupsUnverifiable, int Resumed);

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
            applies.Count - fresh.Count);
    }

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
