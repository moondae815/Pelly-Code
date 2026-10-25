using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.Json.Serialization;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Core.Reporting;

/// <summary>An apply summary with its start time, parsed from the apply ID (<c>apply-</c> + RunId stamp, local time).</summary>
public sealed record ApplyRecord(ApplySummary Summary, DateTimeOffset StartedUtc);

/// <summary>Reads <c>.pelly/runs/apply-*/apply.json</c> (nothing else). Unreadable files are counted and skipped.</summary>
public static class ApplyHistory
{
    private const string Prefix = "apply-";
    private const string StampFormat = "yyyyMMdd-HHmmss";

    // Same naming/enum handling as the writer in SpecRunner.
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    public static (IReadOnlyList<ApplyRecord> Applies, int Unreadable) Load(IFileSystem fs, Workspace ws, DateOnly? since)
    {
        var runsDir = fs.Path.Combine(ws.PellyDir, "runs");
        if (!fs.Directory.Exists(runsDir)) return ([], 0);

        var applies = new List<ApplyRecord>();
        var unreadable = 0;
        foreach (var dir in fs.Directory.GetDirectories(runsDir).Order(StringComparer.Ordinal))
        {
            var file = fs.Path.Combine(dir, "apply.json");
            if (!fs.File.Exists(file)) continue; // a task run, or an apply still in progress
            var record = TryRead(fs, file);
            if (record is null) { unreadable++; continue; }
            if (since is { } s && DateOnly.FromDateTime(record.StartedUtc.ToLocalTime().DateTime) < s) continue;
            applies.Add(record);
        }
        return (applies, unreadable);
    }

    private static ApplyRecord? TryRead(IFileSystem fs, string file)
    {
        try
        {
            var summary = JsonSerializer.Deserialize<ApplySummary>(fs.File.ReadAllText(file), Json);
            if (summary is null || summary.ApplyId is null || summary.Tasks is null || summary.Tasks.Any(t => t is null))
                return null;
            return TryParseStart(summary.ApplyId) is { } started ? new ApplyRecord(summary, started) : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static DateTimeOffset? TryParseStart(string applyId)
    {
        if (!applyId.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        var stamp = applyId[Prefix.Length..];
        if (stamp.Length < StampFormat.Length
            || !DateTime.TryParseExact(stamp[..StampFormat.Length], StampFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            return null;
        return new DateTimeOffset(DateTime.SpecifyKind(local, DateTimeKind.Local)).ToUniversalTime();
    }
}
