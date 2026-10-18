using System.ComponentModel;
using System.Globalization;
using System.IO.Abstractions;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Workspaces;
using Spectre.Console;
using Spectre.Console.Cli;

namespace PellyCoder.Cli.Commands;

public sealed class ReportSettings : CommandSettings
{
    [CommandOption("--since <DATE>")]
    [Description("이 날짜(yyyy-MM-dd) 이후의 실행만 집계")]
    public string? Since { get; init; }

    [CommandOption("--sanitize")]
    [Description("허용 목록 방식의 비식별 리포트(json·md)를 파일로 저장")]
    public bool Sanitize { get; init; }

    [CommandOption("--out <DIR>")]
    [Description("--sanitize 출력 폴더(기본: 현재 폴더)")]
    public string? Out { get; init; }

    public override ValidationResult Validate()
    {
        if (Since is not null && !DateOnly.TryParseExact(Since, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            return ValidationResult.Error("--since는 yyyy-MM-dd 형식이어야 합니다.");
        return ValidationResult.Success();
    }
}

/// <summary><c>pelly report</c>: aggregates the run summaries of the current workspace into KPI tables (PRD §2.2 order).</summary>
public sealed class ReportCommand(IFileSystem fs, IAnsiConsole console) : Command<ReportSettings>
{
    public override int Execute(CommandContext context, ReportSettings settings, CancellationToken cancellation)
    {
        // The workspace is the current directory; the placeholder file name is never read.
        var ws = WorkspaceLocator.Resolve(fs, fs.Path.Combine(fs.Directory.GetCurrentDirectory(), "pelly-report"));
        DateOnly? since = settings.Since is null ? null : DateOnly.ParseExact(settings.Since, "yyyy-MM-dd", CultureInfo.InvariantCulture);

        var history = RunHistory.Load(fs, ws, since);
        if (history.Runs.Count == 0)
        {
            console.MarkupLine(Markup.Escape($"실행 기록이 없습니다: {fs.Path.Combine(ws.PellyDir, "runs")}"));
            if (history.Unreadable > 0) console.MarkupLine(Markup.Escape($"읽을 수 없는 기록 {history.Unreadable}건은 건너뛰었습니다."));
            return ExitCodes.Success;
        }

        var d = RunReport.Build(history.Runs);
        if (settings.Sanitize) return WriteSanitized(history.Runs, d, settings.Out);
        var kinds = string.Join(", ", d.RunsByKind.Select(k => $"{k.Key} {k.Value}"));
        console.MarkupLine(Markup.Escape($"실행 {d.RunCount}건 ({kinds})" + (history.Unreadable > 0 ? $", 읽을 수 없어 건너뜀 {history.Unreadable}건" : "")));

        var kpi = new Table().AddColumns("지표", "값");
        kpi.AddRow("검증 불변 조건", "독립 검증 없음 — 벤치마크 실행기로 측정");
        kpi.AddRow("초기 응답 지연(TTFT)", Describe(d.Ttft, $" (TTFT {d.TtftSamples}건)"));
        kpi.AddRow("대기(Queue)", Describe(d.Queue, ""));
        kpi.AddRow("추론 예산 제어(Thinking)", $"think 최대 {d.ThinkMax} 토큰, 잘림 {d.ThinkTruncatedCount}건");
        kpi.AddRow("패치 포맷 파싱 성공률", d.Parse.Rate is { } r ? $"{r:P1} ({d.Parse.Ok}/{d.Parse.Total} 시도)" : "-");
        kpi.AddRow("최종 작업 성공률", $"{d.SuccessRate:P1} ({d.SuccessCount}/{d.RunCount}건, 종료 코드 0)");
        kpi.AddRow("단일 작업 완료 시간", Describe(d.TaskSeconds, " (단계 합)"));
        console.Write(kpi);

        Distribution("종료 코드", d.ExitCodes.Select(e => ($"{e.Key}", e.Value)));
        Distribution("실패 유형", d.FailureKinds.Select(e => (e.Key, e.Value)));
        Distribution("시도 수", d.AttemptDistribution.Select(e => ($"{e.Key}회", e.Value)));

        var phases = new Table().AddColumns("단계", "평균 시간(초)");
        foreach (var p in d.MeanPhaseSeconds) phases.AddRow(Markup.Escape(p.Key.ToString()), p.Value.ToString("F2", CultureInfo.InvariantCulture));
        console.Write(phases);
        return ExitCodes.Success;
    }

    private int WriteSanitized(IReadOnlyList<RunRecord> runs, RunReportData d, string? outDir)
    {
        var report = ReportSanitizer.Sanitize(runs, d);
        var dir = fs.Path.GetFullPath(outDir ?? fs.Directory.GetCurrentDirectory());
        var stem = fs.Path.Combine(dir, "pelly-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
        try
        {
            fs.Directory.CreateDirectory(dir);
            fs.File.WriteAllText(stem + ".json", report.ToJson());
            fs.File.WriteAllText(stem + ".md", report.ToMarkdown());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            console.MarkupLine(Markup.Escape($"리포트를 저장할 수 없습니다: {ex.Message}"));
            return ExitCodes.EnvError;
        }
        console.MarkupLine(Markup.Escape($"비식별 리포트를 저장했습니다: {stem}.json"));
        console.MarkupLine(Markup.Escape($"비식별 리포트를 저장했습니다: {stem}.md"));
        return ExitCodes.Success;
    }

    private void Distribution(string title, IEnumerable<(string Key, int Count)> rows)
    {
        var list = rows.ToList();
        if (list.Count == 0) return;
        var table = new Table().AddColumns(title, "건수");
        foreach (var (key, count) in list) table.AddRow(Markup.Escape(key), count.ToString(CultureInfo.InvariantCulture));
        console.Write(table);
    }

    private static string Describe(Spread? s, string suffix) =>
        s is null ? "-" : Markup.Escape($"중위 {s.Median.ToString("F2", CultureInfo.InvariantCulture)}초 / P90 {s.P90.ToString("F2", CultureInfo.InvariantCulture)}초{suffix}");
}
