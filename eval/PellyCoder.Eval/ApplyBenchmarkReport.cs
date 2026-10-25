using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Eval;

/// <summary>
/// <c>apply-results.json</c> (every case run) and <c>apply-summary.md</c>. The rates are <see cref="ApplyReport.Build"/>'s over
/// the <c>apply.json</c> each run's work copy holds, read by <see cref="ApplyHistory"/>: the same counting as <c>pelly report</c>.
/// </summary>
public static class ApplyBenchmarkReport
{
    public const string ResultsFile = "apply-results.json";
    public const string SummaryFile = "apply-summary.md";

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Writes both files to <paramref name="outDir"/> and returns the summary.</summary>
    public static string Write(IFileSystem fs, string outDir, IReadOnlyList<ApplyCaseResult> results, string header)
    {
        var records = new List<ApplyRecord>();
        var missing = 0;
        var unreadable = 0;
        foreach (var r in results.Where(r => r.Outcome != ApplyBenchmark.Skipped))
        {
            var corpus = ApplyBenchmark.CorpusOf(ApplyBenchmark.CaseDir(outDir, r.Id, r.Repeat));
            var (applies, bad) = ApplyHistory.Load(fs, new Workspace(corpus, Path.Combine(corpus, ".pelly")), null);
            records.AddRange(applies);
            unreadable += bad;
            if (applies.Count == 0) missing++;
        }

        var md = Render(results, ApplyReport.Build(records), header, missing, unreadable);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, ResultsFile), JsonSerializer.Serialize(results, Json));
        File.WriteAllText(Path.Combine(outDir, SummaryFile), md);
        return md;
    }

    private static string Render(IReadOnlyList<ApplyCaseResult> results, ApplyReportData data, string header, int missing, int unreadable)
    {
        var sb = new StringBuilder();
        sb.Append("# pelly-eval apply 결과\n\n").Append(header).Append("\n\n## 요약\n");
        sb.Append($"* 명세 완료율: {Ratio(data.Completion)}\n");
        sb.Append($"* 명세 완료율(작업 3~8개): {Ratio(data.Completion3To8)}\n");
        sb.Append($"* 그룹 빌드 통과율: {Ratio(data.GroupBuild)}" + (data.GroupsUnverifiable > 0 ? $" · 그룹 빌드 검증 불가 {data.GroupsUnverifiable}건" : "") + "\n");

        var exit0 = results.Where(r => r.Outcome != ApplyBenchmark.Skipped && r.ExitCode == 0).ToList();
        var noCs = exit0.Count(r => r.IndependentBuildOk is null && r.IndependentDetail == ApplyBenchmark.NoCsChange);
        sb.Append($"* 독립 빌드(종료 0 사례 {exit0.Count}건): 통과 {exit0.Count(r => r.IndependentBuildOk == true)} · "
            + $"위반 {exit0.Count(r => r.IndependentBuildOk == false)} · 독립 검증 불가 {exit0.Count(r => r.IndependentBuildOk is null) - noCs} · "
            + $"C# 변경 없음 {noCs}\n");
        sb.Append($"* 사례 실행 {results.Count}건: 완료 {Count(results, ApplyBenchmark.Completed)} · 실패 {Count(results, ApplyBenchmark.Failed)} · "
            + $"실행기 오류 {Count(results, ApplyBenchmark.Errored)} · 건너뜀 {Count(results, ApplyBenchmark.Skipped)}\n");
        if (missing > 0 || unreadable > 0)
            sb.Append($"* 기록(apply.json)이 없는 실행 {missing}건 · 읽지 못한 기록 {unreadable}건 — 완료율 분모에 들지 않습니다\n");

        sb.Append("\n## 건너뛴 사례\n");
        var skipped = results.Where(r => r.Outcome == ApplyBenchmark.Skipped).ToList();
        if (skipped.Count == 0) sb.Append("- 없음\n");
        foreach (var r in skipped) sb.Append($"- {r.Id} (회차 {r.Repeat}): {r.SkipReason}\n");

        sb.Append("\n## 사례별 결과\n| 사례 | 회차 | 결과 | 종료 | 작업 | 보관 | 독립 빌드 | 시간(초) |\n| :--- | ---: | :--- | ---: | ---: | :--- | :--- | ---: |\n");
        foreach (var r in results)
        {
            var skippedRow = r.Outcome == ApplyBenchmark.Skipped;
            var independent = r.IndependentBuildOk switch
            {
                true => "통과",
                false => $"위반: {r.IndependentDetail}",
                null => r.IndependentDetail ?? r.Error ?? "-",
            };
            sb.Append($"| {r.Id} | {r.Repeat} | {r.Outcome} | {(skippedRow ? "-" : r.ExitCode.ToString(CultureInfo.InvariantCulture))} | "
                + $"{r.TaskCount} | {(skippedRow ? "-" : r.Archived ? "예" : "아니오")} | {Cell(independent)} | "
                + $"{r.Seconds.ToString("0.0", CultureInfo.InvariantCulture)} |\n");
        }
        return sb.ToString();
    }

    private static int Count(IReadOnlyList<ApplyCaseResult> results, string outcome) => results.Count(r => r.Outcome == outcome);

    private static string Ratio(Ratio r) => r.Rate is { } rate
        ? $"{(rate * 100).ToString("0.0", CultureInfo.InvariantCulture)}% ({r.Ok}/{r.Total}건)"
        : $"판정 불가 ({r.Total}건)";

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
}
