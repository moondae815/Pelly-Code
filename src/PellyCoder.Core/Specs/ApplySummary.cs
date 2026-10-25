namespace PellyCoder.Core.Specs;

/// <summary>Options of one <c>pelly apply</c> run.</summary>
public sealed record ApplyOptions(bool Continue, bool Resume, bool ReviewAtEnd, bool Yes, bool DryRun, int? MaxRetries, bool Verbose);

/// <summary>
/// One task's outcome inside an apply (<paramref name="RunId"/> is null when the executor threw). <paramref name="Group"/> is the
/// task's group ID (group tasks only); when the group fails as a whole, <paramref name="FailureKind"/> carries the group's kind.
/// </summary>
public sealed record ApplyTaskResult(string TaskId, string? RunId, int ExitCode, string? FailureKind, double Seconds, string? Group = null);

/// <summary>Written to <c>.pelly/runs/&lt;apply-id&gt;/apply.json</c> (never <c>summary.json</c>, so run history skips it).</summary>
/// <param name="Impact">How the start-of-apply impact search found its targets: <c>semantic</c>, <c>name</c>, or null when none ran.</param>
/// <param name="ImpactFallback">The <c>SemanticFailureKind</c> name that made the search name-based; null otherwise.</param>
/// <param name="TaskCount">All tasks of the spec (done ones included); 0 in an older file.</param>
/// <param name="Resumed">Whether the apply ran with <c>--resume</c>.</param>
public sealed record ApplySummary(
    string ApplyId,
    string SpecId,
    IReadOnlyList<ApplyTaskResult> Tasks,
    int ExitCode,
    IReadOnlyList<SpecIssue> Warnings,
    bool Archived,
    string? Impact = null,
    string? ImpactFallback = null,
    int TaskCount = 0,
    bool Resumed = false);

/// <summary>The failure kinds a task result carries when its whole group failed (see <c>SpecRunner</c>, <c>ApplyReport</c>).</summary>
public static class GroupFailureKinds
{
    public const string Unverifiable = "GroupUnverifiable";
    public const string BuildFailed = "GroupBuildFailed";
    public const string MissingTarget = "GroupMissingTarget";
    public const string Rejected = "GroupRejected";

    public static bool IsGroupKind(string? kind) => kind is Unverifiable or BuildFailed or MissingTarget or Rejected;
}
