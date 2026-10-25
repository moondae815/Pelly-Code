using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using PellyCoder.Core.Fsm;
using PellyCoder.Core.Logging;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Reporting;

public sealed record SanitizedRun(
    int Index, string Kind, string ProjectStyle, int ExitCode, int Attempts,
    IReadOnlyDictionary<string, double> PhaseSeconds, double TtftSeconds, double QueueSeconds,
    string ThinkMode, int ThinkTokens, bool ThinkTruncated,
    int InputTokensLocal, int? InputTokensServer, int? InputTokensCached,
    IReadOnlyList<string> MatchStages, IReadOnlyList<string> ErrorCodes, int DroppedErrorCodes, int FormatErrors,
    string? FailureKind, string PellyVersion, string? OllamaVersion, string PromptVersion);

/// <summary>The spec KPI as six plain numbers: completed/total, completed/total for 3 to 8 tasks, groups passed/total.</summary>
public sealed record ApplyCounts(int Completed, int CompletionTotal, int Completed3To8, int Completion3To8Total, int GroupPassed, int GroupTotal);

public sealed record SanitizedAggregate(
    int RunCount, IReadOnlyDictionary<string, int> RunsByKind, int SuccessCount, double SuccessRate,
    IReadOnlyDictionary<string, int> ExitCodes,
    Spread? Ttft, int TtftSamples, Spread? Queue, Spread? TaskSeconds,
    int ParseOk, int ParseTotal, double? ParseRate,
    int ThinkMax, int ThinkTruncatedCount,
    IReadOnlyDictionary<string, int> FailureKinds,
    IReadOnlyDictionary<string, double> MeanPhaseSeconds,
    IReadOnlyDictionary<string, int> AttemptDistribution,
    ApplyCounts? Apply = null);

