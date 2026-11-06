namespace PellyCoder.Core.Web;

/// <summary>Blocking CSS checks run on a patched sheet; counted as new compared with the original, by content.</summary>
public static class CssChecks
{
    /// <summary>Structure problems whose (Code, Message) count grew beyond the original, as many as it grew (the later ones).</summary>
    public static IReadOnlyList<CssProblem> NewProblems(CssSheet original, CssSheet patched) =>
        NewProblems(original.Problems, patched.Problems);

    /// <summary>The same count rule over problem lists (several blocks joined).</summary>
    public static IReadOnlyList<CssProblem> NewProblems(IReadOnlyList<CssProblem> original, IReadOnlyList<CssProblem> patched)
    {
        var baseline = original.GroupBy(p => (p.Code, p.Message)).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<CssProblem>();
        foreach (var group in patched.GroupBy(p => (p.Code, p.Message)))
        {
            var extra = group.Count() - baseline.GetValueOrDefault(group.Key);
            if (extra > 0) result.AddRange(group.OrderBy(p => p.Line).ThenBy(p => p.Column).TakeLast(extra));
        }
        return result.OrderBy(p => p.Line).ThenBy(p => p.Column).ToArray();
    }

    /// <summary>
    /// For a rule target: as many rules with the same kind, selector and container as in the original must remain (a selector
    /// or @media head may repeat), so the target at its original position among them still exists; null when they do.
    /// </summary>
    public static string? ShapeViolation(WebRunState state, CssSheet patched)
    {
        if (state.Unit != WebTargetUnit.Rule || state.Rule is not { } rule) return null;
        bool Same(CssRule r) => r.IsAtRule == rule.IsAtRule && r.Selector == rule.Selector && r.Container == rule.Container;
        var was = (state.OriginalCss ?? CssSheet.Parse(state.OriginalText)).Rules.Where(Same).ToList();
        var (expected, index) = JsChecks.Occurrence(was.Count, was.FindIndex(r => r.StartLine == rule.StartLine));
        var now = patched.Rules.Where(Same).ToList();
        return now.Count == expected && index < now.Count ? null : $"선택자 {rule.Selector}를 바꿀 수 없습니다";
    }
}
