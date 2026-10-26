using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Specs;

/// <summary>An acceptance criterion with a <c>test:</c> and the cs tasks that reference it (spec order, done tasks included).</summary>
public sealed record AcLink(string AcId, string Test, IReadOnlyList<string> Breaks, IReadOnlyList<string> LinkedTaskIds);

/// <summary>
/// What one task checks: every test of its linked criteria (key = test name; <c>None</c> when the task is neither the
/// first nor the last linked task) and the tests those criteria break. <see cref="ExpectedHashes"/> maps a test (as keyed in
/// <see cref="Checks"/>) to the declaration hash <c>pelly apply</c> recorded right before the criterion's first linked task ran,
/// when this task is the criterion's last linked task and a different one; null when there is none.
/// </summary>
public sealed record TaskTests(IReadOnlyDictionary<string, TestCheck> Checks, IReadOnlyList<string> Breaks)
{
    public IReadOnlyDictionary<string, string>? ExpectedHashes { get; init; }
}

/// <summary>Computes which cs tasks are linked to which acceptance-criterion tests.</summary>
public static class TestFirstLinks
{
    /// <summary>One link per criterion that has a <c>test:</c>; sql tasks are never linked.</summary>
    public static IReadOnlyList<AcLink> Build(ChangeSpec spec) =>
        spec.Criteria.Where(c => c.Test is not null)
            .Select(c => new AcLink(
                c.Id, c.Test!, c.BreakNames,
                spec.Tasks.Where(t => t.Kind == TaskKind.CSharp && t.AcRefs.Contains(c.Id, StringComparer.Ordinal)).Select(t => t.Id).ToList()))
            .ToList();

    /// <summary>The checks of <paramref name="taskId"/>; null when no criterion links it. A test named by two criteria gets the OR of the roles.</summary>
    public static TaskTests? For(IReadOnlyList<AcLink> links, string taskId)
    {
        var checks = new Dictionary<string, TestCheck>(StringComparer.Ordinal);
        var breaks = new List<string>();
        var any = false;
        foreach (var link in links)
        {
            var i = link.LinkedTaskIds.ToList().IndexOf(taskId);
            if (i < 0) continue;
            any = true;
            var role = TestCheck.None;
            if (i == 0) role |= TestCheck.Red;
            if (i == link.LinkedTaskIds.Count - 1) role |= TestCheck.Green;
            checks[link.Test] = checks.GetValueOrDefault(link.Test) | role;
            foreach (var b in link.Breaks)
                if (!breaks.Contains(b, StringComparer.Ordinal)) breaks.Add(b);
        }
        return any ? new TaskTests(checks, breaks) : null;
    }
}
