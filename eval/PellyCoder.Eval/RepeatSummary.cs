using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Reporting;

namespace PellyCoder.Eval;

/// <summary>
/// The settings a repeated run was measured under, so a comparison (Task 6 <c>compare</c>) can see whether two summaries
/// are comparable. <paramref name="CsPromptVersion"/>/<paramref name="SqlPromptVersion"/> are the embedded edit prompts.
/// </summary>
/// <param name="TestFirst"><c>run --test-first</c>: true; null (and left out of <c>summary.json</c>) for any other run.</param>
public sealed record RunConditions(
    string Model, string CsPromptVersion, string SqlPromptVersion, string ThinkMode, int BudgetThink,
    SamplingOptions SamplingThink, SamplingOptions SamplingCode, string? PellyVersion, string? Tasks, string? Kind,
    string? OllamaVersion = null, string? ModelDigest = null, bool? RulesRemoved = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? TestFirst = null);

/// <summary>One task across the runs.</summary>
/// <param name="Group">Id prefix group (<c>cs-legacy</c>, <c>cs-modern</c>, <c>sql</c>): the R4 category.</param>
/// <param name="Successes">Runs in which the task passed.</param>
/// <param name="Runs">Runs summarised (the denominator; a run that did not reach the task counts as not passed).</param>
/// <param name="Outcomes">Per run: <c>Passed</c>, <c>Failed(&lt;FailureType&gt;)</c>, <c>Skipped</c>, or <c>—</c> when the run has no row.</param>
public sealed record TaskRepeatSummary(string Id, string Group, int Successes, int Runs, IReadOnlyList<string> Outcomes)
{
    /// <summary><c>successes/runs</c>, e.g. <c>2/3</c>.</summary>
    public string Ratio => $"{Successes}/{Runs}";
}

/// <summary>One id prefix group: its passed-task count per run and the statistics over the runs.</summary>
/// <param name="StdDevSuccesses">Sample standard deviation (n − 1) of <paramref name="SuccessesPerRun"/>; 0 for one run.</param>
public sealed record GroupRepeatSummary(
    string Group, int Tasks, double MeanSuccesses, double StdDevSuccesses, int MinSuccesses, int MaxSuccesses,
    IReadOnlyList<int> SuccessesPerRun);

/// <summary>One run (1-based <paramref name="Run"/>).</summary>
/// <param name="Ran">Rows that were not skipped.</param>
/// <param name="InvariantViolations">Exit 0 with a failed independent build/compile (the R4 rule 3 count).</param>
/// <param name="JudgeFailures">FailureType <c>Judge</c>: a semantic judge failed after a passing build.</param>
/// <param name="JudgeTooling">FailureType <c>JudgeTooling</c>: the behaviour judge could not run (not a success).</param>
/// <param name="MedianWallSeconds">Nearest-rank median task time of the run's timed rows; null when none.</param>
/// <param name="Folder">The run's folder relative to the summary (<c>run-&lt;k&gt;</c>).</param>
public sealed record RunRepeatSummary(
    int Run, int Tasks, int Ran, int Successes, int InvariantViolations, int JudgeFailures, int JudgeTooling,
    double? MedianWallSeconds, string? Folder = null);

/// <summary>
/// The top-level <c>summary.json</c> of <c>--repeat N</c>: everything the R4 acceptance rule compares, self-contained.
/// </summary>
/// <param name="MeanSuccesses">Mean of the per-run passed-task counts.</param>
/// <param name="StdDevSuccesses">Sample standard deviation (n − 1) of the per-run passed-task counts; 0 for one run.</param>
/// <param name="MedianWallSeconds">Nearest-rank median task time over every timed row of every run (R4 rule 4).</param>
/// <param name="Cancelled">The last run was interrupted; it is not in the summary.</param>
public sealed record RepeatSummaryData(
    int RunCount, int TaskCount, double MeanSuccesses, double StdDevSuccesses, double? MedianWallSeconds,
    IReadOnlyList<RunRepeatSummary> Runs, IReadOnlyList<GroupRepeatSummary> Groups, IReadOnlyList<TaskRepeatSummary> Tasks,
    RunConditions? Conditions = null, bool Cancelled = false)
{
    /// <summary>The <c>summary.json</c> layout this code writes; <see cref="RepeatSummary.FromJson"/> also reads version 1.</summary>
    public const int CurrentFormatVersion = 2;

    /// <summary>The oldest layout still read (1: no model identity in the conditions; those read as null).</summary>
    public const int MinReadableFormatVersion = 1;

    /// <summary>Layout version; a summary without it (Task 5) is refused rather than read with defaults.</summary>
    [JsonRequired]
    [JsonPropertyOrder(-1)]
    public int FormatVersion { get; init; } = CurrentFormatVersion;
}

