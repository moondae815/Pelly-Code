using System.Text.Json;

namespace PellyCoder.Eval;

/// <summary>What one run left in <c>.pelly/runs/&lt;runId&gt;/</c> (summary.json and events.jsonl, see RunLogger).</summary>
/// <param name="TestFirst">summary.json's <c>testFirst</c> (a run with named tests); null when the run wrote none.</param>
public sealed record RunArtifacts(int Attempts, double TtftSeconds, int ThinkTokens, bool ThinkTruncated, string? FailureKind,
    IReadOnlyList<string> EditOutcomes, int FormatErrors = 0, TestFirstResult? TestFirst = null)
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
            edits,
            Int(s, "formatErrors"),
            s.TryGetProperty("testFirst", out var tf) && tf.ValueKind == JsonValueKind.Object ? TestFirstOf(tf) : null);
    }

    private static TestFirstResult TestFirstOf(JsonElement e) =>
        new(Str(e, "red"), Str(e, "green"), Str(e, "firstAttemptGreen"), Int(e, "regressions"), Int(e, "breaksExcluded"));

    /// <summary>The string value, or <c>NotChecked</c> (the enums' default) when it is missing.</summary>
    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "NotChecked";

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
