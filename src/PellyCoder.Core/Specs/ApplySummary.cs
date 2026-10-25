namespace PellyCoder.Core.Specs;

/// <summary>Options of one <c>pelly apply</c> run.</summary>
public sealed record ApplyOptions(bool Continue, bool Resume, bool ReviewAtEnd, bool Yes, bool DryRun, int? MaxRetries, bool Verbose);

/// <summary>One task's outcome inside an apply (<paramref name="RunId"/> is null when the executor threw).</summary>
public sealed record ApplyTaskResult(string TaskId, string? RunId, int ExitCode, string? FailureKind, double Seconds);

/// <summary>Written to <c>.pelly/runs/&lt;apply-id&gt;/apply.json</c> (never <c>summary.json</c>, so run history skips it).</summary>
/// <param name="Impact">How the start-of-apply impact search found its targets: <c>semantic</c>, <c>name</c>, or null when none ran.</param>
/// <param name="ImpactFallback">The <c>SemanticFailureKind</c> name that made the search name-based; null otherwise.</param>
public sealed record ApplySummary(
    string ApplyId,
    string SpecId,
    IReadOnlyList<ApplyTaskResult> Tasks,
    int ExitCode,
    IReadOnlyList<SpecIssue> Warnings,
    bool Archived,
    string? Impact = null,
    string? ImpactFallback = null);