/// <summary>Statistics over repeated runs of the same task set (R4), and the <c>summary.json</c> codec.</summary>
public static partial class RepeatSummary
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Strict reading for compare: a missing field is an error, not a default (0 successes, no runs).
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    };

    public static RepeatSummaryData Build(IReadOnlyList<IReadOnlyList<TaskResult>> runs)
    {
        var ids = runs.SelectMany(r => r.Select(t => t.Id)).Distinct(StringComparer.Ordinal).ToList();
        var byRun = runs.Select(r => r.GroupBy(t => t.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)).ToList();

        var tasks = ids.Select(id =>
        {
            var outcomes = byRun.Select(r => r.TryGetValue(id, out var t) ? Outcome(t) : "—").ToList();
            return new TaskRepeatSummary(id, GroupOf(id), outcomes.Count(o => o == VerificationResult.Passed), runs.Count, outcomes);
        }).ToList();

        var groups = tasks.GroupBy(t => t.Group, StringComparer.Ordinal).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
        {
            var members = g.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
            var perRun = runs.Select(r => r.Count(t => members.Contains(t.Id) && IsSuccess(t))).ToList();
            return new GroupRepeatSummary(g.Key, members.Count, Mean(perRun), StdDev(perRun),
                perRun.Count == 0 ? 0 : perRun.Min(), perRun.Count == 0 ? 0 : perRun.Max(), perRun);
        }).ToList();

        var runSummaries = runs.Select((r, i) =>
        {
            var ran = r.Where(t => t.Status != VerificationResult.Skipped).ToList();
            return new RunRepeatSummary(i + 1, r.Count, ran.Count, r.Count(IsSuccess), r.Count(t => t.InvariantViolated),
                r.Count(t => t.FailureType == "Judge"), r.Count(t => t.FailureType == "JudgeTooling"), MedianWall(ran));
        }).ToList();

        var successes = runSummaries.Select(r => r.Successes).ToList();
        return new RepeatSummaryData(runs.Count, ids.Count, Mean(successes), StdDev(successes),
            MedianWall(runs.SelectMany(r => r).Where(t => t.Status != VerificationResult.Skipped)), runSummaries, groups, tasks);
    }

    public static string ToJson(RepeatSummaryData data) => JsonSerializer.Serialize(data, Json);

    /// <summary>Reads a <c>summary.json</c> strictly: every constructor field must be present and non-null where declared so,
    /// and <see cref="RepeatSummaryData.FormatVersion"/> must be between <see cref="RepeatSummaryData.MinReadableFormatVersion"/> and <see cref="RepeatSummaryData.CurrentFormatVersion"/>.</summary>
    public static RepeatSummaryData FromJson(string json)
    {
        var data = JsonSerializer.Deserialize<RepeatSummaryData>(json, Json) ?? throw new JsonException("summary.json이 비어 있습니다.");
        if (data.FormatVersion is < RepeatSummaryData.MinReadableFormatVersion or > RepeatSummaryData.CurrentFormatVersion)
            throw new JsonException($"summary.json formatVersion {data.FormatVersion}을 읽을 수 없습니다(지원: {RepeatSummaryData.MinReadableFormatVersion}~{RepeatSummaryData.CurrentFormatVersion}).");
        return data;
    }

    /// <summary>The id prefix group: the id without its trailing <c>-NNN</c> (<c>cs-modern-007</c> → <c>cs-modern</c>).</summary>
    public static string GroupOf(string id) => GroupPattern().Match(id) is { Success: true } m ? m.Groups[1].Value : id;

    private static bool IsSuccess(TaskResult t) => t.Status == VerificationResult.Passed;

    private static string Outcome(TaskResult t) => t.Status == VerificationResult.Failed ? $"Failed({t.FailureType ?? "?"})" : t.Status;

    private static double? MedianWall(IEnumerable<TaskResult> rows) =>
        KpiMath.Percentile(rows.Where(t => t.WallSeconds is not null).Select(t => t.WallSeconds!.Value), 0.5);

    private static double Mean(IReadOnlyList<int> values) => values.Count == 0 ? 0 : values.Average();

    private static double StdDev(IReadOnlyList<int> values)
    {
        if (values.Count < 2) return 0;
        var mean = values.Average();
        return Math.Sqrt(values.Sum(v => (v - mean) * (v - mean)) / (values.Count - 1));
    }

    [GeneratedRegex(@"\A(.+)-[0-9]+\z")]
    private static partial Regex GroupPattern();
}

/// <summary>
/// <c>--repeat N</c>: runs the task set N times. N = 1 keeps the single-run layout (everything in the output folder, no
/// summary files); N &gt; 1 runs into <c>run-&lt;k&gt;/</c> and writes <c>summary.json</c>/<c>summary.md</c> above them.
/// An interrupted run stops the series and is left out of the summary.
/// </summary>
public static class RepeatedRun
{
    public sealed record RunOnce(IReadOnlyList<TaskResult> Results, bool Cancelled);

    public sealed record Outcome(IReadOnlyList<IReadOnlyList<TaskResult>> Runs, bool Cancelled, RepeatSummaryData? Summary);

    public static string RunDir(string outDir, int repeat, int run) => repeat == 1 ? outDir : Path.Combine(outDir, $"run-{run}");

    /// <param name="runOnce">Runs the task set once into the given folder (1-based run number) and writes its own results there.</param>
    public static async Task<Outcome> ExecuteAsync(int repeat, string outDir, Func<string, int, Task<RunOnce>> runOnce,
        RunConditions? conditions, string header)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(repeat, 1);
        var completed = new List<IReadOnlyList<TaskResult>>();
        var cancelled = false;
        for (var k = 1; k <= repeat; k++)
        {
            var once = await runOnce(RunDir(outDir, repeat, k), k);
            if (once.Cancelled)
            {
                cancelled = true;
                if (repeat == 1) completed.Add(once.Results); // single run: its partial results are the run's report
                break;
            }
            completed.Add(once.Results);
        }
        if (repeat == 1) return new Outcome(completed, cancelled, null);

        var built = RepeatSummary.Build(completed);
        var summary = built with
        {
            Runs = built.Runs.Select(r => r with { Folder = $"run-{r.Run}" }).ToList(),
            Conditions = conditions,
            Cancelled = cancelled,
        };
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "summary.json"), RepeatSummary.ToJson(summary));
        File.WriteAllText(Path.Combine(outDir, "summary.md"), KpiReport.RenderRepeat(summary, header));
        return new Outcome(completed, cancelled, summary);
    }
}
