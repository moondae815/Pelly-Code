using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Models;
using PellyCoder.Core.Rules;
using PellyCoder.Core.Sql;

namespace PellyCoder.Core.Llm;

public enum PromptBudgetError { None, RequestTooLong, FeedbackTooLong, TotalTooLong }

public sealed record PromptBuildResult(string BasePrompt, int Tokens, PromptBudgetError Error, string? Detail, RuleSelection? Rules = null);

/// <summary>
/// Assembles the raw ChatML prompt. The system part (rules + few-shot) depends only on the language hint,
/// so Ollama's prefix cache is reused across runs (S2).
/// </summary>
public sealed class PromptBuilder(QwenChatTemplate template, PromptResources resources, ITokenCounter tokens, BudgetOptions budget)
{
    private const string HintPlaceholder = "{LanguageHint}";

    // Order of parts inside ### CONTEXT: (part kind, header).
    private static readonly (string Kind, string Header)[] ContextOrder =
    {
        ("fields", "[READ-ONLY: fields]"),
        ("dto", "[READ-ONLY: dto]"),
        ("signatures", "[READ-ONLY: signatures]"),
        ("usings", "[EDITABLE: usings]"),
        ("helpers", "[EDITABLE: helper]"),
        ("target", "[EDITABLE: target]"),
    };

    private static readonly (string Kind, string Header)[] SqlContextOrder =
    {
        ("schema", "[READ-ONLY: schema]"),
        ("target", "[EDITABLE: target]"),
    };

    private static readonly (string Kind, string Header)[] AspContextOrder =
    {
        ("env", "[READ-ONLY: env]"),
        ("page", "[READ-ONLY: page]"),
        ("procedures", "[READ-ONLY: procedures]"),
        ("variables", "[READ-ONLY: variables]"),
        ("sql", "[READ-ONLY: sql]"),
        ("around", "[READ-ONLY: around]"),
        ("target", "[EDITABLE: target]"),
    };

    private static readonly (string Kind, string Header)[] JsContextOrder =
    {
        ("env", "[READ-ONLY: env]"),
        ("callees", "[READ-ONLY: callees]"),
        ("file", "[READ-ONLY: file]"),
        ("around", "[READ-ONLY: around]"),
        ("target", "[EDITABLE: target]"),
    };

    private static readonly (string Kind, string Header)[] JsPageContextOrder =
    {
        ("env", "[READ-ONLY: env]"),
        ("callees", "[READ-ONLY: callees]"),
        ("page", "[READ-ONLY: page]"),
        ("html", "[READ-ONLY: html]"),
        ("around", "[READ-ONLY: around]"),
        ("target", "[EDITABLE: target]"),
    };

    private static readonly (string Kind, string Header)[] CssContextOrder =
    {
        ("env", "[READ-ONLY: env]"),
        ("related", "[READ-ONLY: related]"),
        ("around", "[READ-ONLY: around]"),
        ("target", "[EDITABLE: target]"),
    };

    public PromptBuildResult BuildCSharp(EditTarget target, Slice slice, string request, Feedback? feedback, IReadOnlyList<ProjectRule>? rules = null, bool group = false)
    {
        var requestText = request.Trim();
        var system = (group ? resources.SystemCsGroup : resources.SystemCs).Replace(HintPlaceholder, slice.LanguageHint) + "\n\n" + resources.FewShotCs;

        var lines = new List<string>
        {
            "### TARGET",
            $"{target.SymbolDisplay} — file: {Path.GetFileName(target.FilePath.Replace('\\', '/'))}",
            "### CONTEXT",
        };
        foreach (var (kind, header) in ContextOrder)
        {
            var part = slice.Included.FirstOrDefault(p => p.Kind == kind);
            if (part is null) continue;
            lines.Add(header);
            lines.Add(part.Text);
        }
        var selection = AddRules(lines, rules, slice);
        lines.Add("### REQUEST");
        lines.Add(requestText);
        if (feedback is not null)
        {
            lines.Add("### FEEDBACK");
            lines.Add(feedback.Text);
        }

        return Finish(system, lines, requestText, feedback, selection);
    }

    public PromptBuildResult BuildSql(EditTarget target, SqlObjectKind kind, Slice slice, string request, Feedback? feedback, IReadOnlyList<ProjectRule>? rules = null)
    {
        var requestText = request.Trim();
        var system = resources.SystemSql.Replace(HintPlaceholder, slice.LanguageHint) + "\n\n" + resources.FewShotSql;

        var lines = new List<string>
        {
            "### TARGET",
            $"{target.SymbolDisplay} ({KindWord(kind)}) — file: {Path.GetFileName(target.FilePath.Replace('\\', '/'))}",
            "### CONTEXT",
        };
        foreach (var (partKind, header) in SqlContextOrder)
        {
            var part = slice.Included.FirstOrDefault(p => p.Kind == partKind);
            if (part is null) continue;
            lines.Add(header);
            lines.Add(part.Text);
        }
        var selection = AddRules(lines, rules, slice);
        lines.Add("### REQUEST");
        lines.Add(requestText);
        if (feedback is not null)
        {
            lines.Add("### FEEDBACK");
            lines.Add(feedback.Text);
        }
        return Finish(system, lines, requestText, feedback, selection);
    }

