using System.IO.Abstractions;
using System.Text;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Builds;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Sql;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Fsm;

/// <summary>
/// One <c>pelly cs</c> invocation. The target is <paramref name="Line"/> (1-based, not with a class or method name), else
/// <paramref name="MethodName"/>, else found from the request text (design 8.4). <paramref name="MaxRetries"/> null means
/// <c>retry.max</c> from the options. <paramref name="RunTests"/> (<c>--test</c>) adds the test step to VERIFY.
/// <paramref name="Host"/> is set when <c>pelly apply</c> runs this request as one of its tasks; <paramref name="Group"/>
/// when that task belongs to a task group. <paramref name="VerifyTests"/> (<c>--verify-test</c>) names the tests that must
/// fail before the edit and pass after it (design §6); <paramref name="ExpectBreaks"/> (<c>--expect-break</c>) the tests the
/// change may break (design §5.2b).
/// </summary>
public sealed record RunRequest(
    string FilePath, string? ClassName, string? MethodName, int? Line, string Request, bool Yes, bool DryRun, int? MaxRetries,
    string? ObjectName = null, EditKind Kind = EditKind.CSharp, bool Verbose = false, bool RunTests = false,
    ApplyHost? Host = null, GroupRun? Group = null,
    IReadOnlyList<string>? VerifyTests = null, IReadOnlyList<string>? ExpectBreaks = null)
{
    /// <summary>A test-first run: it always runs the tests and judges RED before the first write and GREEN after it.</summary>
    public bool TestFirst => VerifyTests is { Count: > 0 };
}

/// <summary>
/// The request is one task of a task group (design §6). The group verifies with one build of the affected projects at its
/// end, so the run neither builds nor tests, and the target's signature and name may change. <paramref name="InitialFeedback"/>
/// (the group build's errors attributed to this task, on a re-run) is the first attempt's feedback. <paramref name="EditableHelpers"/>
/// are the displays of the private helpers this task's earlier runs added: a re-run may change them (they are in its original
/// file, so the shape check would otherwise reject the change) and sees them as editable.
/// </summary>
public sealed record GroupRun(Feedback? InitialFeedback, IReadOnlyList<string>? EditableHelpers = null);

/// <summary>
/// The <c>pelly apply</c> run that hosts this request. Its caller already holds the workspace lock for the whole apply, so
/// PREFLIGHT neither takes nor releases it, uses <paramref name="Workspace"/> and refuses a target file outside its root.
/// </summary>
public sealed record ApplyHost(Workspace Workspace);

/// <summary>
/// Outcome of one run. <paramref name="Original"/> is the file as first read (null when the run failed before reading it).
/// <paramref name="Final"/> is the file as the run left it: the last write on success; after a failure, rejection or
/// cancellation the rolled-back file, or <paramref name="Original"/> when nothing was written; null when it is unknown
/// (not read, or the rollback found the file changed outside pelly or failed). <paramref name="TargetDisplay"/> is the
/// target member's display in <paramref name="Final"/>: a group run's rename or signature change shows here, and after a
/// rollback to <paramref name="Original"/> it is the original display again; null without a C# target.
/// <paramref name="AddedHelpers"/> are the displays of the private helpers the run's patches added (and, in a group re-run, the
/// editable helpers it was given) that are in <paramref name="Final"/> (all of them when it is null); null when the orchestrator
/// did not report them.
/// </summary>
public sealed record RunResult(int ExitCode, string RunId, string? FilePath, FileSnapshot? Original, FileSnapshot? Final, string? FailureKind,
    string? TargetDisplay = null, IReadOnlyList<string>? AddedHelpers = null);

/// <summary>State of a <c>pelly sql</c> run (set by SQL LOCATE; null for C# runs).</summary>
public sealed class SqlRunState(SqlScript originalScript, int targetIndex, SqlObjectName target, SqlObjectKind kind,
    string statementType, SqlSessionSettings settings, int compatLevel)
{
    public SqlScript OriginalScript { get; } = originalScript;
    public SqlObjectName Target { get; } = target;
    public SqlObjectKind Kind { get; } = kind;
    public string StatementType { get; } = statementType;
    public SqlSessionSettings Settings { get; } = settings;
    public int CompatLevel { get; } = compatLevel;

    /// <summary>Index of the target batch in the script; changes when a patch is applied.</summary>
    public int TargetIndex { get; set; } = targetIndex;

    /// <summary>The target batch as it is now; starts as the original batch.</summary>
    public SqlBatch CurrentTarget { get; set; } = originalScript.Batches[targetIndex];

    /// <summary>Names the original batch references that do not exist (set by the first VERIFY).</summary>
    public IReadOnlyList<SqlObjectName>? OriginalMissing { get; set; }

    /// <summary>Compile errors of the original batch (set by the first VERIFY); only errors beyond these count.</summary>
    public IReadOnlyList<Diagnostic>? OriginalCompileErrors { get; set; }

    public bool NoticesShown { get; set; }
}

