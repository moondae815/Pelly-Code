using PellyCoder.Core.Files;
using PellyCoder.Core.Specs;

namespace PellyCoder.Core.Impact;

/// <summary>
/// Turns a change spec into impact seeds and an impact result into spec warnings (design §5 "영향 탐색"). It only ever
/// produces warnings: a missing impact target is something for the author to look at, not a reason to refuse the spec.
/// </summary>
public static class ImpactCheck
{
    /// <summary>
    /// The not-done cs tasks the search starts from: the grouped ones, or every one when <paramref name="allCsTasks"/>.
    /// sql tasks are never seeds in V2-1c.
    /// </summary>
    public static IReadOnlyList<ImpactSeed> Seeds(ChangeSpec spec, bool allCsTasks) =>
        spec.Tasks
            .Where(t => t is { Kind: TaskKind.CSharp, Done: false, Symbol: not null } && (allCsTasks || t.Group is not null))
            .Select(t =>
            {
                var (className, member) = SpecSymbol.Split(t.Symbol!);
                return new ImpactSeed(t.Id, t.Line, Normalize(t.Path), className, member, t.Kind);
            })
            .ToList();

    /// <summary>Warnings for one impact result: how it was found, unsupported seeds, then each target no task names.</summary>
    public static IReadOnlyList<SpecIssue> Issues(ChangeSpec spec, IReadOnlyList<ImpactSeed> seeds, ImpactResult result)
    {
        var issues = new List<SpecIssue>();
        if (result.Mode == ImpactMode.Name)
            issues.Add(Warning(0, "ImpactNameBased", $"영향 탐색: 이름 기준(부정확): 의미 분석 실패 — {result.Fallback?.Display ?? "알 수 없음"}"));
        if (result.Incomplete)
            issues.Add(Warning(0, "ImpactIncomplete", "영향 탐색: 일부 폴더·파일을 읽지 못해 결과가 불완전합니다"));

        var lines = seeds.GroupBy(s => s.TaskId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First().Line, StringComparer.Ordinal);
        int LineOf(string taskId) => lines.GetValueOrDefault(taskId);

        foreach (var id in result.UnsupportedSeeds)
            issues.Add(Warning(LineOf(id), "ImpactUnsupported", $"작업 {id}: 영향 탐색은 메서드·속성만 봅니다."));

        foreach (var id in result.SeedsNotFound)
        {
            var seed = seeds.FirstOrDefault(s => s.TaskId == id);
            var where = seed is null ? "" : $": {seed.Path}#{(seed.ClassName is null ? seed.Member : seed.ClassName + "." + seed.Member)}";
            issues.Add(Warning(LineOf(id), "ImpactSeedNotFound", $"작업 {id}: 영향 탐색 시작점을 찾지 못했습니다{where}"));
        }

        // One warning per place, however many seeds reach it: on the first seed's line, naming every seed.
        // Every task counts here, done ones included: a target already handled earlier is not missing.
        var places = result.Targets
            .Where(t => !spec.Tasks.Any(task => Covers(task, t)))
            .GroupBy(t => (Path: Normalize(t.Path), t.Member), PlaceComparer.Instance);
        foreach (var place in places)
        {
            var first = place.First();
            var ids = string.Join(", ", place.Select(t => t.SeedTaskId).Distinct(StringComparer.Ordinal));
            issues.Add(Warning(LineOf(first.SeedTaskId), "ImpactMissing",
                $"작업 {ids}의 영향 대상이 명세에 없습니다: {place.Key.Path}#{place.Key.Member} ({first.Reason})"));
        }

        return issues;
    }

    /// <summary>
    /// True when <paramref name="task"/> names <paramref name="target"/>: same file (OS path comparison, either separator)
    /// and, for a cs task, the same member name. The one place the match rule lives, so sql targets can be added here.
    /// </summary>
    public static bool Covers(SpecTask task, ImpactTarget target)
    {
        if (task.Kind != TaskKind.CSharp || task.Symbol is null) return false;
        return PathComparers.ForOs.Equals(Normalize(task.Path), Normalize(target.Path))
            && string.Equals(SpecSymbol.Split(task.Symbol).Member, target.Member, StringComparison.Ordinal);
    }

    private static string Normalize(string path) => path.Replace('\\', '/');

    /// <summary>Same place: OS path comparison (separators already normalized) and ordinal member name.</summary>
    private sealed class PlaceComparer : IEqualityComparer<(string Path, string Member)>
    {
        public static readonly PlaceComparer Instance = new();

        public bool Equals((string Path, string Member) x, (string Path, string Member) y) =>
            PathComparers.ForOs.Equals(x.Path, y.Path) && string.Equals(x.Member, y.Member, StringComparison.Ordinal);

        public int GetHashCode((string Path, string Member) obj) =>
            HashCode.Combine(PathComparers.ForOs.GetHashCode(obj.Path), StringComparer.Ordinal.GetHashCode(obj.Member));
    }

    private static SpecIssue Warning(int line, string code, string message) => new(line, SpecIssueSeverity.Warning, code, message);
}
