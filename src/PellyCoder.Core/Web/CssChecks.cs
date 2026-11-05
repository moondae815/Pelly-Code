namespace PellyCoder.Core.Web;

/// <summary>Blocking CSS checks run on a patched sheet; counted as new compared with the original, by content.</summary>
public static class CssChecks
{
    /// <summary>Structure problems whose (Code, Message) count grew beyond the original, as many as it grew (the later ones).</summary>
    public static IReadOnlyList<CssProblem> NewProblems(CssSheet original, CssSheet patched)
    {
        var baseline = original.Problems.GroupBy(p => (p.Code, p.Message)).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<CssProblem>();
        foreach (var group in patched.Problems.GroupBy(p => (p.Code, p.Message)))
        {
            var extra = group.Count() - baseline.GetValueOrDefault(group.Key);
            if (extra > 0) result.AddRange(group.OrderBy(p => p.Line).ThenBy(p => p.Column).TakeLast(extra));
        }
        return result.OrderBy(p => p.Line).ThenBy(p => p.Column).ToArray();
    }

    /// <summary>For a rule target: exactly one rule with the same selector and container must remain; null otherwise.</summary>
    public static string? ShapeViolation(WebRunState state, CssSheet patched)
    {
        if (state.Unit != WebTargetUnit.Rule || state.Rule is not { } rule) return null;
        var count = patched.Rules.Count(r => r.Selector == rule.Selector && r.Container == rule.Container);
        return count == 1 ? null : $"선택자 {rule.Selector}를 바꿀 수 없습니다";
    }
}
