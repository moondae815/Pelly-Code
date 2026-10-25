using PellyCoder.Core.Reporting;
using PellyCoder.Core.Specs;

namespace PellyCoder.Core.Tests.Reporting;

public sealed class ApplyReportTests
{
    internal static ApplyRecord Apply(string id, int exit = 0, bool archived = false, bool resumed = false, int taskCount = 3,
        params ApplyTaskResult[] tasks) =>
        new(new ApplySummary(id, "spec", tasks, exit, [], archived, TaskCount: taskCount, Resumed: resumed), DateTimeOffset.UnixEpoch);

    internal static ApplyTaskResult T(string group, int exit = 0, string? kind = null) =>
        new("T", "R", exit, kind, 1, group);

    [Fact]
    public void Completion_counts_fresh_applies_that_archived_with_exit_0()
    {
        var applies = new[]
        {
            Apply("apply-1", 0, archived: true),
            Apply("apply-2", 1),
            Apply("apply-3", 0, archived: true, resumed: true),
        };

        var d = ApplyReport.Build(applies);

        Assert.Equal(new Ratio(1, 2), d.Completion);
        Assert.Equal(0.5, d.Completion.Rate);
        Assert.Equal(1, d.Resumed);
    }

    [Fact]
    public void Completion_3_to_8_uses_TaskCount()
    {
        var applies = new[] { 2, 3, 8, 9 }.Select(n => Apply("apply-" + n, 0, archived: true, taskCount: n)).Append(Apply("apply-x", 1, taskCount: 5));

        var d = ApplyReport.Build(applies.ToList());

        Assert.Equal(new Ratio(2, 3), d.Completion3To8);
        Assert.Equal(new Ratio(4, 5), d.Completion);
    }

    [Fact]
    public void Group_build_rate_counts_groups_that_reached_the_build()
    {
        var applies = new[]
        {
            Apply("apply-1", tasks: [T("G1"), T("G1")]),
            Apply("apply-2", tasks: [T("G1"), T("G1", kind: "GroupBuildFailed")]),
            Apply("apply-3", tasks: [T("G1", kind: "GroupMissingTarget")]),
            Apply("apply-4", tasks: [T("G1", 4, "GroupUnverifiable")]),
            Apply("apply-5", tasks: [T("G1", kind: "GroupRejected")]),
            Apply("apply-6", tasks: [T("G1", 1, "BuildErrors")]),   // failed before the group build
            Apply("apply-7", tasks: [new ApplyTaskResult("S", "R", 0, null, 1)]), // not a group task
        };

        var d = ApplyReport.Build(applies);

        Assert.Equal(new Ratio(1, 3), d.GroupBuild);
        Assert.Equal(1, d.GroupsUnverifiable);
    }

    [Fact]
    public void Groups_are_counted_per_apply_and_group()
    {
        var applies = new[] { Apply("apply-1", tasks: [T("G1"), T("G2", kind: "GroupBuildFailed")]) };

        var d = ApplyReport.Build(applies);

        Assert.Equal(new Ratio(1, 2), d.GroupBuild);
    }

    [Fact]
    public void Empty_denominator_has_no_rate()
    {
        var d = ApplyReport.Build([]);

        Assert.Null(d.Completion.Rate);
        Assert.Null(d.GroupBuild.Rate);
    }
}
