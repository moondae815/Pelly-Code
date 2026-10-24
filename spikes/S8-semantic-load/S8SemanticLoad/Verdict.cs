namespace S8SemanticLoad;

public sealed record Judgement(string Item, bool? Pass, string Value);

/// <summary>Design §7 pass criteria, fixed before running. The first run is cold, the rest are warm.</summary>
public static class Verdict
{
    public const string LoadItem = "불러오기";
    public const string TypeInfoItem = "타입 정보";
    public const string ColdItem = "콜드 시간";
    public const string WarmItem = "웜 시간";
    public const string ProbeItem = "참조 찾기";
    public const string CorpusItem = "코퍼스 대조";

    public const double ColdLimitSeconds = 15;
    public const double WarmLimitSeconds = 10;
    public const double ProbeLimitSeconds = 2;
    public const double MinCallRate = 0.99;
    public const int TimedProjectLimit = 4;

    public static IReadOnlyList<Judgement> Judge(IReadOnlyList<LoadReport> runs)
    {
        if (runs.Count == 0) throw new ArgumentException("no runs", nameof(runs));
        var cold = runs[0];
        var warm = runs.Skip(1).ToList();
        var list = new List<Judgement>();

        var csFailures = runs.Max(r => r.Issues.Where(i => i.FromCSharpProject && i.IsFailure).Sum(i => i.Count));
        var allLoaded = runs.All(r => r.Census.Loaded == r.Census.CSharpTotal);
        list.Add(new(LoadItem, allLoaded && csFailures == 0, $"불러옴 {cold.Census.Loaded}/{cold.Census.CSharpTotal}, C# 실패 {csFailures}, 솔루션 밖 추가 {cold.Census.Extra}"));

        var minRate = runs.Min(r => r.Calls.Rate);
        var errors = runs.Max(r => r.Health.CompileErrorsCounted);
        var missing = runs.Max(r => r.Health.MissingMetadataRefs);
        // 잰 것이 없으면(복원된 C# 프로젝트 0 또는 호출 0) 통과가 아니라 판정 불가다.
        var measured = runs.Min(r => r.Health.ProjectsMeasured);
        var nothingMeasured = measured == 0 || runs.Any(r => r.Calls.Total == 0);
        list.Add(new(TypeInfoItem, nothingMeasured ? null : errors == 0 && missing == 0 && minRate >= MinCallRate,
            $"측정 프로젝트 {measured}/{cold.Census.CSharpTotal}, 해석률 {minRate:P1}, 컴파일 오류 {errors}, 없는 참조 {missing}"));

        var timed = cold.Census.CSharpTotal <= TimedProjectLimit;
        var coldSec = cold.OpenSeconds + cold.CompileSeconds;
        list.Add(new(ColdItem, timed ? coldSec <= ColdLimitSeconds : null, $"{coldSec:F1}초"));
        if (warm.Count == 0)
            list.Add(new(WarmItem, null, "웜 반복 없음"));
        else
        {
            var warmSec = warm.Average(r => r.OpenSeconds + r.CompileSeconds);
            list.Add(new(WarmItem, timed ? warmSec <= WarmLimitSeconds : null, $"평균 {warmSec:F1}초"));
        }

        var probeMax = runs.Max(r => r.Probe.MaxSeconds);
        var probed = runs.Min(r => r.Probe.Methods);
        list.Add(new(ProbeItem, probed == 0 ? null : probeMax <= ProbeLimitSeconds, $"메서드 {probed}개, 최대 {probeMax:F2}초"));

        if (runs.Any(r => r.A3 is not null))
        {
            var ok = runs.Where(r => r.A3 is not null).All(r => r.A3!.Matches);
            list.Add(new(CorpusItem, ok, ok ? "정답과 같음" : "다름"));
        }
        return list;
    }
}
