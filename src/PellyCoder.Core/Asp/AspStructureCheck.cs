namespace PellyCoder.Core.Asp;

/// <summary>Structure checks of a patched page against the original: new parse problems and the locked shape.</summary>
public static class AspStructureCheck
{
    /// <summary>
    /// Problems whose (Code, Message) count grew beyond the original, as many as it grew, with the patched page's
    /// line numbers (the later occurrences of each problem).
    /// </summary>
    public static IReadOnlyList<AspProblem> NewProblems(AspPage original, AspPage patched)
    {
        var baseline = original.Problems.GroupBy(p => (p.Code, p.Message)).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<AspProblem>();
        foreach (var group in patched.Problems.GroupBy(p => (p.Code, p.Message)))
        {
            var extra = group.Count() - baseline.GetValueOrDefault(group.Key);
            if (extra > 0)
                result.AddRange(group.OrderBy(p => p.Line).ThenBy(p => p.Column).TakeLast(extra));
        }
        return result.OrderBy(p => p.Line).ThenBy(p => p.Column).ToArray();
    }

    /// <summary>A Korean reason when the include list, directives or the target procedure's identity changed; otherwise null.</summary>
    public static string? ShapeViolation(AspPage original, AspPage patched, AspRunState state)
    {
        var originalIncludes = original.Includes.Select(i => (i.Path, i.Virtual)).ToArray();
        var patchedIncludes = patched.Includes.Select(i => (i.Path, i.Virtual)).ToArray();
        if (!originalIncludes.SequenceEqual(patchedIncludes))
            return "include 목록(경로·종류·순서)이 바뀌었습니다. include 줄은 고칠 수 없습니다.";

        var originalDirectives = DirectiveTexts(original);
        var patchedDirectives = DirectiveTexts(patched);
        if (!originalDirectives.SequenceEqual(patchedDirectives, StringComparer.Ordinal))
            return "`<%@ … %>` 지시문이 바뀌었습니다. 지시문은 고칠 수 없습니다.";

        if (state.Unit != AspTargetUnit.Procedure || state.Procedure is not { } target)
            return null;

        var matches = patched.Procedures.Where(p => SameIdentity(p, target)).ToArray();
        var display = target.ClassName is null ? target.Name : $"{target.ClassName}.{target.Name}";
        if (matches.Length != 1)
            return $"대상 프로시저 `{display}`를 수정본에서 하나로 찾을 수 없습니다. 프로시저 이름을 바꾸거나 없애거나 늘리지 마십시오.";

        var found = matches[0];
        if (!found.Keyword.Equals(target.Keyword, StringComparison.OrdinalIgnoreCase))
            return $"대상 프로시저 `{display}`의 종류가 `{target.Keyword}`에서 `{found.Keyword}`(으)로 바뀌었습니다.";
        if (!found.Parameters.SequenceEqual(target.Parameters, StringComparer.OrdinalIgnoreCase))
            return $"대상 프로시저 `{display}`의 매개변수 목록(이름·순서)이 바뀌었습니다. "
                + $"원래 매개변수: ({string.Join(", ", target.Parameters)})";
        if (patched.Procedures.Count != original.Procedures.Count)
            return "프로시저 수가 바뀌었습니다. 프로시저 대상에서는 새 `Function`·`Sub`를 추가하거나 지울 수 없습니다.";
        return null;
    }

    private static bool SameIdentity(AspProcedure a, AspProcedure b) =>
        string.Equals(a.ClassName, b.ClassName, StringComparison.OrdinalIgnoreCase)
        && a.Name.Equals(b.Name, StringComparison.OrdinalIgnoreCase);

    private static string[] DirectiveTexts(AspPage page) =>
        page.Segments.Where(s => s.Kind == AspSegmentKind.Directive).Select(s => s.Text).ToArray();
}
