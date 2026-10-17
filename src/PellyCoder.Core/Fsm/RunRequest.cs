using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Fsm;

/// <summary>One <c>pelly cs</c> invocation. <paramref name="MaxRetries"/> null means <c>retry.max</c> from the options.</summary>
public sealed record RunRequest(
    string FilePath, string? ClassName, string MethodName, string Request, bool Yes, bool DryRun, int? MaxRetries);

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
    public MemberCandidate? Candidate { get; set; }
    public LanguageTarget? Language { get; set; }

    /// <summary>Syntax errors already in <see cref="Original"/> (set by Locate); VERIFY reports only errors beyond these.</summary>
    public SyntaxBaseline OriginalSyntax { get; set; } = SyntaxBaseline.Empty;

    /// <summary><c>Sdk</c>, <c>Legacy</c> or <c>None</c> (no csproj, or LOCATE not reached).</summary>
    public string ProjectStyle { get; set; } = "None";

    public SliceResult SliceResult { get; set; } = null!;
    public PromptBuildResult? Prompt { get; set; }
    public string? ModelOutput { get; set; }
    public IReadOnlyList<PatchBlock> Blocks { get; set; } = [];

    /// <summary>Latest feedback for the next attempt (one item, never accumulated).</summary>
    public Feedback? Feedback { get; set; }

    public GenerationResult? Generation { get; set; }

    /// <summary>Match stages of the last successful patch.</summary>
    public List<MatchStage> MatchStages { get; } = [];

    /// <summary>Distinct diagnostic codes seen across attempts, in first-seen order.</summary>
    public List<string> ErrorCodes { get; } = [];

    /// <summary>Label of the last failure (null on success).</summary>
    public string? FailureKind { get; set; }

    /// <summary>Messages for the user; the orchestrator shows each one once through <see cref="IUserInteraction.Warn"/>.</summary>
    public List<string> Messages { get; } = [];

    public Dictionary<RunPhase, double> PhaseSeconds { get; } = [];
}
