using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace S8SemanticLoad;

public sealed record IssueCount(WorkspaceIssueKind Kind, bool FromCSharpProject, bool IsFailure, int Count);

/// <summary><c>Loaded</c> counts census C# projects that were opened; <c>Extra</c> counts other projects MSBuild opened too (references outside the solution, extra target frameworks).</summary>
public sealed record CensusCounts(int SdkCs, int LegacyCs, int NonCs, int UnrestoredSdk, int Loaded, int Extra)
{
    public int CSharpTotal => SdkCs + LegacyCs;
}

public sealed record HealthTotals(int CompileErrorsCounted, int CompileErrorsExcluded, int MissingMetadataRefs, int ProjectsMeasured);

/// <summary>
/// One load. In field mode (the default) it holds numbers and fixed kind names only, so it can leave the
/// closed network; names and messages appear only in detail mode.
/// </summary>
public sealed record LoadReport(
    string Mode, string Os, string ToolForm, string RoslynVersion, string? MsBuildGuess,
    CensusCounts Census, IReadOnlyList<IssueCount> Issues,
    double OpenSeconds, double CompileSeconds, double TotalSeconds, long PeakWorkingSetBytes,
    CallResolution Calls, HealthTotals Health, ProbeResult Probe, A3Result? A3,
    IReadOnlyList<string>? DetailMessages, IReadOnlyList<ProjectHealth>? DetailProjects)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public bool IsField => Mode == "field";

    /// <summary>Field mode drops every name-bearing part: detail lists and the a3 key lists (kept as counts via Matches).</summary>
    public LoadReport Sanitized() => IsField
        ? this with
        {
            DetailMessages = null,
            DetailProjects = null,
            A3 = A3 is null ? null : new A3Result(Masked(A3.Missing), Masked(A3.ForbiddenHit), Masked(A3.Extra)),
        }
        : this;

    private static IReadOnlyList<string> Masked(IReadOnlyList<string> keys) => keys.Select((_, i) => $"#{i + 1}").ToList();

    public string ToJson() => JsonSerializer.Serialize(Sanitized(), Json);

    public static LoadReport FromJson(string json) =>
        JsonSerializer.Deserialize<LoadReport>(json, Json) ?? throw new InvalidDataException("empty load report");

    public string ToSummary()
    {
        var b = new StringBuilder();
        b.AppendLine($"S8 의미 분석 불러오기 — {Os} · 형태 {ToolForm} · Roslyn {RoslynVersion} · MSBuild {(MsBuildGuess is null ? "알 수 없음" : MsBuildGuess + "(추정)")}");
        b.AppendLine($"프로젝트: C# {Census.CSharpTotal}(SDK {Census.SdkCs}, 레거시 {Census.LegacyCs}) · C# 아님 {Census.NonCs} · 복원 안 된 SDK {Census.UnrestoredSdk} · 불러옴 {Census.Loaded} · 솔루션 밖 추가 {Census.Extra}");
        var issues = Issues.Where(i => i.Count > 0).ToList();
        b.AppendLine(issues.Count == 0
            ? "작업 공간 진단: 없음"
            : "작업 공간 진단: " + string.Join(", ", issues.Select(i => $"{(i.IsFailure ? "실패" : "경고")} {KindName(i.Kind)}{(i.FromCSharpProject ? "" : "(C# 아님)")} {i.Count}")));
        b.AppendLine($"시간: 열기 {OpenSeconds:F1}초 · 컴파일 정보 {CompileSeconds:F1}초 · 전체 {TotalSeconds:F1}초 · 최대 메모리 {PeakWorkingSetBytes / 1_048_576} MB");
        b.AppendLine($"호출 해석률: {Calls.Rate:P1} ({Calls.Resolved}/{Calls.Total}, 후보 여럿 {Calls.Ambiguous}, 해석 못 함 {Calls.Unresolved})");
        b.AppendLine($"컴파일 오류: {Health.CompileErrorsCounted} (측정 프로젝트 {Health.ProjectsMeasured}, 복원 안 된 프로젝트 몫 제외 {Health.CompileErrorsExcluded}) · 없는 참조 DLL {Health.MissingMetadataRefs}");
        b.AppendLine($"참조 찾기: 메서드 {Probe.Methods}개 · 최대 {Probe.MaxSeconds:F2}초 · 평균 {Probe.MeanSeconds:F2}초 · 위치 0 비율 {Probe.ZeroReferenceRatio:P0}");
        if (A3 is not null)
            b.AppendLine($"코퍼스 a3 대조: {(A3.Matches ? "정답과 같음" : "다름")} (빠짐 {A3.Missing.Count}, 금지 포함 {A3.ForbiddenHit.Count}, 그 밖 {A3.Extra.Count})");
        if (!IsField && DetailMessages is { Count: > 0 })
        {
            b.AppendLine("진단 메시지(--detail):");
            foreach (var m in DetailMessages) b.AppendLine("  " + m);
        }
        return b.ToString();
    }

    public static string KindName(WorkspaceIssueKind k) => k switch
    {
        WorkspaceIssueKind.MsBuildNotFound => "빌드 도구(MSBuild·SDK)를 찾을 수 없음",
        WorkspaceIssueKind.UnsupportedProject => "지원 안 되는 프로젝트",
        WorkspaceIssueKind.ReferenceResolution => "참조 해석 실패",
        WorkspaceIssueKind.NotRestored => "패키지 복원 안 됨",
        _ => "기타",
    };
}
