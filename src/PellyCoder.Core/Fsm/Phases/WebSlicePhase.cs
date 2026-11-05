using System.Text.RegularExpressions;
using Acornima;
using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Files;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Web;
using static PellyCoder.Core.Fsm.Phases.WebLocateCommon;

namespace PellyCoder.Core.Fsm.Phases;

/// <summary>
/// Re-reads the JS or CSS file, re-finds the target (an earlier attempt may have shifted it) and builds the budgeted context
/// (design §4.6). JS: the editable target, the language level / jQuery / encoding, the declarations of the functions the target
/// calls (same file and site index), the file's named function declarations and the lines around the target. CSS: the target,
/// the encoding and IE hack / vendor prefix use, the other rules sharing a selector with the target and the lines around it.
/// </summary>
public sealed class WebSlicePhase(FileGuard fileGuard, ITokenCounter tokens, BudgetOptions budget) : IPhase
{
    private const int AroundLines = 10;

    // Dropped from the front when the budget is short; the earlier, the lower the priority.
    private static readonly string[] JsDropOrder = ["around", "file", "callees", "env"];
    private static readonly string[] CssDropOrder = ["around", "related", "env"];

    private static readonly Regex CallName = new(@"(?<![\w$.])([A-Za-z_$][\w$]*(?:\.[A-Za-z_$][\w$]*)*)\s*\(", RegexOptions.Compiled);

    public RunPhase Phase => RunPhase.Slice;

    public Task<PhaseOutcome> ExecuteAsync(RunContext ctx, CancellationToken ct)
    {
        var current = fileGuard.Read(ctx.FilePath);
        if (current.Sha256 != ctx.Current.Sha256)
        {
            // Keep ctx.Current as the last content pelly knows, so rollback sees the change and does not overwrite it.
            ctx.Messages.Add($"pelly 실행 중에 파일이 다른 곳에서 바뀌었습니다. 더 진행하지 않습니다: {ctx.FilePath}");
            ctx.FailureKind = FailureKinds.FileChangedExternally;
            return Task.FromResult(PhaseOutcome.EnvError);
        }
        ctx.Current = current;

        var state = ctx.Web!;
        var text = current.Text;
        var lines = text.Split('\n');
        var lineCount = LineCount(text);

        List<SlicePart> candidates = [];
        void Add(string kind, string partText, int priority)
        {
            if (partText.Length > 0) candidates.Add(new SlicePart(kind, partText, tokens.Count(partText), priority));
        }

        string hint;
        string[] dropOrder;
        if (state.Kind == EditKind.Css)
        {
            var sheet = CssSheet.Parse(text);
            CssRule? rule = null;
            if (state.Unit == WebTargetUnit.Rule)
            {
                rule = RefindRule(sheet, state);
                if (rule is null) return Task.FromResult(NotFound(ctx));
                state.CurrentSpan = new TextSpanInfo(rule.StartLine, rule.EndLine);
            }
            if (!InFile(state.CurrentSpan, lineCount)) return Task.FromResult(NotFound(ctx));
            var span = state.CurrentSpan;
            var original = state.OriginalCss ?? CssSheet.Parse(state.OriginalText);
            var ieHacks = YesNo(original.UsesIeHacks);

            Add("env", $"File encoding: {state.EncodingName}\nIE hacks used in this file: {ieHacks}\n"
                + $"Vendor prefixes used in this file: {YesNo(original.UsesVendorPrefixes)}", 3);
            Add("related", Related(sheet, span, rule, lines), 2);
            Add("around", Around(lines, span, lineCount), 1);
            hint = $"CSS; file encoding {state.EncodingName}; IE hacks: {ieHacks}";
            dropOrder = CssDropOrder;
        }
        else
        {
            var document = state.Fallback ? null : JsDocument.Parse(text, EcmaVersion.Latest, state.Module);
            if (document?.Error is not null) document = null;
            var functions = document?.Functions ?? JsTokenScan.FunctionDeclarations(text);
            JsFunction? function = null;
            if (state.Unit == WebTargetUnit.Function)
            {
                function = RefindFunction(functions, state);
                if (function is null) return Task.FromResult(NotFound(ctx));
                state.CurrentSpan = new TextSpanInfo(function.StartLine, function.EndLine);
            }
            if (!InFile(state.CurrentSpan, lineCount)) return Task.FromResult(NotFound(ctx));
            var span = state.CurrentSpan;

            Add("env", JsEnv(state), 4);
            Add("callees", Callees(ctx, state, document, functions, span, function, lines), 3);
            Add("file", FileDeclarations(functions, span, function), 2);
            Add("around", Around(lines, span, lineCount), 1);
            var level = state.TargetLevel is { } l ? JsLevels.Name(l) : "unknown";
            hint = $"JavaScript {level} ({(state.Module ? "module" : "script")}); jQuery {state.Jquery?.ToString() ?? "none"}; "
                + $"file encoding {state.EncodingName}";
            dropOrder = JsDropOrder;
        }

        ctx.Target = ctx.Target! with { MemberSpan = state.CurrentSpan };
        var regions = state.EditableRegions();
        var targetText = string.Join('\n', Lines(lines, state.CurrentSpan.StartLine, state.CurrentSpan.EndLine));
        var targetPart = new SlicePart("target", targetText, tokens.Count(targetText), int.MaxValue);

        var tooLarge = targetPart.Tokens > budget.Context;
        List<SlicePart> omitted = [];
        if (tooLarge)
        {
            omitted.AddRange(candidates);
            candidates.Clear();
        }
        else
        {
            foreach (var kind in dropOrder)
            {
                if (targetPart.Tokens + candidates.Sum(p => p.Tokens) <= budget.Context) break;
                var part = candidates.FirstOrDefault(p => p.Kind == kind);
                if (part is null) continue;
                candidates.Remove(part);
                omitted.Add(part);
            }
        }

        List<SlicePart> included = [targetPart, .. candidates];
        ctx.SliceResult = new SliceResult(new Slice(included, omitted, hint), regions, tooLarge);
        if (tooLarge)
        {
            ctx.Messages.Add($"대상이 문맥 예산보다 큽니다 ({targetPart.Tokens} > budget.context {budget.Context} 토큰). 범위를 나누거나 예산을 늘리세요.");
            return Task.FromResult(PhaseOutcome.BudgetExceeded);
        }
        return Task.FromResult(PhaseOutcome.Ok);
    }

