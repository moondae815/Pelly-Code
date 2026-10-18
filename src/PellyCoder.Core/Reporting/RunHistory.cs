using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Reporting;

/// <summary>A run summary with its start time, parsed from the RunId (<c>yyyyMMdd-HHmmss-xxxx</c>, local time).</summary>
public sealed record RunRecord(RunSummary Summary, DateTimeOffset StartedUtc);

public sealed record RunHistoryResult(IReadOnlyList<RunRecord> Runs, int Unreadable);

/// <summary>Reads <c>.pelly/runs/*/summary.json</c> (and nothing else, design §8.12). Unreadable summaries are counted and skipped.</summary>
public static class RunHistory
{
    private const string StampFormat = "yyyyMMdd-HHmmss";

    // Same naming/enum handling as RunLogger's summary writer.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static RunHistoryResult Load(IFileSystem fs, Workspace ws, DateOnly? since)
    {
        var runsDir = fs.Path.Combine(ws.PellyDir, "runs");
        if (!fs.Directory.Exists(runsDir)) return new RunHistoryResult([], 0);

        var runs = new List<RunRecord>();
        var unreadable = 0;
        foreach (var dir in fs.Directory.GetDirectories(runsDir).Order(StringComparer.Ordinal))
        {
            var file = fs.Path.Combine(dir, "summary.json");
            if (!fs.File.Exists(file)) continue; // a run still in progress or pruned halfway: not a summary
            var record = TryRead(fs, file);
            if (record is null) { unreadable++; continue; }
            if (since is { } s && DateOnly.FromDateTime(record.StartedUtc.ToLocalTime().DateTime) < s) continue;
            runs.Add(record);
        }
        return new RunHistoryResult(runs, unreadable);
    }

    private static RunRecord? TryRead(IFileSystem fs, string file)
    {
        try
        {
            var summary = JsonSerializer.Deserialize<RunSummary>(fs.File.ReadAllText(file), Json);
            if (summary is null || summary.RunId is null || summary.PhaseSeconds is null
                || summary.MatchStages is null || summary.ErrorCodes is null)
                return null;
            return TryParseStart(summary.RunId) is { } started ? new RunRecord(summary, started) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static DateTimeOffset? TryParseStart(string runId)
    {
        if (runId.Length < StampFormat.Length
            || !DateTime.TryParseExact(runId[..StampFormat.Length], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUniversalTime();
    }
}
