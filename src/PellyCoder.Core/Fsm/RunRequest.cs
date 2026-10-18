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
/// </summary>
public sealed record RunRequest(
    string FilePath, string? ClassName, string? MethodName, int? Line, string Request, bool Yes, bool DryRun, int? MaxRetries,
    string? ObjectName = null, EditKind Kind = EditKind.CSharp, bool Verbose = false, bool RunTests = false);

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

    public EditTarget? Target { get; set; }

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

    /// <summary>Messages for the user; the orchestrator shows each one once through <see cref="IUserInteraction.Warn"/>.</summary>
    public List<string> Messages { get; } = [];

    public Dictionary<RunPhase, double> PhaseSeconds { get; } = [];
}
