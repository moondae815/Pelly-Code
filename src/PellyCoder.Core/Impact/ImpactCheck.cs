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

        // Every task counts here, done ones included: a target already handled earlier is not missing.
        foreach (var target in result.Targets.Where(t => !spec.Tasks.Any(task => Covers(task, t))))
            issues.Add(Warning(LineOf(target.SeedTaskId), "ImpactMissing",
                $"작업 {target.SeedTaskId}의 영향 대상이 명세에 없습니다: {target.Path}#{target.Member} ({target.Reason})"));

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

    private static SpecIssue Warning(int line, string code, string message) => new(line, SpecIssueSeverity.Warning, code, message);
}
