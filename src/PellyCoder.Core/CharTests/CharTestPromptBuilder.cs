using PellyCoder.Core.Abstractions;
using PellyCoder.Core.Config;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.CharTests;

/// <summary>
/// Everything the <c>cs-test</c> prompt shows. <paramref name="ExampleTest"/> null falls back to the framework's example;
/// <paramref name="Hint"/> is the human's <c>[HINT]</c>. <see cref="LangVersion"/> fills <c>{LangVersion}</c> in the system prompt.
/// </summary>
public sealed record CharTestPromptInput(
    string TargetSource, string TargetDisplay, string TypeName, string Namespace, string Construction,
    string? TestHelpers, string? Dependencies, string? Dto, string? ExampleTest, IReadOnlyList<string> TestUsings,
    FrameworkInfo Framework, int Count, string? Hint, Feedback? Feedback)
{
    public string LangVersion { get; init; } = "latest";
}

/// <summary>
/// Assembles the raw ChatML prompt for <c>pelly test</c> (design §4.3). Parts 1–2 (target, construction) are always kept; when the
/// prompt is over <c>budget.InputTotal</c>, parts are dropped from the highest number down (7, 6, 5, 4, 3). The prompt ends at the
/// assistant start: the caller prefills <c>### USINGS</c>.
/// </summary>
public sealed class CharTestPromptBuilder(ITokenCounter tokens, BudgetOptions budget, QwenChatTemplate template, PromptResources resources)
{
    public const string RequestSentence = "Write {0} tests that pin the CURRENT behavior of the target. Assert what it does now, even if it looks wrong.";

    /// <summary>System prompt plus few-shot with the framework's test attribute, equality assertion and the language version filled in.</summary>
    public static string SystemText(PromptResources resources, FrameworkInfo framework, string langVersion) =>
        Fill(resources.SystemCsTest, framework, langVersion) + "\n\n" + Fill(resources.FewShotCsTest, framework, langVersion);

    private static string Fill(string text, FrameworkInfo framework, string langVersion) =>
        text.Replace("{TestAttribute}", framework.TestAttribute).Replace("{LangVersion}", langVersion)
            .Replace("{AssertEqual}", framework.AssertEqual("\"Hi Kim\"", "result"));

    public PromptBuildResult Build(CharTestPromptInput input)
    {
        var system = SystemText(resources, input.Framework, input.LangVersion);
        var hint = string.IsNullOrWhiteSpace(input.Hint) ? null : input.Hint.Trim();
        var request = string.Format(System.Globalization.CultureInfo.InvariantCulture, RequestSentence, input.Count)
            + (hint is null ? "" : "\nFocus: " + hint);

        var mandatory = new List<string>
        {
            "[READ-ONLY: target]", input.TargetSource,
            "[READ-ONLY: construction]", input.Construction,
        };
        // Priority order (3 kept longest, 7 dropped first).
        var optional = new List<(string Header, string Text)>();
        void AddPart(string header, string? text)
        {
            if (!string.IsNullOrWhiteSpace(text)) optional.Add((header, text));
        }
        AddPart("[READ-ONLY: test helpers]", input.TestHelpers);
        AddPart("[READ-ONLY: dependencies]", input.Dependencies);
        AddPart("[READ-ONLY: dto]", input.Dto);
        AddPart("[READ-ONLY: example test]", input.ExampleTest ?? input.Framework.ExampleTest);
        AddPart("[READ-ONLY: test usings]", input.TestUsings.Count == 0 ? null : string.Join("\n", input.TestUsings));

        string Assemble()
        {
            var lines = new List<string>
            {
                "### TARGET",
                $"{input.TargetDisplay} — type: {input.TypeName}, namespace: {input.Namespace}",
                "### CONTEXT",
            };
            lines.AddRange(mandatory);
            foreach (var (header, text) in optional)
            {
                lines.Add(header);
                lines.Add(text);
            }
            lines.Add("### REQUEST");
            lines.Add(request);
            if (input.Feedback is not null)
            {
                lines.Add("### FEEDBACK");
                lines.Add(input.Feedback.Text);
            }
            return template.Build(system, string.Join("\n", lines));
        }

        var prompt = Assemble();
        var total = tokens.Count(prompt);

        if (hint is not null)
        {
            var hintTokens = tokens.Count(hint);
            if (hintTokens > budget.Request)
                return new PromptBuildResult(prompt, total, PromptBudgetError.RequestTooLong, $"hint is {hintTokens} tokens, limit {budget.Request}");
        }

        if (input.Feedback is not null)
        {
            var feedbackTokens = tokens.Count(input.Feedback.Text);
            if (feedbackTokens > budget.Feedback)
                return new PromptBuildResult(prompt, total, PromptBudgetError.FeedbackTooLong, $"feedback is {feedbackTokens} tokens, limit {budget.Feedback}");
        }

        var mandatoryTokens = tokens.Count(string.Join("\n", mandatory));
        if (mandatoryTokens > budget.Context)
            return new PromptBuildResult(prompt, total, PromptBudgetError.TotalTooLong,
                $"target is too large: target and construction are {mandatoryTokens} tokens, limit {budget.Context}");

        while (total > budget.InputTotal && optional.Count > 0)
        {
            optional.RemoveAt(optional.Count - 1);
            prompt = Assemble();
            total = tokens.Count(prompt);
        }

        if (total > budget.InputTotal)
            return new PromptBuildResult(prompt, total, PromptBudgetError.TotalTooLong, $"prompt is {total} tokens, limit {budget.InputTotal}");

        return new PromptBuildResult(prompt, total, PromptBudgetError.None, null);
    }
}