/// <summary>
/// State of a test-first run (set by LOCATE; null when the run is not test-first or LOCATE did not reach it). A dry run
/// records no hashes.
/// <see cref="TestFileHashes"/> maps each declaring file's full path to the SHA-256 hex of what pelly must not change there.
/// </summary>
public sealed class TestFirstState(IReadOnlyList<string> names, IReadOnlyList<string> breaks, IReadOnlyDictionary<string, string> testFileHashes)
{
    public IReadOnlyList<string> Names { get; } = names;
    public IReadOnlyList<string> Breaks { get; } = breaks;
    public IReadOnlyDictionary<string, string> TestFileHashes { get; } = testFileHashes;

    /// <summary>Full names of the named tests' declarations; <see cref="HashOf"/> recomputes the hashes with them.</summary>
    public IReadOnlyCollection<string> DeclaredNames { get; init; } = [];

    /// <summary>The RED verdict (set by the first APPLY that awaited the baseline tests).</summary>
    public RedResult? Red { get; set; }

    public GreenState Green { get; set; } = GreenState.NotChecked;
    public int Regressions { get; set; }
    public int BreaksExcluded { get; set; }

    /// <summary>
    /// Hash recorded for a file declaring the named tests: the whole file, or, when it is <paramref name="targetFile"/> (which
    /// pelly edits), only the declarations of <paramref name="testFullNames"/> in it.
    /// </summary>
    public static string HashOf(IFileSystem fs, string file, string targetFile, IReadOnlyCollection<string> testFullNames)
    {
        if (!PathComparers.ForOs.Equals(fs.Path.GetFullPath(file), fs.Path.GetFullPath(targetFile)))
            return TextFileCodec.HashHex(fs.File.ReadAllBytes(file));
        var declarations = TestMethodIndex.DeclarationTexts(fs.File.ReadAllText(file), testFullNames);
        return TextFileCodec.HashHex(Encoding.UTF8.GetBytes(string.Join("\n", declarations)));
    }
}

/// <summary>
/// Mutable state of one run. Phases exchange data only through this object.
/// Properties marked "set by X" are valid only after phase X succeeded.
/// </summary>
public sealed class RunContext(string runId, RunRequest request, PellyOptions options)
{
    public string RunId { get; } = runId;
    public RunRequest Request { get; } = request;
    public PellyOptions Options { get; } = options;
    public int MaxRetries => Request.MaxRetries ?? Options.Retry.Max;

    public RunPhase Phase { get; set; } = RunPhase.Preflight;

    /// <summary>Number of EDIT entries so far; EDIT increments it before building the prompt.</summary>
    public int Attempt { get; set; }

    /// <summary>Number of EDIT results that were <c>FormatError</c> (unparseable model output).</summary>
    public int FormatErrors { get; set; }

    public int ExitCode { get; set; }

    /// <summary>Full path of the target file (set by Preflight).</summary>
    public string FilePath { get; set; } = "";

    /// <summary>Set by Preflight; null only when Preflight failed before resolving it.</summary>
    public Workspace? Workspace { get; set; }

    /// <summary>Workspace lock held for the whole run (set by Preflight, disposed by the orchestrator).</summary>
    public IDisposable? Lock { get; set; }

    public LlmServerStatus? ServerStatus { get; set; }

    /// <summary>File as first read (set by Preflight). Never changes during the run.</summary>
    public FileSnapshot Original { get; set; } = null!;

    /// <summary>Last content pelly read or wrote; external-change checks and rollback compare against it.</summary>
    public FileSnapshot Current { get; set; } = null!;

    /// <summary>Set by APPLY before the first write; rollback restores from it.</summary>
    public string? BackupPath { get; set; }

    /// <summary>Rules from the workspace AGENTS.md (set by Preflight); empty when there is no file or section.</summary>
    public IReadOnlyList<PellyCoder.Core.Rules.ProjectRule> Rules { get; set; } = [];

    public EditTarget? Target { get; set; }

    /// <summary>
    /// The target's display in <see cref="Original"/> (set by Locate). <see cref="Candidate"/> follows a group run's rename or
    /// signature change; the shape and no-change checks compare against the original member through this.
    /// </summary>
    public string? OriginalTargetDisplay { get; set; }

