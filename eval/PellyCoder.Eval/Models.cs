namespace PellyCoder.Eval;

/// <summary>Result of the runner's own re-verification, independent of the orchestrator's VERIFY.</summary>
/// <param name="Status"><c>Passed</c>, <c>Failed</c> or <c>Skipped</c>: the whole check, behaviour judge included.</param>
/// <param name="Build">
/// The independent build (C#) or NOEXEC compile (SQL) verdict when it differs from <paramref name="Status"/>: a behaviour judge ran
/// (or could not run) after a passing build. Null: <paramref name="Status"/> is the build verdict. The invariant (PRD §2.2) is
/// judged from <see cref="BuildStatus"/> only — a failing judge test is the model's miss, not a violation.
/// </param>
public sealed record VerificationResult(string Status, string? Detail, string? Build = null)
{
    /// <summary>The build/compile verdict the verification invariant is judged from.</summary>
    public string BuildStatus => Build ?? Status;

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
/// <param name="InvariantViolated">Exit 0 but the independent build/compile failed (KPI "검증 불변 조건").</param>
/// <param name="JudgeToolingSkipped">The task's behaviour judge could not run (judge tooling failure): not a success.</param>
public sealed record JudgeOutcome(bool Passed, IReadOnlyList<string> Reasons, string BuildCheck, bool InvariantViolated,
    bool JudgeToolingSkipped = false);

/// <summary>One task's row in <c>results.json</c>.</summary>
/// <param name="Status"><c>Passed</c>, <c>Failed</c> or <c>Skipped</c> (not run; <paramref name="SkipReason"/> says why).</param>
/// <param name="FailureType">Summary FailureKind for a failed run; for exit 0: <c>InvariantViolated</c> (independent build/compile
/// failed), <c>JudgeTooling</c> (the behaviour judge could not run), <c>Judge</c> (a judge failed); else null.</param>
/// <param name="FormatErrors">EDIT attempts whose output could not be parsed (summary.json); the parse-success KPI is (Attempts − FormatErrors) ÷ Attempts.</param>
/// <param name="EditOutcomes">Outcomes of the EDIT phase from <c>events.jsonl</c>, in order.</param>
public sealed record TaskResult(
    string Id, string Kind, string Status, int? ExitCode, int Attempts, IReadOnlyList<string> Reasons,
    VerificationResult? Independent, string BuildCheck, bool InvariantViolated, string? FailureType,
    double? WallSeconds, double? TtftSeconds, int ThinkTokens, bool ThinkTruncated,
    IReadOnlyList<string> EditOutcomes, string? SkipReason, bool IsLegacy, int FormatErrors = 0)
{
    /// <summary>Prefix of a skip caused by the machine, not by pelly (the KPI denominators shrink because of it).</summary>
    public const string EnvironmentPrefix = "환경 오류";

    public static TaskResult Skipped(BenchmarkTask task, string reason) =>
        new(task.Id, task.Kind, VerificationResult.Skipped, null, 0, [], null, VerificationResult.NotRequired, false, null,
            null, null, 0, false, [], reason, task.IsLegacy);

    /// <summary>A skip caused by the environment, labelled <c>환경 오류(kind): detail</c> in the report.</summary>
    public static TaskResult EnvironmentSkipped(BenchmarkTask task, string kind, string detail) =>
        Skipped(task, $"{EnvironmentPrefix}({kind}): {detail}");

    /// <summary>A task that failed before or outside the orchestrator run (definition or runner error).</summary>
    public static TaskResult Failed(BenchmarkTask task, string reason, string failureType) =>
        new(task.Id, task.Kind, VerificationResult.Failed, null, 0, [reason], null, VerificationResult.NotRequired, false, failureType,
            null, null, 0, false, [], null, task.IsLegacy);
}
