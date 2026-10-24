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

    public PromptBuildResult BuildCSharp(EditTarget target, Slice slice, string request, Feedback? feedback, IReadOnlyList<ProjectRule>? rules = null)
    {
        var requestText = request.Trim();
        var system = resources.SystemCs.Replace(HintPlaceholder, slice.LanguageHint) + "\n\n" + resources.FewShotCs;

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

    private RuleSelection? AddRules(List<string> lines, IReadOnlyList<ProjectRule>? rules, Slice slice)
    {
        if (rules is null) return null;
        var selection = RuleSelector.Select(rules, slice, tokens, budget.Rules);
        if (selection.Included.Count > 0)
        {
            lines.Add("### RULES");
            lines.Add(RuleSelector.Render(selection.Included));
        }
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