    /// <summary>Set by SQL LOCATE; null in C# runs.</summary>
    public SqlRunState? Sql { get; set; }
    public MemberCandidate? Candidate { get; set; }

    /// <summary>SLICE's dto text with the (member display, file sha) it was built for; a retry with both unchanged reuses it.</summary>
    public (string Display, string Sha256, string? Text)? DtoCache { get; set; }
    public LanguageTarget? Language { get; set; }

    /// <summary>Syntax errors already in <see cref="Original"/> (set by Locate); VERIFY reports only errors beyond these.</summary>
    public SyntaxBaseline OriginalSyntax { get; set; } = SyntaxBaseline.Empty;

    /// <summary><c>Sdk</c>, <c>Legacy</c> or <c>None</c> (no csproj, or LOCATE not reached).</summary>
    public string ProjectStyle { get; set; } = "None";

    /// <summary>Build tool for the project (set by Locate); null means no build verification (no csproj).</summary>
    public BuildTool? BuildTool { get; set; }

    /// <summary>
    /// Build of the untouched project, started by Locate and awaited by Apply before the first write (design §7.3).
    /// Null when there is nothing to build or in a dry run.
    /// </summary>
    public Task<BuildRun>? Baseline { get; set; }

    /// <summary>Cancels <see cref="Baseline"/>; linked to the run token. The orchestrator cancels and disposes it at the end.</summary>
    public CancellationTokenSource? BaselineCancellation { get; set; }

    /// <summary>Finished <see cref="Baseline"/> (set by the first Apply that awaited it).</summary>
    public BaselineSnapshot? BaselineSnapshot { get; set; }

    /// <summary>Test projects for the target (set by Locate with <c>--test</c>); empty means no test step.</summary>
    public IReadOnlyList<string> TestProjects { get; set; } = [];

    /// <summary>
    /// Tests of the untouched code, one run per <see cref="TestProjects"/> entry. Started by Locate after <see cref="Baseline"/>
    /// finishes, on the same cancellation source; awaited by Apply before the first write. Null without test projects or in a dry run.
    /// </summary>
    public Task<IReadOnlyList<TestRun>>? BaselineTests { get; set; }

    /// <summary>Finished <see cref="BaselineTests"/> (set by the first Apply that awaited them); null entries could not run.</summary>
    public IReadOnlyList<TestRun?>? BaselineTestRuns { get; set; }

    /// <summary>The "cannot compare" notice of an exit-code-only test project was shown (once per run).</summary>
    public bool TestNotComparableShown { get; set; }

    /// <summary>Set by LOCATE in a test-first run (in a dry run without hashes); null otherwise.</summary>
    public TestFirstState? TestFirst { get; set; }

    public SliceResult SliceResult { get; set; } = null!;
    public PromptBuildResult? Prompt { get; set; }
    public string? ModelOutput { get; set; }
    public IReadOnlyList<PatchBlock> Blocks { get; set; } = [];

    /// <summary>Latest feedback for the next attempt (one item, never accumulated).</summary>
    public Feedback? Feedback { get; set; }

    public GenerationResult? Generation { get; set; }

    /// <summary>Match stages of the last successful patch.</summary>
    public List<MatchStage> MatchStages { get; } = [];

    /// <summary>
    /// Displays of private helpers the run's patches added, in first-seen order (no duplicates). SLICE shows those still in
    /// the file as <c>[EDITABLE: helper]</c> so later attempts can fix them.
    /// </summary>
    public List<string> AddedHelpers { get; } = [];

    /// <summary>Distinct diagnostic codes seen across attempts, in first-seen order.</summary>
    public List<string> ErrorCodes { get; } = [];

    /// <summary>Label of the last failure (null on success).</summary>
    public string? FailureKind { get; set; }

    /// <summary>
    /// Messages for the user, all written to the run record (<c>messages.txt</c>); the orchestrator shows each one once
    /// through <see cref="IUserInteraction.Warn"/> unless <see cref="Record"/> added it.
    /// </summary>
    public List<string> Messages { get; } = [];

    /// <summary>Indexes in <see cref="Messages"/> that are recorded but not shown.</summary>
    public HashSet<int> RecordOnlyMessages { get; } = [];

    /// <summary>Adds a message to the run record without showing it (it is not wanted on screen, or was already shown).</summary>
    public void Record(string message)
    {
        RecordOnlyMessages.Add(Messages.Count);
        Messages.Add(message);
    }

    public Dictionary<RunPhase, double> PhaseSeconds { get; } = [];
}
