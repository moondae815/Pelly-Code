using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Llm;

public enum PromptBudgetError { None, RequestTooLong, FeedbackTooLong, TotalTooLong }

public sealed record PromptBuildResult(string BasePrompt, int Tokens, PromptBudgetError Error, string? Detail);

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
        ("signatures", "[READ-ONLY: signatures]"),
        ("usings", "[EDITABLE: usings]"),
        ("target", "[EDITABLE: target]"),
    };

    public PromptBuildResult BuildCSharp(EditTarget target, Slice slice, string request, Models.Feedback? feedback)
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
        lines.Add("### REQUEST");
        lines.Add(requestText);
        if (feedback is not null)
        {
            lines.Add("### FEEDBACK");
            lines.Add(feedback.Text);
        }

        var prompt = template.Build(system, string.Join("\n", lines));
        var total = tokens.Count(prompt);

        var requestTokens = tokens.Count(requestText);
        if (requestTokens > budget.Request)
            return new PromptBuildResult(prompt, total, PromptBudgetError.RequestTooLong,
                $"request is {requestTokens} tokens, limit {budget.Request}");

        if (feedback is not null)
        {
            var feedbackTokens = tokens.Count(feedback.Text);
            if (feedbackTokens > budget.Feedback)
                return new PromptBuildResult(prompt, total, PromptBudgetError.FeedbackTooLong,
                    $"feedback is {feedbackTokens} tokens, limit {budget.Feedback}");
        }

        if (total > budget.InputTotal)
            return new PromptBuildResult(prompt, total, PromptBudgetError.TotalTooLong,
                $"prompt is {total} tokens, limit {budget.InputTotal}");

        return new PromptBuildResult(prompt, total, PromptBudgetError.None, null);
    }
}
