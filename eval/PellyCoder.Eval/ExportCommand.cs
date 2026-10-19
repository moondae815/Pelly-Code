using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace PellyCoder.Eval;

/// <summary>
/// <c>export &lt;results folder&gt; --out &lt;folder&gt;</c>: writes a de-identified copy of a benchmark result for carry-out.
/// Rows keep every KPI field; free-text fields that can quote customer code, paths or addresses are dropped
/// (<see cref="DroppedFields"/>), <c>report.md</c> is re-rendered from the sanitized rows without the endpoint, and
/// <c>summary.json</c> (ids, outcomes and conditions only) is copied as is. Work copies and pelly run logs are never copied.
/// Exit 0 exported, 4 refused (missing input, unreadable results, non-empty <c>--out</c>).
/// </summary>
public static partial class ExportCommand
{
    /// <summary>The fields replaced in every row, as listed in <c>EXPORT.md</c>.</summary>
    internal static readonly string[] DroppedFields = ["reasons", "independent.detail", "skipReason"];

    internal const string GenericSkip = "생략";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static int Run(EvalArguments args, TextWriter output)
    {
        if (args.Positional.Count != 1 || args.Out is null)
        {
            output.WriteLine("사용법: dotnet run --project eval/PellyCoder.Eval -- export <결과 폴더> --out <폴더>");
            return 4;
        }
        var input = Path.GetFullPath(args.Positional[0]);
        var dest = Path.GetFullPath(args.Out);
        if (!Directory.Exists(input)) return Refuse(output, $"결과 폴더가 없습니다: {args.Positional[0]}");
        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
            return Refuse(output, $"--out 폴더가 비어 있지 않습니다: {args.Out}");

        var folderName = Path.GetFileName(input.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var summaryPath = Path.Combine(input, "summary.json");
        try
        {
            if (File.Exists(summaryPath)) return ExportRepeat(input, dest, folderName, summaryPath, output);
            if (File.Exists(Path.Combine(input, "results.json"))) return ExportSingle(input, dest, folderName, output);
            return Refuse(output, $"results.json도 summary.json도 없습니다: {args.Positional[0]}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return Refuse(output, $"반출하지 못했습니다: {ex.Message}");
        }
    }

    private static int ExportRepeat(string input, string dest, string name, string summaryPath, TextWriter output)
    {
        var summary = RepeatSummary.FromJson(File.ReadAllText(summaryPath));
        var runDirs = Directory.GetDirectories(input, "run-*").Where(d => File.Exists(Path.Combine(d, "results.json"))).OrderBy(d => d, StringComparer.Ordinal).ToList();
        if (runDirs.Count == 0) return Refuse(output, "summary.json은 있으나 run-<k>/results.json이 없습니다");
        var version = summary.Conditions?.PellyVersion ?? "알 수 없음";
        var model = summary.Conditions?.Model ?? "알 수 없음";

        Directory.CreateDirectory(dest);
        foreach (var runDir in runDirs)
            WriteRun(runDir, Path.Combine(dest, Path.GetFileName(runDir)), name, version, model);
        File.Copy(summaryPath, Path.Combine(dest, "summary.json"));
        var header = Header(name, version, model);
        File.WriteAllText(Path.Combine(dest, "summary.md"), KpiReport.RenderRepeat(summary, header));
        WriteReadme(dest, name, $"반복 실행 {runDirs.Count}회");
        output.WriteLine($"반출: {dest} (반복 실행 {runDirs.Count}회)");
        return 0;
    }

    private static int ExportSingle(string input, string dest, string name, TextWriter output)
    {
        var (version, model) = HeaderFacts(Path.Combine(input, "report.md"));
        Directory.CreateDirectory(dest);
        WriteRun(input, dest, name, version, model);
        WriteReadme(dest, name, "단일 실행");
        output.WriteLine($"반출: {dest} (단일 실행)");
        return 0;
    }

    private static void WriteRun(string runDir, string destDir, string name, string version, string model)
    {
        var rows = JsonSerializer.Deserialize<List<TaskResult>>(File.ReadAllText(Path.Combine(runDir, "results.json")), ReadOptions)
            ?? throw new JsonException("results.json이 비어 있습니다.");
        var clean = rows.Select(Sanitize).ToList();
        Directory.CreateDirectory(destDir);
        File.WriteAllText(Path.Combine(destDir, "results.json"), JsonSerializer.Serialize(clean, WriteOptions));
        File.WriteAllText(Path.Combine(destDir, "report.md"), KpiReport.Render(clean, Header(name, version, model)));
    }

    internal static TaskResult Sanitize(TaskResult t) => t with
    {
        Reasons = [],
        Independent = t.Independent is null ? null : t.Independent with { Detail = null },
        SkipReason = SanitizeSkip(t.SkipReason),
    };

    /// <summary><c>환경 오류(kind): detail</c> keeps the label up to the closing parenthesis; any other skip reason becomes <see cref="GenericSkip"/>.</summary>
    internal static string? SanitizeSkip(string? reason)
    {
        if (reason is null) return null;
        var m = EnvironmentLabel().Match(reason);
        return m.Success ? m.Value : GenericSkip;
    }

    private static string Header(string name, string version, string model) => $"* 반출본: {name} · pelly {version} · 모델 `{model}`";

    /// <summary>Pelly version and model from the first <c>report.md</c> header line; the endpoint after them is not read.</summary>
    private static (string Version, string Model) HeaderFacts(string reportPath)
    {
        if (!File.Exists(reportPath)) return ("알 수 없음", "알 수 없음");
        var m = HeaderFactsPattern().Match(File.ReadAllText(reportPath));
        return m.Success ? (m.Groups[1].Value, m.Groups[2].Value) : ("알 수 없음", "알 수 없음");
    }

    private static void WriteReadme(string dest, string name, string kind)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# 반출용 비식별 벤치마크 결과").AppendLine();
        sb.AppendLine($"* 원본 폴더: {name} ({kind})");
        sb.AppendLine("* 유지: 작업 id·종류·판정·종료 코드·시도 수·실패 유형·빌드 판정·시간·추론 토큰·편집 결과·형식 오류·독립 검증 상태 등 KPI 필드 전부");
        sb.AppendLine("* 지운 필드(행마다):");
        sb.AppendLine("  * `reasons` → 빈 목록");
        sb.AppendLine("  * `independent.detail` → null");
        sb.AppendLine($"  * `skipReason` → `환경 오류(<종류>)`처럼 앞부분만, 그 밖의 생략 사유는 `{GenericSkip}`");
        sb.AppendLine("* `report.md`·`summary.md`는 비식별 결과로 다시 그렸고 접속 주소를 넣지 않았습니다.");
        sb.AppendLine("* `summary.json`은 id·결과·조건만 담고 있어 그대로 복사했습니다.");
        sb.AppendLine("* 복사하지 않음: 작업 복사본(`work/`)과 `.pelly/runs` (pelly 실행 기록은 `pelly report --sanitize`로 따로 반출).");
        File.WriteAllText(Path.Combine(dest, "EXPORT.md"), sb.ToString());
    }

    private static int Refuse(TextWriter output, string message)
    {
        output.WriteLine("반출하지 않습니다 — " + message);
        return 4;
    }

    [GeneratedRegex(@"\A환경 오류\([^)\r\n]*\)")]
    private static partial Regex EnvironmentLabel();

    [GeneratedRegex(@"pelly (\S+) · 모델 `([^`]+)`")]
    private static partial Regex HeaderFactsPattern();
}
