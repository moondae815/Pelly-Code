using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using PellyCoder.Core.CharTests;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Testing;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Logging;

/// <summary>Per-run summary written to <c>summary.json</c> (design §8.12, QueueSeconds is the server wait split out of the client TTFT, R3).</summary>
public sealed record RunSummary(
    string RunId, string PellyVersion, string? OllamaVersion, string ModelName, string PromptVersion,
    EditKind Kind, string ProjectStyle, int ExitCode, int Attempts,
    IReadOnlyDictionary<RunPhase, double> PhaseSeconds, double TtftSeconds, double QueueSeconds,
    string ThinkMode, int ThinkTokens, bool ThinkTruncated,
    int InputTokensLocal, int? InputTokensServer, int? InputTokensCached,
    IReadOnlyList<MatchStage> MatchStages, IReadOnlyList<string> ErrorCodes, int FormatErrors, string? FailureKind,
    IReadOnlyList<string>? RulesIncluded = null, IReadOnlyList<string>? RulesDropped = null,
    TestFirstSummary? TestFirst = null, SqlProdCheckSummary? SqlProd = null, CharTestSummary? CharTest = null);

/// <summary>
/// Production comparison recorded in the run summary: the outcome name and the three normalized hashes (no definition text).
/// <paramref name="RecordRefreshed"/> is true when the file as the run left it carries the refreshed pull record.
/// </summary>
public sealed record SqlProdCheckSummary(string Outcome, string? FileHash, string? ProdHash, string? PulledHash, bool RecordRefreshed = false);

/// <summary>
/// Test-first outcome of a run: counts and enum states only, never test names (they stay out of the sanitized report).
/// <paramref name="InApply"/> is true when <c>pelly apply</c> ran it as one of its tasks. <paramref name="FirstAttemptGreen"/>
/// is the first attempt's GREEN verdict (NotChecked when that attempt did not reach the test step).
/// </summary>
public sealed record TestFirstSummary(int Tests, RedState Red, GreenState Green, int Regressions, int BreaksExcluded, bool InApply = false,
    GreenState FirstAttemptGreen = GreenState.NotChecked);

/// <summary>
/// Writes <c>.pelly/runs/&lt;runId&gt;/</c>: <c>events.jsonl</c>, <c>attempt-&lt;n&gt;/*.txt</c>, <c>summary.json</c>.
/// Logging is best effort: any failure (except cancellation) is recorded in <see cref="RunContext.Messages"/> and never fails the run.
/// </summary>
public sealed class RunLogger(IFileSystem fs, TimeProvider time)
{
    private const string StampFormat = "yyyyMMdd-HHmmss";

    private static readonly Lazy<PromptResources> Prompts = new(PromptResources.LoadDefault);

    private static readonly JsonSerializerOptions LineJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions SummaryJson = new(LineJson) { WriteIndented = true };

    private bool _failed;

    /// <summary><c>yyyyMMdd-HHmmss-xxxx</c> in local time with four lowercase hex digits.</summary>
    public static string NewRunId(TimeProvider time) =>
        time.GetLocalNow().ToString(StampFormat, CultureInfo.InvariantCulture) + "-" + Random.Shared.Next(0x10000).ToString("x4", CultureInfo.InvariantCulture);