    /// <summary>The function with the original's qualified name; among same-named ones, the one at the same position.</summary>
    private static JsFunction? RefindFunction(IReadOnlyList<JsFunction> functions, WebRunState state)
    {
        var original = state.Function!;
        var originals = state.OriginalJs?.Functions ?? JsTokenScan.FunctionDeclarations(state.OriginalText);
        return Refind(originals, functions, f => f.QualifiedName == original.QualifiedName, f => f.DeclLine == original.DeclLine);
    }

    /// <summary>The rule with the original's selector and container; among same ones, the one at the same position.</summary>
    private static CssRule? RefindRule(CssSheet sheet, WebRunState state)
    {
        var original = state.Rule!;
        var originals = (state.OriginalCss ?? CssSheet.Parse(state.OriginalText)).Rules;
        return Refind(originals, sheet.Rules,
            r => r.IsAtRule == original.IsAtRule && r.Selector == original.Selector && r.Container == original.Container,
            r => r.StartLine == original.StartLine);
    }

    private static T? Refind<T>(IReadOnlyList<T> before, IReadOnlyList<T> now, Func<T, bool> same, Func<T, bool> isOriginal) where T : class
    {
        var was = before.Where(same).ToList();
        var found = now.Where(same).ToList();
        var index = was.FindIndex(x => isOriginal(x));
        if (found.Count == 0 || found.Count != was.Count || index < 0)
            return found.Count == 1 ? found[0] : null;
        return found[index];
    }

    /// <summary>Language level with the features it forbids, jQuery version and encoding.</summary>
    private static string JsEnv(WebRunState state)
    {
        string level;
        if (state.TargetLevel is { } target)
        {
            var above = JsFeatures.Above(target);
            level = $"Language level: {JsLevels.Name(target)}";
            if (above.Count > 0) level += " — do not use " + string.Join(", ", above.Select(f => f.EnglishName));
        }
        else
        {
            level = "Language level: unknown (file did not parse)";
        }
        var jquery = state.Jquery is { } v ? $"jQuery {v}" : "jQuery: not found";
        return string.Join('\n', level, jquery, $"File encoding: {state.EncodingName}");
    }

