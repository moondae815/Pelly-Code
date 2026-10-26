using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Workspaces;

namespace PellyCoder.Eval;

/// <summary>
/// <c>apply-results.json</c> (every case run) and <c>apply-summary.md</c>. The rates are <see cref="ApplyReport.Build"/>'s over
/// the <c>apply.json</c> each run's work copy holds, read by <see cref="ApplyHistory"/>: the same counting as <c>pelly report</c>.
/// A run that ended without writing one (spec check error, refusal) and is neither skipped nor errored is added on top to the
/// completion denominators (by its task count for the 3~8 row) as a non-completion, so a refusal never raises the rate.
/// Runs with acceptance criteria add a test-first table (the criteria counts of <see cref="ApplyReport.Build"/> plus the test judge
/// mismatches of the independent re-run) and a per-case criteria table; a run set with an unapproved case gets a preliminary title.
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
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Appended to the title when a case of the run set is not approved yet.</summary>
    public const string PreliminarySuffix = " — 예비(사람 승인 전)";

    /// <summary>Writes both files to <paramref name="outDir"/> and returns the summary; <paramref name="cases"/> (the run set) give the approval and expected criteria.</summary>
    public static string Write(IFileSystem fs, string outDir, IReadOnlyList<ApplyCaseResult> results, string header,
        IReadOnlyList<ApplyCase>? cases = null)
    {
        var records = new List<ApplyRecord>();
        var missing = new List<ApplyCaseResult>();
        var unreadable = 0;
        foreach (var r in results.Where(r => r.Outcome != ApplyBenchmark.Skipped))
        {
            var corpus = ApplyBenchmark.CorpusOf(ApplyBenchmark.CaseDir(outDir, r.Id, r.Repeat));
            var (applies, bad) = ApplyHistory.Load(fs, new Workspace(corpus, Path.Combine(corpus, ".pelly")), null);
            records.AddRange(applies);
            unreadable += bad;
            if (applies.Count == 0 && r.Outcome != ApplyBenchmark.Errored) missing.Add(r);
        }

        var md = Render(results, ApplyReport.Build(records), header, missing, unreadable, cases ?? []);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, ResultsFile), JsonSerializer.Serialize(results, Json));
        File.WriteAllText(Path.Combine(outDir, SummaryFile), md);
        return md;
    }

    private static string Render(IReadOnlyList<ApplyCaseResult> results, ApplyReportData data, string header, IReadOnlyList<ApplyCaseResult> missing,
        int unreadable, IReadOnlyList<ApplyCase> cases)
    {
        string Missing(int n) => n > 0 ? $" — apply.json 없는 실행 {n}건을 미완료로 포함" : "";
        var missing3To8 = missing.Count(r => r.TaskCount is >= 3 and <= 8);
        var sb = new StringBuilder();
        sb.Append("# pelly-eval apply 결과").Append(cases.Any(c => !c.Approved) ? PreliminarySuffix : "").Append("\n\n").Append(header).Append("\n\n## 요약\n");
        sb.Append($"* 명세 완료율: {Ratio(data.Completion with { Total = data.Completion.Total + missing.Count })}{Missing(missing.Count)}\n");
        sb.Append($"* 명세 완료율(작업 3~8개): {Ratio(data.Completion3To8 with { Total = data.Completion3To8.Total + missing3To8 })}{Missing(missing3To8)}\n");
        sb.Append($"* 그룹 빌드 통과율: {Ratio(data.GroupBuild)}" + (data.GroupsUnverifiable > 0 ? $" · 그룹 빌드 검증 불가 {data.GroupsUnverifiable}건" : "") + "\n");

        var exit0 = results.Where(r => r.Outcome != ApplyBenchmark.Skipped && r.ExitCode == 0).ToList();
        var noCs = exit0.Count(r => r.IndependentBuildOk is null && r.IndependentDetail == ApplyBenchmark.NoCsChange);
        sb.Append($"* 독립 빌드(종료 0 사례 {exit0.Count}건): 통과 {exit0.Count(r => r.IndependentBuildOk == true)} · "
            + $"위반 {exit0.Count(r => r.IndependentBuildOk == false)} · 독립 검증 불가 {exit0.Count(r => r.IndependentBuildOk is null) - noCs} · "
            + $"C# 변경 없음 {noCs}\n");
        sb.Append($"* 사례 실행 {results.Count}건: 완료 {Count(results, ApplyBenchmark.Completed)} · 실패 {Count(results, ApplyBenchmark.Failed)} · "
            + $"실행기 오류 {Count(results, ApplyBenchmark.Errored)} · 건너뜀 {Count(results, ApplyBenchmark.Skipped)}\n");
        if (unreadable > 0)
            sb.Append($"* 읽지 못한 기록(apply.json) {unreadable}건 — 그 실행은 기록 없는 실행으로 미완료에 셉니다\n");

        var testFirst = data.Criteria is not null || results.Any(r => r.Criteria is not null || r.TestChecks is not null);
        if (testFirst)
        {
            var ac = data.Criteria ?? new AcCounts(0, 0, 0, 0, 0, 0);
            sb.Append("\n## 테스트 우선\n| 지표 | 값 |\n| :--- | :--- |\n");
            sb.Append($"| 수용 기준 충족률 | {Ratio(new Ratio(ac.Met, ac.Judged))} |\n");
            sb.Append($"| RED 확인 경고율 | {Ratio(new Ratio(ac.RedWarned, ac.RedChecked))} |\n");
            sb.Append($"| 테스트로 검증되지 않은 수용 기준 | {N(ac.Unverified)}건 |\n");
            sb.Append($"| breaks로 뺀 테스트 | {N(ac.BreaksExcluded)}건 |\n");
            sb.Append($"| 테스트 판정 불일치 | {N(results.Sum(r => r.TestJudgeMismatches))}건 |\n");
        }

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
        if (testFirst) CriteriaTable(sb, results, cases);
        return sb.ToString();
    }

    /// <summary>Per run, every criterion of the case's expected.json and of the run's apply.json: expected vs actual, and the re-run.</summary>
    private static void CriteriaTable(StringBuilder sb, IReadOnlyList<ApplyCaseResult> results, IReadOnlyList<ApplyCase> cases)
    {
        var expectedOf = cases.GroupBy(c => c.Id, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Expected ?? [], StringComparer.Ordinal);
        sb.Append("\n## 사례별 수용 기준\n| 사례 | 회차 | AC | 기대 RED/결과 | 실제 RED/결과 | 독립 테스트 |\n| :--- | ---: | :--- | :--- | :--- | :--- |\n");
        foreach (var r in results.Where(r => r.Outcome != ApplyBenchmark.Skipped))
        {
            var expected = expectedOf.GetValueOrDefault(r.Id) ?? [];
            var actual = r.Criteria ?? [];
            var ids = expected.Select(e => e.AcId).Concat(actual.Select(a => a.AcId)).Distinct(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                var e = expected.FirstOrDefault(x => x.AcId == id);
                var a = actual.FirstOrDefault(x => x.AcId == id);
                var check = r.TestChecks?.FirstOrDefault(x => x.AcId == id);
                var judged = check is null ? "-" : check.Detail is { } detail ? $"{check.Verdict}: {detail}" : check.Verdict;
                sb.Append($"| {r.Id} | {r.Repeat} | {Cell(id)} | {(e is null ? "-" : Cell($"{e.Red}/{e.Outcome}"))} | "
                    + $"{(a is null ? "-" : $"{a.Red}/{a.Outcome}")} | {Cell(judged)} |\n");
            }
        }
    }

    private static string N(int v) => v.ToString(CultureInfo.InvariantCulture);

    private static int Count(IReadOnlyList<ApplyCaseResult> results, string outcome) => results.Count(r => r.Outcome == outcome);

    private static string Ratio(Ratio r) => r.Rate is { } rate
        ? $"{(rate * 100).ToString("0.0", CultureInfo.InvariantCulture)}% ({r.Ok}/{r.Total}건)"
        : $"판정 불가 ({r.Total}건)";

    private static string Cell(string text) => text.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal);
}