/// <summary>
/// Report that can leave a closed network (D11, design §8.12). Whitelist only: every string in it is a fixed literal,
/// an enum name or a value that passed one of the patterns below; everything else is replaced or dropped.
/// </summary>
public sealed record SanitizedReport(SanitizedAggregate Aggregate, IReadOnlyList<SanitizedRun> Runs)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public string ToJson() => JsonSerializer.Serialize(this, Options);

    public string ToMarkdown()
    {
        var a = Aggregate;
        var sb = new StringBuilder();
        sb.AppendLine("# Pelly 비식별 리포트").AppendLine();
        sb.AppendLine("## 요약").AppendLine();
        sb.AppendLine("| 지표 | 값 |").AppendLine("| --- | --- |");
        sb.AppendLine($"| 실행 수 | {N(a.RunCount)} |");
        sb.AppendLine($"| 초기 응답 지연(TTFT) | {Describe(a.Ttft)} ({N(a.TtftSamples)}건) |");
        sb.AppendLine($"| 대기(Queue) | {Describe(a.Queue)} |");
        sb.AppendLine($"| 추론 예산 제어(Thinking) | think 최대 {N(a.ThinkMax)} 토큰, 잘림 {N(a.ThinkTruncatedCount)}건 |");
        sb.AppendLine($"| 패치 포맷 파싱 성공률 | {(a.ParseRate is { } r ? Pct(r) : "-")} ({N(a.ParseOk)}/{N(a.ParseTotal)} 시도) |");
        sb.AppendLine($"| 최종 작업 성공률 | {Pct(a.SuccessRate)} ({N(a.SuccessCount)}/{N(a.RunCount)}건) |");
        sb.AppendLine($"| 단일 작업 완료 시간 | {Describe(a.TaskSeconds)} |");
        if (a.Apply is { } ap)
        {
            sb.AppendLine("## 명세").AppendLine();
            sb.AppendLine("| 지표 | 값 |").AppendLine("| --- | --- |");
            sb.AppendLine($"| 명세 완료율 | {Ratio(ap.Completed, ap.CompletionTotal)} ({N(ap.Completed)}/{N(ap.CompletionTotal)}건) |");
            sb.AppendLine($"| 명세 완료율(작업 3~8개) | {Ratio(ap.Completed3To8, ap.Completion3To8Total)} ({N(ap.Completed3To8)}/{N(ap.Completion3To8Total)}건) |");
            sb.AppendLine($"| 그룹 빌드 통과율 | {Ratio(ap.GroupPassed, ap.GroupTotal)} ({N(ap.GroupPassed)}/{N(ap.GroupTotal)}그룹) |");
            sb.AppendLine();
        }
        Table(sb, "종류", a.RunsByKind);
        Table(sb, "종료 코드", a.ExitCodes);
        Table(sb, "실패 유형", a.FailureKinds);
        Table(sb, "시도 수", a.AttemptDistribution);
        if (a.MeanPhaseSeconds.Count > 0)
        {
            sb.AppendLine("## 단계별 평균 시간(초)").AppendLine();
            sb.AppendLine("| 단계 | 초 |").AppendLine("| --- | --- |");
            foreach (var p in a.MeanPhaseSeconds) sb.AppendLine($"| {p.Key} | {F(p.Value)} |");
            sb.AppendLine();
        }

        sb.AppendLine("## 실행별").AppendLine();
        sb.AppendLine("| # | 종류 | 프로젝트 | 종료 코드 | 시도 | TTFT | 대기 | think | 오류 코드 | 실패 유형 | 버전 |");
        sb.AppendLine("| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |");
        foreach (var x in Runs)
        {
            sb.AppendLine($"| {N(x.Index)} | {x.Kind} | {x.ProjectStyle} | {N(x.ExitCode)} | {N(x.Attempts)} | {F(x.TtftSeconds)} | {F(x.QueueSeconds)} "
                + $"| {x.ThinkMode} {N(x.ThinkTokens)} | {(x.ErrorCodes.Count == 0 ? "-" : string.Join(", ", x.ErrorCodes))} | {x.FailureKind ?? "-"} "
                + $"| pelly {x.PellyVersion}, ollama {x.OllamaVersion ?? "-"}, prompt {x.PromptVersion} |");
        }
        return sb.ToString();
    }

    private static void Table(StringBuilder sb, string title, IReadOnlyDictionary<string, int> rows)
    {
        if (rows.Count == 0) return;
        sb.AppendLine($"## {title}").AppendLine();
        sb.AppendLine("| 값 | 건수 |").AppendLine("| --- | --- |");
        foreach (var r in rows) sb.AppendLine($"| {r.Key} | {N(r.Value)} |");
        sb.AppendLine();
    }

    private static string N(int v) => v.ToString(CultureInfo.InvariantCulture);
    private static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
    private static string Ratio(int ok, int total) => total == 0 ? "-" : Pct((double)ok / total);
    private static string Pct(double v) => (v * 100).ToString("F1", CultureInfo.InvariantCulture) + "%";
    private static string Describe(Spread? s) => s is null ? "-" : $"중위 {F(s.Median)}초 / P90 {F(s.P90)}초";
}

public static partial class ReportSanitizer
{
    [GeneratedRegex(@"\A(CS|MSB|NU|Msg|SQL)[0-9]{1,6}\z", RegexOptions.CultureInvariant)]
    private static partial Regex ErrorCodePattern { get; }