    /// <summary>
    /// Declaration lines (with the comments directly above) of the functions the target calls: from the same file first, else
    /// from the site index under a <c>// &lt;relative path&gt;</c> header. Bodies are not included.
    /// </summary>
    private static string Callees(RunContext ctx, WebRunState state, JsDocument? document, IReadOnlyList<JsFunction> functions,
        TextSpanInfo span, JsFunction? target, string[] lines)
    {
        bool InSpan(int line) => line >= span.StartLine && line <= span.EndLine;
        IEnumerable<string> called = document is not null
            ? document.Calls.Where(c => InSpan(c.Line)).Select(c => c.Callee)
            : CalledInFallback(lines, span);

        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var selfPath = Relative(state.SiteRoot, ctx.FilePath);
        WebSiteIndexResult? index = null;
        var indexFailed = false;
        foreach (var callee in called)
        {
            if (callee.Length == 0 || !seen.Add(callee)) continue;
            var local = functions.FirstOrDefault(f => (f.QualifiedName == callee || f.Name == callee)
                && f != target && !InSpan(f.DeclLine));
            if (local is not null)
            {
                result.AddRange(Lines(lines, local.StartLine, local.DeclLine));
                continue;
            }
            if (functions.Any(f => f.QualifiedName == callee || f.Name == callee)) continue;
            if (index is null && !indexFailed)
            {
                try { index = state.SiteIndex.Value; }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { indexFailed = true; }
            }
            if (index is null) continue;
            foreach (var entry in index.Entries.Where(e => e.Name == callee
                && !string.Equals(e.RelativePath, selfPath, StringComparison.OrdinalIgnoreCase)))
            {
                result.Add($"// {entry.RelativePath}");
                result.Add(entry.Declaration);
            }
        }
        return string.Join('\n', result);
    }

    /// <summary>Called names in the target when the file does not parse: an identifier path followed by "(" in code (not strings or comments).</summary>
    private static IEnumerable<string> CalledInFallback(string[] lines, TextSpanInfo span)
    {
        var masked = JsTokenScan.Mask(string.Join('\n', lines)).Split('\n');
        for (var line = span.StartLine; line <= span.EndLine && line <= masked.Length; line++)
        {
            foreach (Match m in CallName.Matches(masked[line - 1]))
            {
                var name = m.Groups[1].Value;
                if (name is "function" or "if" or "for" or "while" or "switch" or "catch" or "return" or "typeof") continue;
                yield return name;
            }
        }
    }

    /// <summary>Declaration lines of the file's named functions, except the target and the functions inside the target.</summary>
    private static string FileDeclarations(IReadOnlyList<JsFunction> functions, TextSpanInfo span, JsFunction? target) =>
        string.Join('\n', functions
            .Where(f => f != target && (f.DeclLine < span.StartLine || f.DeclLine > span.EndLine))
            .Select(f => f.Declaration));

    /// <summary>Other rules (outside the target) that share a selector with the target rule(s), in full.</summary>
    private static string Related(CssSheet sheet, TextSpanInfo span, CssRule? target, string[] lines)
    {
        bool Inside(CssRule r) => r.StartLine >= span.StartLine && r.EndLine <= span.EndLine;
        var targets = target is not null ? [target] : sheet.Rules.Where(r => !r.IsAtRule && Inside(r)).ToList();
        var selectors = targets.SelectMany(r => r.Selectors).ToHashSet(StringComparer.Ordinal);
        var parts = sheet.Rules
            .Where(r => !r.IsAtRule && !Inside(r) && r.Selectors.Any(selectors.Contains))
            .Select(r => string.Join('\n', Lines(lines, r.StartLine, r.EndLine)));
        return string.Join('\n', parts);
    }

    /// <summary>Ten lines before and after the target, with a marker where the target sits.</summary>
    private static string Around(string[] lines, TextSpanInfo span, int lineCount)
    {
        var before = Lines(lines, Math.Max(1, span.StartLine - AroundLines), span.StartLine - 1).ToList();
        var after = Lines(lines, span.EndLine + 1, Math.Min(lineCount, span.EndLine + AroundLines)).ToList();
        if (before.Count == 0 && after.Count == 0)
            return "";
        return string.Join('\n', before.Append("... (target lines) ...").Concat(after));
    }

    private static IEnumerable<string> Lines(string[] lines, int from, int to)
    {
        for (var line = from; line <= to; line++)
            yield return line >= 1 && line <= lines.Length ? lines[line - 1] : "";
    }

    private static bool InFile(TextSpanInfo span, int lineCount) =>
        span.StartLine >= 1 && span.EndLine <= lineCount && span.EndLine >= span.StartLine;

    private static string Relative(string root, string path)
    {
        var r = root.Replace('\\', '/').TrimEnd('/');
        var p = path.Replace('\\', '/');
        return p.StartsWith(r + "/", StringComparison.OrdinalIgnoreCase) ? p[(r.Length + 1)..] : p;
    }

    private static string YesNo(bool value) => value ? "yes" : "no";

    private static PhaseOutcome NotFound(RunContext ctx)
    {
        ctx.Messages.Add($"수정 중인 대상 '{ctx.Target!.SymbolDisplay}'을(를) 다시 찾을 수 없습니다.");
        ctx.FailureKind = FailureKinds.TargetNotFound;
        return PhaseOutcome.NotFound;
    }
}
