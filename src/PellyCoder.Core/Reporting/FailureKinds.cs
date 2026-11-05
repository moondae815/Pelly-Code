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
    public const string SqlProdChanged = "SqlProdChanged";

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

    // Group test-first verification (pelly apply, V2-2 §7.3): a group task result carries these when its whole group failed.
    public const string GroupTestGreenFailed = "GroupTestGreenFailed";
    public const string GroupTestRegression = "GroupTestRegression";
    public const string GroupTestUnverifiable = "GroupTestUnverifiable";
    public const string GroupTestFileChanged = "GroupTestFileChanged";

    public const string RulesInvalid = "RulesInvalid";
    public const string RulesErrors = "RulesErrors";

    // Classic ASP edits (pelly asp).
    public const string AspStructureErrors = "AspStructureErrors";
    public const string AspUnknownNames = "AspUnknownNames";
    public const string AspCompileErrors = "AspCompileErrors";
    public const string AspSafetyRejected = "AspSafetyRejected";
    public const string AspEncoding = "AspEncoding";
    public const string AspSpNotFound = "AspSpNotFound";
    public const string AspUnsupportedLanguage = "AspUnsupportedLanguage";
    public const string AspUnsupportedFile = "AspUnsupportedFile";

    public const string JsSyntaxErrors = "JsSyntaxErrors";
    public const string JsLevelExceeded = "JsLevelExceeded";
    public const string JsSafetyRejected = "JsSafetyRejected";
    public const string JsTargetChanged = "JsTargetChanged";
    public const string CssStructureErrors = "CssStructureErrors";
    public const string CssSafetyRejected = "CssSafetyRejected";
    public const string CssTargetChanged = "CssTargetChanged";
    public const string WebVendorFile = "WebVendorFile";
    public const string WebMinifiedFile = "WebMinifiedFile";
    public const string WebUnsupportedFile = "WebUnsupportedFile";
    public const string WebEncoding = "WebEncoding";

    public const string EnvError = "EnvError";
    public const string BudgetExceeded = "BudgetExceeded";
    public const string Rejected = "Rejected";
    public const string Cancelled = "Cancelled";
    public const string CharTestShape = "CharTestShape";
    public const string CharTestNoAssert = "CharTestNoAssert";
    public const string CharTestEnvDependent = "CharTestEnvDependent";
    public const string CharTestBuild = "CharTestBuild";
    public const string CharTestFailsOnCurrent = "CharTestFailsOnCurrent";
    public const string CharTestRegression = "CharTestRegression";
    public const string CharTestFlaky = "CharTestFlaky";
    public const string CharTestSurvivesMutation = "CharTestSurvivesMutation";
    public const string CharTestMutationUnbuildable = "CharTestMutationUnbuildable";
    public const string CharTestBaselineUnusable = "CharTestBaselineUnusable";
    public const string CharTestPending = "CharTestPending";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        Parse, NotFound, Ambiguous, OutsideRegion, Overlap, Unencodable,
        BaselineErrors, BuildToolUnavailable, BuildUnverifiable, Crashed, FileChangedExternally, ShapeRejected, NoChange,
        SqlForbiddenInOriginal, SqlParseErrors, SqlPermission, SqlSafetyRejected, SqlTargetNotAllowed, SqlProdChanged,
        TargetAmbiguous, TargetNotFound,
        SyntaxErrors, BuildErrors, SqlObjectsErrors, SqlCompileErrors, TestErrors,
        TestGreenFailed, TestRegression, TestNotFound, TestAmbiguous, TestUnverifiable, TestRedDeclined, TestFileChanged,
        GroupTestGreenFailed, GroupTestRegression, GroupTestUnverifiable, GroupTestFileChanged,
        RulesInvalid, RulesErrors,
        AspStructureErrors, AspUnknownNames, AspCompileErrors, AspSafetyRejected, AspEncoding, AspSpNotFound, AspUnsupportedLanguage, AspUnsupportedFile,
        JsSyntaxErrors, JsLevelExceeded, JsSafetyRejected, JsTargetChanged, CssStructureErrors, CssSafetyRejected, CssTargetChanged,
        WebVendorFile, WebMinifiedFile, WebUnsupportedFile, WebEncoding,
        EnvError, BudgetExceeded, Rejected, Cancelled,
        CharTestShape, CharTestNoAssert, CharTestEnvDependent, CharTestBuild, CharTestFailsOnCurrent, CharTestRegression, CharTestFlaky, CharTestSurvivesMutation, CharTestMutationUnbuildable, CharTestBaselineUnusable, CharTestPending,
    };

    public static bool IsKnown(string? kind) => kind is not null && All.Contains(kind);
}
