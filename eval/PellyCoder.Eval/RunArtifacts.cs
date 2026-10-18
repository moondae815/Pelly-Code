using System.Text.Json;

namespace PellyCoder.Eval;

/// <summary>What one run left in <c>.pelly/runs/&lt;runId&gt;/</c> (summary.json and events.jsonl, see RunLogger).</summary>
public sealed record RunArtifacts(int Attempts, double TtftSeconds, int ThinkTokens, bool ThinkTruncated, string? FailureKind,
    IReadOnlyList<string> EditOutcomes)
{
    public static RunArtifacts Empty { get; } = new(0, 0, 0, false, null, []);

    /// <summary>The newest run folder under <paramref name="pellyDir"/>, or <see cref="Empty"/> when there is none.</summary>
    public static RunArtifacts Read(string pellyDir)
    {
        var runs = Path.Combine(pellyDir, "runs");
        if (!Directory.Exists(runs)) return Empty;
        var dir = Directory.GetDirectories(runs).OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal).LastOrDefault();
        if (dir is null) return Empty;

        var events = Path.Combine(dir, "events.jsonl");
        var edits = File.Exists(events) ? ParseEditOutcomes(File.ReadLines(events)) : [];
        var summaryPath = Path.Combine(dir, "summary.json");
        if (!File.Exists(summaryPath)) return Empty with { EditOutcomes = edits };

        using var doc = JsonDocument.Parse(File.ReadAllText(summaryPath));
        var s = doc.RootElement;
        return new RunArtifacts(
            Int(s, "attempts"),
            s.TryGetProperty("ttftSeconds", out var ttft) && ttft.ValueKind == JsonValueKind.Number ? ttft.GetDouble() : 0,
            Int(s, "thinkTokens"),
            s.TryGetProperty("thinkTruncated", out var tr) && tr.ValueKind == JsonValueKind.True,
            s.TryGetProperty("failureKind", out var fk) && fk.ValueKind == JsonValueKind.String ? fk.GetString() : null,
            edits);
    }

    /// <summary>The <c>outcome</c> of every <c>events.jsonl</c> line whose <c>phase</c> is <c>Edit</c>, in order.</summary>
    public static IReadOnlyList<string> ParseEditOutcomes(IEnumerable<string> eventLines)
    {
        var result = new List<string>();
        foreach (var line in eventLines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            using var doc = JsonDocument.Parse(line);
            var e = doc.RootElement;
            if (e.TryGetProperty("phase", out var phase) && phase.GetString() == "Edit"
                && e.TryGetProperty("outcome", out var outcome) && outcome.GetString() is { } o)
                result.Add(o);
        }
        return result;
    }

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
}
