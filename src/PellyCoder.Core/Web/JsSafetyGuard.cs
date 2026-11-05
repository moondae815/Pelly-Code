using System.Text.RegularExpressions;
using PellyCoder.Core.Rules;

namespace PellyCoder.Core.Web;

/// <summary>A JS or CSS safety rule hit; <paramref name="Rule"/> is one of the rule IDs.</summary>
public sealed record WebSafetyFinding(string Rule, int Line, string Text);

/// <summary>Safety rules for client JavaScript: dynamic code, string timers, document.write, execScript and ActiveX.</summary>
public static class JsSafetyGuard
{
    public const string Eval = "JsEval";
    public const string FunctionConstructor = "JsFunctionConstructor";
    public const string StringTimer = "JsStringTimer";
    public const string DocumentWrite = "JsDocumentWrite";
    public const string ExecScript = "JsExecScript";
    public const string ActiveX = "JsActiveX";

    public static IReadOnlyList<WebSafetyFinding> Findings(JsDocument doc)
    {
        var result = new List<WebSafetyFinding>();
        foreach (var call in doc.Calls)
        {
            var callee = call.Callee.StartsWith("window.", StringComparison.Ordinal) ? call.Callee["window.".Length..] : call.Callee;
            string? rule = null;
            if (callee == "eval" && !call.IsNew) rule = Eval;
            else if (callee == "Function") rule = FunctionConstructor;
            else if (callee is "setTimeout" or "setInterval" && !call.IsNew
                     && call.FirstArg is JsArgKind.StringLiteral or JsArgKind.StringConcat) rule = StringTimer;
            else if (callee is "document.write" or "document.writeln" && !call.IsNew) rule = DocumentWrite;
            else if (callee == "execScript" && !call.IsNew) rule = ExecScript;
            else if (callee == "ActiveXObject" && call.IsNew) rule = ActiveX;
            if (rule is not null) result.Add(new WebSafetyFinding(rule, call.Line, call.Text));
        }
        return result;
    }

    /// <summary>Findings whose (Rule, Text) count grew beyond the original, as many as it grew (the later occurrences).</summary>
    public static IReadOnlyList<WebSafetyFinding> NewFindings(JsDocument original, JsDocument patched) =>
        Grown(Findings(original), Findings(patched));

    private static readonly (string Rule, Regex Pattern)[] TextRules =
    [
        (Eval, new Regex(@"\beval\s*\(", RegexOptions.CultureInvariant)),
        (FunctionConstructor, new Regex(@"\bnew\s+Function\s*\(", RegexOptions.CultureInvariant)),
        (StringTimer, new Regex(@"\b(?:set(?:Timeout|Interval))\s*\(\s*[""']", RegexOptions.CultureInvariant)),
        (DocumentWrite, new Regex(@"\bdocument\.write(?:ln)?\s*\(", RegexOptions.CultureInvariant)),
        (ExecScript, new Regex(@"\bexecScript\s*\(", RegexOptions.CultureInvariant)),
        (ActiveX, new Regex(@"\bnew\s+ActiveXObject\s*\(", RegexOptions.CultureInvariant)),
    ];

    /// <summary>Fallback mode (a file that does not parse): the same rules as regexes over the masked text, compared by normalized line.</summary>
    public static IReadOnlyList<WebSafetyFinding> NewFindingsText(string originalLf, string patchedLf) =>
        Grown(TextFindings(originalLf), TextFindings(patchedLf));

    private static List<WebSafetyFinding> TextFindings(string textLf)
    {
        var mask = CodeMask.Js(textLf);
        var lines = textLf.Split('\n');
        var maskLines = mask.Split('\n');
        var result = new List<WebSafetyFinding>();
        for (var i = 0; i < maskLines.Length && i < lines.Length; i++)
        {
            foreach (var (rule, pattern) in TextRules)
                foreach (Match _ in pattern.Matches(maskLines[i]))
                    result.Add(new WebSafetyFinding(rule, i + 1, Regex.Replace(lines[i], @"\s+", " ").Trim()));
        }
        return result;
    }

    private static IReadOnlyList<WebSafetyFinding> Grown(IReadOnlyList<WebSafetyFinding> original, IReadOnlyList<WebSafetyFinding> patched)
    {
        var baseline = original.GroupBy(f => (f.Rule, f.Text)).ToDictionary(g => g.Key, g => g.Count());
        var result = new List<WebSafetyFinding>();
        foreach (var group in patched.GroupBy(f => (f.Rule, f.Text)))
        {
            var extra = group.Count() - baseline.GetValueOrDefault(group.Key);
            if (extra > 0) result.AddRange(group.OrderBy(f => f.Line).TakeLast(extra));
        }
        return result.OrderBy(f => f.Line).ThenBy(f => f.Rule, StringComparer.Ordinal).ToArray();
    }

    internal static IReadOnlyList<WebSafetyFinding> GrownFindings(IReadOnlyList<WebSafetyFinding> original, IReadOnlyList<WebSafetyFinding> patched) =>
        Grown(original, patched);
}
