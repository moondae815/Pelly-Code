using PellyCoder.Core.Config;
using PellyCoder.Core.Llm;

namespace PellyCoder.Core.Tests.Llm;

/// <summary>The system prompt and few-shot must fit the default <c>budget.system</c>: the rules budget was split out of it, so a longer prompt would silently eat the margin.</summary>
public sealed class SystemPromptBudgetTests
{
    private static readonly QwenChatTemplate Template = QwenChatTemplate.LoadDefault();
    private static readonly PromptResources Resources = PromptResources.LoadDefault();

    private static int SystemTokens(string system, string fewShot, string hint) =>
        QwenTokenCounter.Shared.Count(Template.SystemStart + system.Replace("{LanguageHint}", hint) + "\n\n" + fewShot + Template.SystemEnd);

    [Fact]
    public void CSharp_SystemAndFewShot_FitDefaultSystemBudget()
    {
        var tokens = SystemTokens(Resources.SystemCs, Resources.FewShotCs, "C# 12, .NET 8");

        Assert.True(tokens <= new BudgetOptions().System, $"C# system prompt is {tokens} tokens; budget.system is {new BudgetOptions().System}");
    }

    [Fact]
    public void CSharpGroup_SystemAndFewShot_FitDefaultSystemBudget()
    {
        var tokens = SystemTokens(Resources.SystemCsGroup, Resources.FewShotCs, "C# 12, .NET 8");

        Assert.True(tokens <= new BudgetOptions().System, $"C# group system prompt is {tokens} tokens; budget.system is {new BudgetOptions().System}");
    }

    [Fact]
    public void Sql_SystemAndFewShot_FitDefaultSystemBudget()
    {
        var tokens = SystemTokens(Resources.SystemSql, Resources.FewShotSql, "T-SQL, SQL Server compatibility level 160");

        Assert.True(tokens <= new BudgetOptions().System, $"SQL system prompt is {tokens} tokens; budget.system is {new BudgetOptions().System}");
    }
}
