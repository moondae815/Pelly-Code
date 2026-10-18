using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PellyCoder.Core.Doctor;

/// <summary>
/// Doctor output that can leave a closed network (D11, R9). Whitelist only: known row names, the status, and per row
/// the fixed fact keys whose values are short tokens. <c>Detail</c> and <c>Action</c> are never read.
/// </summary>
public static partial class DoctorSanitizer
{
    private const string Other = "기타";

    private static readonly IReadOnlyDictionary<string, string[]> Rows = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        [DoctorFacts.OllamaServer] = [DoctorFacts.OllamaVersion, DoctorFacts.LatencyMs],
        [DoctorFacts.Model] = [DoctorFacts.Loaded],
        [DoctorFacts.Renderer] = [],
        [DoctorFacts.TemplateMatch] = [],
        [DoctorFacts.TokenizerMatch] = [DoctorFacts.Match],
        [DoctorFacts.Latency] = [DoctorFacts.TtftMs],
        [DoctorFacts.Pelly] = [DoctorFacts.PellyVersion, DoctorFacts.TokenizerHash],
        [DoctorFacts.Settings] = [],
        [DoctorFacts.DotnetSdk] = [DoctorFacts.DotnetVersion],
        [DoctorFacts.MsBuild] = [DoctorFacts.Found],
        [DoctorFacts.TargetingPack] = [DoctorFacts.FrameworkVersion, DoctorFacts.Installed],
        [DoctorFacts.ServerSettings] = [],
        [DoctorFacts.Sql] = [DoctorFacts.CanViewDefinition, DoctorFacts.ServerMajor],
    };

    /// <summary>Version-valued facts follow the run report's version rule: a value outside it is written as <c>invalid</c>.</summary>
    private static readonly HashSet<string> VersionKeys =
        [DoctorFacts.OllamaVersion, DoctorFacts.DotnetVersion, DoctorFacts.PellyVersion, DoctorFacts.FrameworkVersion];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // every string is whitelisted
    };

    private sealed record Row(string Name, string Status, IReadOnlyDictionary<string, string> Facts);

    public static string ToJson(IReadOnlyList<DoctorCheck> checks) =>
        JsonSerializer.Serialize(new { checks = Sanitize(checks) }, Options);

    public static string ToMarkdown(IReadOnlyList<DoctorCheck> checks)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Pelly doctor 비식별 결과").AppendLine();
        sb.AppendLine("| 점검 | 상태 | 값 |").AppendLine("| --- | --- | --- |");
        foreach (var r in Sanitize(checks))
        {
            var facts = string.Join(", ", r.Facts.Select(f => $"{f.Key}={f.Value}"));
            sb.AppendLine($"| {r.Name} | {r.Status} | {facts} |");
        }
        return sb.ToString();
    }

    private static List<Row> Sanitize(IReadOnlyList<DoctorCheck> checks)
    {
        var rows = new List<Row>();
        foreach (var c in checks)
        {
            var known = Rows.TryGetValue(c.Name, out var keys);
            var facts = new SortedDictionary<string, string>(StringComparer.Ordinal);
            if (known && c.Facts is not null)
            {
                foreach (var key in keys!)
                {
                    if (!c.Facts.TryGetValue(key, out var value) || string.IsNullOrEmpty(value)) continue;
                    if (VersionKeys.Contains(key)) facts[key] = Reporting.ReportSanitizer.Version(value);
                    else if (ValuePattern.IsMatch(value)) facts[key] = value;
                }
            }
            var status = Enum.IsDefined(c.Status) ? c.Status.ToString() : nameof(DoctorStatus.Fail);
            rows.Add(new Row(known ? c.Name : Other, status, facts));
        }
        return rows;
    }

    [GeneratedRegex(@"\A[0-9A-Za-z.:_+\-]{0,40}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ValuePattern { get; }
}
