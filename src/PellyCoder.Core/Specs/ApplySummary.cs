using System.Text.Json.Serialization;
using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Specs;

/// <summary>Options of one <c>pelly apply</c> run.</summary>
public sealed record ApplyOptions(bool Continue, bool Resume, bool ReviewAtEnd, bool Yes, bool DryRun, int? MaxRetries, bool Verbose);

/// <summary>
/// One task's outcome inside an apply (<paramref name="RunId"/> is null when the executor threw). <paramref name="Group"/> is the
/// task's group ID (group tasks only); when the group fails as a whole, <paramref name="FailureKind"/> carries the group's kind.
/// </summary>
public sealed record ApplyTaskResult(string TaskId, string? RunId, int ExitCode, string? FailureKind, double Seconds, string? Group = null);

/// <summary>How an acceptance criterion with a <c>test:</c> ended in an apply.</summary>
public enum AcOutcome { Met, NotMet, NotRun, Unverified }

/// <summary>
/// One acceptance criterion's result: <paramref name="Red"/> from its first linked task's run, the outcome, and the run ID and
/// expected breaks left out of the regressions of its last linked task's run (null / 0 when that task did not run).
/// </summary>
public sealed record AcResult(string AcId, RedState Red, AcOutcome Outcome, string? GreenRunId, int BreaksExcluded);

/// <summary>Written to <c>.pelly/runs/&lt;apply-id&gt;/apply.json</c> (never <c>summary.json</c>, so run history skips it).</summary>
/// <param name="Impact">How the start-of-apply impact search found its targets: <c>semantic</c>, <c>name</c>, or null when none ran.</param>
/// <param name="ImpactFallback">The <c>SemanticFailureKind</c> name that made the search name-based; null otherwise.</param>
/// <param name="TaskCount">All tasks of the spec (done ones included); 0 in an older file.</param>
/// <param name="Resumed">Whether the apply ran with <c>--resume</c>.</param>
/// <param name="Criteria">Per-criterion results when any criterion has a <c>test:</c>; null (and absent from the file) otherwise.</param>
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
    bool Resumed = false,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<AcResult>? Criteria = null);

/// <summary>The failure kinds a task result carries when its whole group failed (see <c>SpecRunner</c>, <c>ApplyReport</c>).</summary>
public static class GroupFailureKinds
{
    // The one literal lives in GroupVerifier: the KPI (ApplyReport) silently miscounts if the two drift.
    public const string Unverifiable = GroupVerifier.UnverifiableKind;
    public const string BuildFailed = "GroupBuildFailed";
    public const string MissingTarget = "GroupMissingTarget";
    public const string Rejected = "GroupRejected";

    public static bool IsGroupKind(string? kind) => kind is Unverifiable or BuildFailed or MissingTarget or Rejected;
}