    /// <summary>
    /// Deletes run folders older than <paramref name="retentionDays"/>. Call only from a run that holds the workspace
    /// lock (a concurrent run's folder must not vanish); the current run's folder is never deleted. Best effort.
    /// </summary>
    public void Prune(Workspace ws, int retentionDays, string currentRunId)
    {
        try
        {
            var runs = fs.Path.Combine(ws.PellyDir, "runs");
            if (!fs.Directory.Exists(runs)) return;
            var cutoff = time.GetLocalNow().DateTime.AddDays(-retentionDays);
            foreach (var dir in fs.Directory.GetDirectories(runs))
            {
                var name = fs.Path.GetFileName(dir);
                if (name == currentRunId || name.Length < StampFormat.Length) continue;
                if (!DateTime.TryParseExact(name[..StampFormat.Length], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var stamp))
                    continue;
                if (stamp >= cutoff) continue;
                // An unfinished `pelly test` run stays until a person resolves it (its marker blocks cs, sql, test and apply).
                if (fs.File.Exists(fs.Path.Combine(dir, CharTestPending.FileName))) continue;
                try { fs.Directory.Delete(dir, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* retried at the next run */ }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Retention is housekeeping: never fails the run.
        }
    }

    /// <summary>Creates the run folder and resets the per-run failure note. Does not prune (see <see cref="Prune"/>).</summary>
    public void Start(RunContext ctx)
    {
        _failed = false;
        Safe(ctx, () =>
        {
            fs.Directory.CreateDirectory(RunsDir(ctx));
            fs.Directory.CreateDirectory(RunDir(ctx));
        });
    }

    public void Phase(RunContext ctx, PhaseOutcome outcome, TimeSpan elapsed) => Safe(ctx, () =>
    {
        var line = JsonSerializer.Serialize(new PhaseEvent(time.GetUtcNow(), ctx.Phase, outcome, ctx.Attempt, (long)elapsed.TotalMilliseconds), LineJson);
        fs.File.AppendAllText(fs.Path.Combine(RunDir(ctx), "events.jsonl"), line + "\n");
    });

    public void Attempt(RunContext ctx, string prompt, GenerationResult gen, Feedback? feedback) => Safe(ctx, () =>
    {
        var dir = fs.Path.Combine(RunDir(ctx), $"attempt-{ctx.Attempt}");
        fs.Directory.CreateDirectory(dir);
        fs.File.WriteAllText(fs.Path.Combine(dir, "prompt.txt"), prompt);
        if (gen.ThinkText is not null) fs.File.WriteAllText(fs.Path.Combine(dir, "think.txt"), gen.ThinkText);
        fs.File.WriteAllText(fs.Path.Combine(dir, "output.txt"), gen.Output);
        if (feedback is not null) fs.File.WriteAllText(fs.Path.Combine(dir, "feedback.txt"), feedback.Text);
    });

    /// <summary>Writes <c>summary.json</c> and, when there are any, the user messages to <c>messages.txt</c>.</summary>
    public void Complete(RunContext ctx) => Safe(ctx, () =>
    {
        var dir = RunDir(ctx);
        fs.Directory.CreateDirectory(dir);
        fs.File.WriteAllText(fs.Path.Combine(dir, "summary.json"), JsonSerializer.Serialize(Summarize(ctx), SummaryJson));
        if (ctx.Messages.Count > 0)
            fs.File.WriteAllText(fs.Path.Combine(dir, "messages.txt"), string.Join("\n", ctx.Messages) + "\n");
    });

    public static RunSummary Summarize(RunContext ctx)
    {
        var gen = ctx.Generation;
        var first = gen is null ? null : gen.ThinkUsed && gen.ThinkUsage is not null ? gen.ThinkUsage : gen.CodeUsage;
        var queue = first?.Queue.TotalSeconds ?? 0;
        var ttft = first is null ? 0 : (first.Ttft - first.Queue).TotalSeconds;
        // A `pelly test` run is CSharpTest from its start, also when it stops before LOCATE sets the target.
        var kind = ctx.CharTest is not null ? EditKind.CSharpTest : ctx.Target?.Kind ?? ctx.Request.Kind;
        return new RunSummary(
            ctx.RunId, PellyInfo.Version, ctx.ServerStatus?.Version, ctx.Options.Llm.Model, Prompts.Value.VersionFor(kind),
            kind, ctx.ProjectStyle, ctx.ExitCode, ctx.Attempt,
            new Dictionary<RunPhase, double>(ctx.PhaseSeconds), ttft, queue,
            ctx.Options.Llm.ThinkMode.ToString(), gen?.ThinkTokens ?? 0, gen?.ThinkTruncated ?? false,
            ctx.Prompt?.Tokens ?? 0, gen?.CodeUsage.PromptEvalCount, gen?.CodeUsage.PromptEvalCachedCount,
            [.. ctx.MatchStages], [.. ctx.ErrorCodes], ctx.FormatErrors, ctx.FailureKind,
            ctx.Prompt?.Rules is { } sel ? [.. sel.Included.Select(r => r.Text)] : null,
            ctx.Prompt?.Rules is { } sel2 ? [.. sel2.Dropped.Select(r => r.Text)] : null,
            SummarizeTestFirst(ctx),
            ctx.SqlProd is { } p
                ? new SqlProdCheckSummary(p.Outcome.ToString(), p.FileHash, p.ProdHash, p.PulledHash,
                    ctx.Sql?.RecordRefresh is { } refresh && ctx.Current is { } current && refresh.IsIn(current.Text))
                : null,
            ctx.CharTest?.Summary());
    }

    private static TestFirstSummary? SummarizeTestFirst(RunContext ctx)
    {
        if (ctx.TestFirst is not { } s) return null;
        // Only the names with the RED role: AlreadyPassed wins; Red only when the baseline was actually judged (no
        // unverifiable/failure verdict). Without such a name RED is NotChecked.
        var tests = s.Red?.Tests.Where(t => ctx.Request.CheckOf(t.Name).HasFlag(TestCheck.Red)).ToList() ?? [];
        var red = s.Red is null ? RedState.NotChecked
            : tests.Any(t => t.State == RedState.AlreadyPassed) ? RedState.AlreadyPassed
            : s.Red.Unverifiable is null && s.Red.FailureKind is null && tests.Any(t => t.State == RedState.Red) ? RedState.Red
            : RedState.NotChecked;
        return new TestFirstSummary(s.Names.Count, red, s.Green, s.Regressions, s.BreaksExcluded, ctx.Request.Host is not null,
            s.FirstAttemptGreen);
    }

    private string RunsDir(RunContext ctx) => fs.Path.Combine(ctx.Workspace!.PellyDir, "runs");

    private string RunDir(RunContext ctx) => fs.Path.Combine(RunsDir(ctx), ctx.RunId);

    private void Safe(RunContext ctx, Action write)
    {
        if (ctx.Workspace is null) return;
        try
        {
            write();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (_failed) return;
            _failed = true; // one note per run is enough
            ctx.Messages.Add($"실행 로그를 쓰지 못했습니다 ({ex.Message}).");
        }
    }

    private sealed record PhaseEvent(DateTimeOffset Ts, RunPhase Phase, PhaseOutcome Outcome, int Attempt, long ElapsedMs);
}
