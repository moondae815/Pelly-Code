using PellyCoder.Core.Models;

namespace PellyCoder.Core.Asp;

/// <summary>
/// Review warnings (never rejections) for silent-wrong edits that no other check sees (docs/asp-premeasure.md): a
/// condition that checks and converts the same value although VBScript <c>And</c>/<c>Or</c> do not short-circuit, and a
/// <c>Request</c> read of the target that the edit dropped. Only warnings the original target span does not already have
/// count.
/// </summary>
public static class AspLogicWarnings
{
    private static readonly HashSet<string> CheckFunctions = new(StringComparer.OrdinalIgnoreCase) { "IsNumeric", "IsDate" };

    private static readonly HashSet<string> ConvertFunctions = new(StringComparer.OrdinalIgnoreCase)
    {
        "CLng", "CInt", "CDbl", "CDate",
    };

    public static IReadOnlyList<string> New(AspPage original, TextSpanInfo originalSpan, AspPage patched, TextSpanInfo patchedSpan)
    {
        var result = new List<string>();

        var existing = NoShortCircuit(original, originalSpan).Select(e => e.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var (key, display) in NoShortCircuit(patched, patchedSpan))
        {
            if (existing.Add(key))
                result.Add($"And/Or는 단축 평가를 하지 않습니다: {display}를 같은 조건에서 검사·변환합니다");
        }

        var kept = RequestReads(patched, patchedSpan).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var name in RequestReads(original, originalSpan).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!kept.Contains(name))
                result.Add($"기존 입력값 읽기 줄이 사라졌습니다: {name}");
        }
        return result;
    }

    private static IEnumerable<VbStatement> InSpan(AspPage page, TextSpanInfo span) =>
        page.Statements.Where(s => s.StartLine >= span.StartLine && s.StartLine <= span.EndLine);

    /// <summary>
    /// For each <c>If</c>/<c>ElseIf</c> condition joined by <c>And</c>/<c>Or</c>: every expression E that is both checked
    /// (<c>IsNumeric(E)</c>, <c>IsDate(E)</c>) and converted (<c>CLng(E)</c> …). Key is E's normalized token text.
    /// </summary>
    private static IEnumerable<(string Key, string Display)> NoShortCircuit(AspPage page, TextSpanInfo span)
    {
        foreach (var statement in InSpan(page, span))
        {
            var t = statement.Tokens;
            if (t.Count == 0 || !(IsWord(t[0], "If") || IsWord(t[0], "ElseIf")))
                continue;
            var end = ConditionEnd(t);
            if (!Enumerable.Range(1, end - 1).Any(i => IsWord(t[i], "And") || IsWord(t[i], "Or")))
                continue;
            var checkedArgs = new Dictionary<string, string>(StringComparer.Ordinal);
            var converted = new HashSet<string>(StringComparer.Ordinal);
            for (var i = 1; i + 1 < end; i++)
            {
                if (t[i].Kind != VbTokenKind.Identifier || t[i + 1].Kind != VbTokenKind.OpenParen
                    || (t[i - 1].Kind == VbTokenKind.Dot))
                    continue;
                var close = Close(t, i + 1, end);
                if (close <= i + 2)
                    continue;
                var arg = t.Skip(i + 2).Take(close - i - 2).ToList();
                var key = string.Join(" ", arg.Select(a => a.Text)).ToLowerInvariant();
                if (CheckFunctions.Contains(t[i].Text))
                    checkedArgs.TryAdd(key, Display(page, arg));
                else if (ConvertFunctions.Contains(t[i].Text))
                    converted.Add(key);
            }
            foreach (var (key, display) in checkedArgs)
            {
                if (converted.Contains(key))
                    yield return (key, display);
            }
        }
    }

    /// <summary>Index of the top-level <c>Then</c> (exclusive end of the condition), or the token count.</summary>
    private static int ConditionEnd(IReadOnlyList<VbToken> t)
    {
        var depth = 0;
        for (var i = 1; i < t.Count; i++)
        {
            if (t[i].Kind == VbTokenKind.OpenParen)
                depth++;
            else if (t[i].Kind == VbTokenKind.CloseParen)
                depth--;
            else if (depth == 0 && IsWord(t[i], "Then"))
                return i;
        }
        return t.Count;
    }

    /// <summary>Names assigned an expression that reads <c>Request</c> (any collection, any wrapper), in statement parts.</summary>
    private static IEnumerable<string> RequestReads(AspPage page, TextSpanInfo span)
    {
        foreach (var statement in InSpan(page, span))
        {
            var t = statement.Tokens;
            var partStart = 0;
            for (var k = 0; k <= t.Count; k++)
            {
                if (k < t.Count && !(IsOperator(t[k], ":") || IsWord(t[k], "Then") || IsWord(t[k], "Else")))
                    continue;
                if (AssignedFromRequest(t, partStart, k) is { } name)
                    yield return name;
                partStart = k + 1;
            }
        }
    }

    private static string? AssignedFromRequest(IReadOnlyList<VbToken> t, int start, int end)
    {
        var p = start < end && IsWord(t[start], "Set") ? start + 1 : start;
        if (p + 2 >= end || t[p].Kind != VbTokenKind.Identifier || !IsOperator(t[p + 1], "="))
            return null;
        for (var k = p + 2; k < end; k++)
        {
            if (t[k].Kind == VbTokenKind.Identifier && t[k].Text.Equals("Request", StringComparison.OrdinalIgnoreCase)
                && t[k - 1].Kind != VbTokenKind.Dot)
                return t[p].Text;
        }
        return null;
    }

    /// <summary>The expression as written when it sits on one line, otherwise its tokens joined.</summary>
    private static string Display(AspPage page, List<VbToken> tokens)
    {
        var first = tokens[0];
        var last = tokens[^1];
        if (first.Line == last.Line && first.Line >= 1 && first.Line <= page.LineCount)
        {
            var line = page.LineText(first.Line);
            var from = first.Column - 1;
            var to = last.Column - 1 + last.Text.Length;
            if (from >= 0 && to <= line.Length && from < to)
                return line[from..to];
        }
        return string.Concat(tokens.Select(x => x.Text));
    }

    private static int Close(IReadOnlyList<VbToken> t, int open, int end)
    {
        var depth = 0;
        for (var i = open; i < end; i++)
        {
            if (t[i].Kind == VbTokenKind.OpenParen)
                depth++;
            else if (t[i].Kind == VbTokenKind.CloseParen && --depth == 0)
                return i;
        }
        return end;
    }

    private static bool IsWord(VbToken token, string text) =>
        token.Kind is VbTokenKind.Identifier or VbTokenKind.Keyword && token.Text.Equals(text, StringComparison.OrdinalIgnoreCase);

    private static bool IsOperator(VbToken token, string text) =>
        token.Kind == VbTokenKind.Operator && token.Text == text;
}
