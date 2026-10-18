namespace PellyCoder.Eval;

/// <summary>Result of the runner's own re-verification, independent of the orchestrator's VERIFY.</summary>
/// <param name="Status"><c>Passed</c>, <c>Failed</c> or <c>Skipped</c>.</param>
public sealed record VerificationResult(string Status, string? Detail)
{
    public const string Passed = "Passed";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
    public const string NotRequired = "NotRequired";

    /// <summary>Detail of a legacy build check skipped off Windows (design §10.6).</summary>
    public const string LegacyBuildSkipped = "빌드 판정 생략";

    public static VerificationResult Pass { get; } = new(Passed, null);

    public static VerificationResult Fail(string detail) => new(Failed, detail);

    public static VerificationResult Skip(string detail) => new(Skipped, detail);
}

/// <param name="BuildCheck"><c>Passed</c>, <c>Failed</c>, <c>Skipped</c> or <c>NotRequired</c>.</param>
/// <param name="InvariantViolated">Exit 0 but the independent verification failed (KPI "검증 불변 조건").</param>
public sealed record JudgeOutcome(bool Passed, IReadOnlyList<string> Reasons, string BuildCheck, bool InvariantViolated);

/// <summary>One task's row in <c>results.json</c>.</summary>
/// <param name="Status"><c>Passed</c>, <c>Failed</c> or <c>Skipped</c> (not run; <paramref name="SkipReason"/> says why).</param>
/// <param name="FailureType">Summary FailureKind for a failed run, <c>Judge</c> when exit 0 but the judge failed, else null.</param>
/// <param name="EditOutcomes">Outcomes of the EDIT phase from <c>events.jsonl</c>, in order.</param>
public sealed record TaskResult(
    string Id, string Kind, string Status, int? ExitCode, int Attempts, IReadOnlyList<string> Reasons,
    VerificationResult? Independent, string BuildCheck, bool InvariantViolated, string? FailureType,
    double? WallSeconds, double? TtftSeconds, int ThinkTokens, bool ThinkTruncated,
    IReadOnlyList<string> EditOutcomes, string? SkipReason)
{
    public bool IsLegacy => Id.StartsWith("cs-legacy-", StringComparison.Ordinal);

    public static TaskResult Skipped(BenchmarkTask task, string reason) =>
        new(task.Id, task.Kind, VerificationResult.Skipped, null, 0, [], null, VerificationResult.NotRequired, false, null,
            null, null, 0, false, [], reason);
}