    [GeneratedRegex(@"\A[0-9][0-9A-Za-z.+\-]{0,39}\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern { get; }

    [GeneratedRegex(@"\A(cs|sql)-v[0-9]{1,4}\z", RegexOptions.CultureInvariant)]
    private static partial Regex PromptVersionPattern { get; }

    private static readonly string[] ProjectStyles = ["Sdk", "Legacy", "None", "Sql"];
    private static readonly string[] ThinkModes = ["Off", "Retry", "Always"];

    public static SanitizedReport Sanitize(IReadOnlyList<RunRecord> runs, RunReportData aggregate, ApplyReportData? apply = null)
    {
        var rows = runs.Select((r, i) => SanitizeRun(r.Summary, i + 1)).ToList();
        var counts = apply is null ? null
            : new ApplyCounts(apply.Completion.Ok, apply.Completion.Total, apply.Completion3To8.Ok, apply.Completion3To8.Total,
                apply.GroupBuild.Ok, apply.GroupBuild.Total);
        return new SanitizedReport(SanitizeAggregate(aggregate) with { Apply = counts }, rows);
    }

    private static SanitizedRun SanitizeRun(RunSummary s, int index)
    {
        var codes = (s.ErrorCodes ?? []).Where(IsAllowedCode).ToList();
        return new SanitizedRun(
            index,
            EnumName(s.Kind),
            OneOf(s.ProjectStyle, ProjectStyles, "Other"),
            s.ExitCode,
            s.Attempts,
            PhaseMap(s.PhaseSeconds),
            Num(s.TtftSeconds),
            Num(s.QueueSeconds),
            // ThinkMode is an enum-like set; anything else is reported as invalid rather than guessed.
            OneOf(s.ThinkMode, ThinkModes, "invalid"),
            s.ThinkTokens,
            s.ThinkTruncated,
            s.InputTokensLocal,
            s.InputTokensServer,
            s.InputTokensCached,
            (s.MatchStages ?? []).Select(EnumName).ToList(),
            codes,
            (s.ErrorCodes?.Count ?? 0) - codes.Count,
            s.FormatErrors,
            s.FailureKind is null ? null : FailureKinds.IsKnown(s.FailureKind) ? s.FailureKind : "Other",
            Version(s.PellyVersion),
            s.OllamaVersion is null ? null : Version(s.OllamaVersion),
            PromptVersion(s.PromptVersion));
    }

    private static SanitizedAggregate SanitizeAggregate(RunReportData d)
    {
        var failures = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var f in d.FailureKinds)
        {
            var key = FailureKinds.IsKnown(f.Key) ? f.Key : "Other";
            failures[key] = failures.GetValueOrDefault(key) + f.Value;
        }
        return new SanitizedAggregate(
            d.RunCount,
            d.RunsByKind.ToDictionary(k => EnumName(k.Key), k => k.Value),
            d.SuccessCount,
            Num(d.SuccessRate),
            d.ExitCodes.ToDictionary(k => k.Key.ToString(CultureInfo.InvariantCulture), k => k.Value),
            Clean(d.Ttft), d.TtftSamples, Clean(d.Queue), Clean(d.TaskSeconds),
            d.Parse.Ok, d.Parse.Total, d.Parse.Rate is { } r ? Num(r) : null,
            d.ThinkMax, d.ThinkTruncatedCount,
            failures,
            PhaseMap(d.MeanPhaseSeconds),
            d.AttemptDistribution.ToDictionary(k => k.Key.ToString(CultureInfo.InvariantCulture), k => k.Value));
    }

    private static Spread? Clean(Spread? s) => s is null ? null : new Spread(Num(s.Median), Num(s.P90));

    private static Dictionary<string, double> PhaseMap(IReadOnlyDictionary<RunPhase, double>? phases)
    {
        var map = new Dictionary<string, double>(StringComparer.Ordinal);
        if (phases is null) return map;
        foreach (var p in phases.OrderBy(p => p.Key))
            if (Enum.IsDefined(p.Key)) map[p.Key.ToString()] = Num(p.Value);
        return map;
    }

    public static bool IsAllowedCode(string? code) => code is not null && (code is "TEST" or "RULE" || ErrorCodePattern.IsMatch(code));

    public static string PromptVersion(string? v) => v is not null && PromptVersionPattern.IsMatch(v) ? v : "invalid";

    public static string Version(string? v) => v is not null && VersionPattern.IsMatch(v) ? v : "invalid";

    private static string OneOf(string? value, string[] allowed, string otherwise) =>
        value is not null && allowed.Contains(value, StringComparer.Ordinal) ? value : otherwise;

    private static string EnumName<T>(T value) where T : struct, Enum => Enum.IsDefined(value) ? value.ToString() : "Other";

    private static double Num(double v) => double.IsFinite(v) ? v : 0;
}