    /// <param name="sqlSanitizers"><c>asp.sqlSanitizers</c>: named on a "Site SQL functions" line at the top of <c>### RULES</c> (system rule 7).</param>
    public PromptBuildResult BuildAsp(EditTarget target, Slice slice, string request, Feedback? feedback, IReadOnlyList<ProjectRule>? rules = null,
        IReadOnlyList<string>? sqlSanitizers = null)
    {
        var requestText = request.Trim();
        var system = resources.SystemAsp.Replace(HintPlaceholder, slice.LanguageHint) + "\n\n" + resources.FewShotAsp;

        var lines = new List<string>
        {
            "### TARGET",
            $"{target.SymbolDisplay} — file: {Path.GetFileName(target.FilePath.Replace('\\', '/'))}",
            "### CONTEXT",
        };
        foreach (var (partKind, header) in AspContextOrder)
        {
            var part = slice.Included.FirstOrDefault(p => p.Kind == partKind);
            if (part is null) continue;
            lines.Add(header);
            lines.Add(part.Text);
        }
        var siteLine = sqlSanitizers is { Count: > 0 } ? $"Site SQL functions: {string.Join(", ", sqlSanitizers)}" : null;
        var selection = AddRules(lines, rules, slice, siteLine);
        lines.Add("### REQUEST");
        lines.Add(requestText);
        if (feedback is not null)
        {
            lines.Add("### FEEDBACK");
            lines.Add(feedback.Text);
        }
        return Finish(system, lines, requestText, feedback, selection);
    }

    internal const string SignatureForbiddenRule = "Do not rename the target function and do not change its parameters.";
    internal const string SignatureAllowedRule =
        "Rename the target function or change its parameters only when the request asks for it; other tasks update its callers.";

    /// <summary>A <c>pelly js</c> or <c>pelly css</c> prompt; <paramref name="target"/>'s kind picks the resources and part order.</summary>
    /// <param name="page">The run edits a block of an ASP page: the page prompts and the page context order are used.</param>
    /// <param name="allowSignature">
    /// The target function may change its name and parameters (W3 design §4.2): the JS rule forbidding it is swapped for
    /// <see cref="SignatureAllowedRule"/>. The prompt files and their versions stay the same.
    /// </param>
    public PromptBuildResult BuildWeb(EditTarget target, Slice slice, string request, Feedback? feedback, IReadOnlyList<ProjectRule>? rules = null,
        bool page = false, bool allowSignature = false)
    {
        var css = target.Kind == EditKind.Css;
        var requestText = request.Trim();
        var (systemText, fewShotText) = (css, page) switch
        {
            (true, true) => (resources.SystemCssPage, resources.FewShotCssPage),
            (true, false) => (resources.SystemCss, resources.FewShotCss),
            (false, true) => (resources.SystemJsPage, resources.FewShotJsPage),
            _ => (resources.SystemJs, resources.FewShotJs),
        };
        if (allowSignature && !css) systemText = systemText.Replace(SignatureForbiddenRule, SignatureAllowedRule);
        var system = systemText.Replace(HintPlaceholder, slice.LanguageHint) + "\n\n" + fewShotText;

        var lines = new List<string>
        {
            "### TARGET",
            $"{target.SymbolDisplay} — file: {Path.GetFileName(target.FilePath.Replace('\\', '/'))}",
            "### CONTEXT",
        };
        foreach (var (partKind, header) in css ? CssContextOrder : page ? JsPageContextOrder : JsContextOrder)
        {
            var part = slice.Included.FirstOrDefault(p => p.Kind == partKind);
            if (part is null) continue;
            lines.Add(header);
            lines.Add(part.Text);
        }
        var selection = AddRules(lines, rules, slice);
        lines.Add("### REQUEST");
        lines.Add(requestText);
        if (feedback is not null)
        {
            lines.Add("### FEEDBACK");
            lines.Add(feedback.Text);
        }
        return Finish(system, lines, requestText, feedback, selection);
    }

    /// <param name="leadLine">A fixed line placed first under <c>### RULES</c> (the section is written even when no rule is selected).</param>
    private RuleSelection? AddRules(List<string> lines, IReadOnlyList<ProjectRule>? rules, Slice slice, string? leadLine = null)
    {
        var selection = rules is null ? null : RuleSelector.Select(rules, slice, tokens, budget.Rules);
        if (leadLine is null && selection is not { Included.Count: > 0 }) return selection;
        lines.Add("### RULES");
        if (leadLine is not null) lines.Add(leadLine);
        if (selection is { Included.Count: > 0 }) lines.Add(RuleSelector.Render(selection.Included));
        return selection;
    }

    private static string KindWord(SqlObjectKind kind) => kind switch
    {
        SqlObjectKind.Procedure => "PROCEDURE",
        SqlObjectKind.Function => "FUNCTION",
        _ => "VIEW",
    };

    private PromptBuildResult Finish(string system, List<string> lines, string requestText, Feedback? feedback, RuleSelection? selection)
    {
        var prompt = template.Build(system, string.Join("\n", lines));
        var total = tokens.Count(prompt);

        var requestTokens = tokens.Count(requestText);
        if (requestTokens > budget.Request)
            return new PromptBuildResult(prompt, total, PromptBudgetError.RequestTooLong,
                $"request is {requestTokens} tokens, limit {budget.Request}", selection);

        if (feedback is not null)
        {
            var feedbackTokens = tokens.Count(feedback.Text);
            if (feedbackTokens > budget.Feedback)
                return new PromptBuildResult(prompt, total, PromptBudgetError.FeedbackTooLong,
                    $"feedback is {feedbackTokens} tokens, limit {budget.Feedback}", selection);
        }

        if (total > budget.InputTotal)
            return new PromptBuildResult(prompt, total, PromptBudgetError.TotalTooLong,
                $"prompt is {total} tokens, limit {budget.InputTotal}", selection);

        return new PromptBuildResult(prompt, total, PromptBudgetError.None, null, selection);
    }
}
