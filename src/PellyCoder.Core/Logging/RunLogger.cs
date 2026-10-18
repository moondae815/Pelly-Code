using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Logging;

/// <summary>Per-run summary written to <c>summary.json</c> (design §8.12, QueueSeconds is the server wait split out of the client TTFT, R3).</summary>
public sealed record RunSummary(
    string RunId, string PellyVersion, string? OllamaVersion, string ModelName, string PromptVersion,
    EditKind Kind, string ProjectStyle, int ExitCode, int Attempts,
    IReadOnlyDictionary<RunPhase, double> PhaseSeconds, double TtftSeconds, double QueueSeconds,
    string ThinkMode, int ThinkTokens, bool ThinkTruncated,
    int InputTokensLocal, int? InputTokensServer, int? InputTokensCached,
    IReadOnlyList<MatchStage> MatchStages, IReadOnlyList<string> ErrorCodes, int FormatErrors, string? FailureKind);

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
        return new RunSummary(
            ctx.RunId, PellyInfo.Version, ctx.ServerStatus?.Version, ctx.Options.Llm.Model, Prompts.Value.VersionFor(ctx.Target?.Kind ?? ctx.Request.Kind),
            ctx.Target?.Kind ?? ctx.Request.Kind, ctx.ProjectStyle, ctx.ExitCode, ctx.Attempt,
            new Dictionary<RunPhase, double>(ctx.PhaseSeconds), ttft, queue,
            ctx.Options.Llm.ThinkMode.ToString(), gen?.ThinkTokens ?? 0, gen?.ThinkTruncated ?? false,
            ctx.Prompt?.Tokens ?? 0, gen?.CodeUsage.PromptEvalCount, gen?.CodeUsage.PromptEvalCachedCount,
            [.. ctx.MatchStages], [.. ctx.ErrorCodes], ctx.FormatErrors, ctx.FailureKind);
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
