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
    // A real option so strict parsing accepts it; Program.cs reads the raw arguments (ExitPause), not this value.
    [CommandOption("--pause-on-exit")]
    [Description("끝난 뒤 키 입력을 기다림 (IDE 외부 도구 창용)")]
    public bool PauseOnExit { get; init; }

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
        if (Out is not null && !Sanitize)
            return ValidationResult.Error("--out은 --sanitize와 함께 쓰세요.");
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
        var (applies, applyUnreadable) = ApplyHistory.Load(fs, ws, since);
        var unreadable = history.Unreadable + applyUnreadable;
        // Spec KPIs come from apply.json alone: an apply history without task runs still reports.
        if (history.Runs.Count == 0 && applies.Count == 0)
        {
            console.MarkupLine(Markup.Escape($"실행 기록이 없습니다: {fs.Path.Combine(ws.PellyDir, "runs")}"));
            if (settings.Sanitize) console.MarkupLine("비식별 리포트를 쓰지 않았습니다 (실행 기록 없음)");
            if (unreadable > 0) console.MarkupLine(Markup.Escape($"읽을 수 없는 기록 {unreadable}건은 건너뛰었습니다."));
            return ExitCodes.Success;
        }

        var d = RunReport.Build(history.Runs);
        var applyData = applies.Count == 0 ? null : ApplyReport.Build(applies);
        if (settings.Sanitize) return WriteSanitized(history.Runs, d, applyData, settings.Out);
        if (history.Runs.Count == 0)
        {
            console.MarkupLine(Markup.Escape($"작업 기록이 없습니다: {fs.Path.Combine(ws.PellyDir, "runs")}"));
            if (unreadable > 0) console.MarkupLine(Markup.Escape($"읽을 수 없어 건너뜀 {unreadable}건"));
            SpecTable(applyData!);
            return ExitCodes.Success;
        }
        var kinds = string.Join(", ", d.RunsByKind.Select(k => $"{k.Key} {k.Value}"));
        console.MarkupLine(Markup.Escape($"실행 {d.RunCount}건 ({kinds})" + (unreadable > 0 ? $", 읽을 수 없어 건너뜀 {unreadable}건" : "")));

        var kpi = new Table().AddColumns("지표", "값");
        kpi.AddRow("검증 불변 조건", "독립 검증 없음 — 벤치마크 실행기로 측정");
        kpi.AddRow("초기 응답 지연(TTFT)", Describe(d.Ttft, $" (TTFT {d.TtftSamples}건)"));
        kpi.AddRow("대기(Queue)", Describe(d.Queue, ""));
        kpi.AddRow("추론 예산 제어(Thinking)", $"think 최대 {d.ThinkMax} 토큰, 잘림 {d.ThinkTruncatedCount}건");
        kpi.AddRow("패치 포맷 파싱 성공률", d.Parse.Rate is { } r ? $"{r:P1} ({d.Parse.Ok}/{d.Parse.Total} 시도)" : "-");
        kpi.AddRow("최종 작업 성공률", $"{d.SuccessRate:P1} ({d.SuccessCount}/{d.RunCount}건, 종료 코드 0)");
        kpi.AddRow("단일 작업 완료 시간", Describe(d.TaskSeconds, " (단계 합)"));
        if (d.TestFirst is { } tf)
        {
            kpi.AddRow("수용 기준 충족률", Markup.Escape(RatioText(tf.Met, tf.Judged)));
            kpi.AddRow("RED 확인 경고율", Markup.Escape(RatioText(tf.RedWarned, tf.RedChecked)));
        }
        console.Write(kpi);

        Distribution("종료 코드", d.ExitCodes.Select(e => ($"{e.Key}", e.Value)));
        Distribution("실패 유형", d.FailureKinds.Select(e => (e.Key, e.Value)));
        Distribution("시도 수", d.AttemptDistribution.Select(e => ($"{e.Key}회", e.Value)));

        var phases = new Table().AddColumns("단계", "평균 시간(초)");
        foreach (var p in d.MeanPhaseSeconds) phases.AddRow(Markup.Escape(p.Key.ToString()), p.Value.ToString("F2", CultureInfo.InvariantCulture));
        console.Write(phases);
        if (applyData is not null) SpecTable(applyData);
        return ExitCodes.Success;
    }

    private void SpecTable(ApplyReportData a)
    {
        var table = new Table().AddColumns("명세", "값");
        table.AddRow("명세 완료율", Markup.Escape(RatioText(a.Completion, $"건, 이어서 하기 {a.Resumed}건 제외")));
        table.AddRow("명세 완료율(작업 3~8개)", Markup.Escape(RatioText(a.Completion3To8, "건")));
        table.AddRow("그룹 빌드 통과율", Markup.Escape(RatioText(a.GroupBuild, $"그룹, 검증 불가 {a.GroupsUnverifiable}그룹 제외")));
        console.Write(table);
    }

    private static string RatioText(int ok, int total) =>
        total == 0 ? "-" : $"{(100.0 * ok / total).ToString("F1", CultureInfo.InvariantCulture)}% ({ok}/{total})";

    private static string RatioText(Ratio r, string unit) =>
        r.Rate is { } rate ? $"{(rate * 100).ToString("F1", CultureInfo.InvariantCulture)}% ({r.Ok}/{r.Total}{unit})" : "-";

    private int WriteSanitized(IReadOnlyList<RunRecord> runs, RunReportData d, ApplyReportData? apply, string? outDir)
    {
        var report = ReportSanitizer.Sanitize(runs, d, apply);
        string jsonPath, mdPath;
        try
        {
            var dir = fs.Path.GetFullPath(outDir ?? fs.Directory.GetCurrentDirectory());
            fs.Directory.CreateDirectory(dir);
            var stem = fs.Path.Combine(dir, "pelly-report-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
            // Never overwrite an earlier report from the same second.
            var suffix = "";
            for (var n = 2; fs.File.Exists(stem + suffix + ".json") || fs.File.Exists(stem + suffix + ".md"); n++)
                suffix = "-" + n.ToString(CultureInfo.InvariantCulture);
            jsonPath = stem + suffix + ".json";
            mdPath = stem + suffix + ".md";
            fs.File.WriteAllText(jsonPath, report.ToJson());
            fs.File.WriteAllText(mdPath, report.ToMarkdown());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            console.MarkupLine(Markup.Escape($"리포트를 저장할 수 없습니다: {ex.Message}"));
            return ExitCodes.EnvError;
        }
        console.MarkupLine(Markup.Escape($"비식별 리포트를 저장했습니다: {jsonPath}"));
        console.MarkupLine(Markup.Escape($"비식별 리포트를 저장했습니다: {mdPath}"));
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
