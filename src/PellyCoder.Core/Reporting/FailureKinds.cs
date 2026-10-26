using PellyCoder.Core.Models;

namespace PellyCoder.Core.Reporting;

/// <summary>
/// Catalog of every <c>RunSummary.FailureKind</c> value pelly can produce. The sanitized report only lets
/// through values for which <see cref="IsKnown"/> is true, so free text (paths, messages) can never leak.
/// </summary>
public static class FailureKinds
{
    public const string Parse = PatchFailureKinds.Parse;
    public const string NotFound = PatchFailureKinds.NotFound;
    public const string Ambiguous = PatchFailureKinds.Ambiguous;
    public const string OutsideRegion = PatchFailureKinds.OutsideRegion;
    public const string Overlap = PatchFailureKinds.Overlap;
    public const string Unencodable = PatchFailureKinds.Unencodable;

    public const string BaselineErrors = "BaselineErrors";
    public const string BuildToolUnavailable = "BuildToolUnavailable";
    public const string BuildUnverifiable = "BuildUnverifiable";
    public const string Crashed = "Crashed";
    public const string FileChangedExternally = "FileChangedExternally";
    public const string ShapeRejected = "ShapeRejected";
    public const string NoChange = "NoChange";

    public const string SqlForbiddenInOriginal = "SqlForbiddenInOriginal";
    public const string SqlParseErrors = "SqlParseErrors";
    public const string SqlPermission = "SqlPermission";
    public const string SqlSafetyRejected = "SqlSafetyRejected";
    public const string SqlTargetNotAllowed = "SqlTargetNotAllowed";

    public const string TargetAmbiguous = "TargetAmbiguous";
    public const string TargetNotFound = "TargetNotFound";

    public const string SyntaxErrors = "SyntaxErrors";
    public const string BuildErrors = "BuildErrors";
    public const string SqlObjectsErrors = "SqlObjectsErrors";
    public const string SqlCompileErrors = "SqlCompileErrors";
    public const string TestErrors = "TestErrors";

    // Test-first verification (--verify-test, design §9).
    public const string TestGreenFailed = "TestGreenFailed";
    public const string TestRegression = "TestRegression";
    public const string TestNotFound = "TestNotFound";
    public const string TestAmbiguous = "TestAmbiguous";
    public const string TestUnverifiable = "TestUnverifiable";
    public const string TestRedDeclined = "TestRedDeclined";
    public const string TestFileChanged = "TestFileChanged";

    public const string RulesInvalid = "RulesInvalid";
    public const string RulesErrors = "RulesErrors";

    public const string EnvError = "EnvError";
    public const string BudgetExceeded = "BudgetExceeded";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Parse, NotFound, Ambiguous, OutsideRegion, Overlap, Unencodable,
        BaselineErrors, BuildToolUnavailable, BuildUnverifiable, Crashed, FileChangedExternally, ShapeRejected, NoChange,
        SqlForbiddenInOriginal, SqlParseErrors, SqlPermission, SqlSafetyRejected, SqlTargetNotAllowed,
        TargetAmbiguous, TargetNotFound,
        SyntaxErrors, BuildErrors, SqlObjectsErrors, SqlCompileErrors, TestErrors,
        TestGreenFailed, TestRegression, TestNotFound, TestAmbiguous, TestUnverifiable, TestRedDeclined, TestFileChanged,
        RulesInvalid, RulesErrors,
        EnvError, BudgetExceeded, Rejected, Cancelled,
    };

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);
}
