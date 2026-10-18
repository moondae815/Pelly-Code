using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
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
        ["Ollama 서버"] = ["ollamaVersion", "latencyMs"],
        ["모델"] = ["loaded"],
        ["렌더러"] = [],
        ["템플릿 일치"] = [],
        ["토크나이저 일치"] = ["match"],
        ["응답 시간"] = ["ttftMs"],
        ["pelly"] = ["pellyVersion", "tokenizerHash"],
        ["설정"] = [],
        [".NET SDK"] = ["dotnetVersion"],
        ["MSBuild (레거시)"] = ["found"],
        [TargetingPackCheck.Name] = ["frameworkVersion", "installed"],
        ["서버 설정 확인 필요"] = [],
        ["SQL 연결"] = ["canViewDefinition", "serverMajor"],
    };

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // every string is whitelisted
        Converters = { new JsonStringEnumConverter() },
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
                    if (c.Facts.TryGetValue(key, out var value) && value is not null && ValuePattern.IsMatch(value))
                        facts[key] = value;
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
