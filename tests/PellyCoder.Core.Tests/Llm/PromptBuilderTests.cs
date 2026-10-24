using PellyCoder.Core.Config;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Rules;

namespace PellyCoder.Core.Tests.Llm;

public sealed class PromptBuilderTests
{
    private static readonly QwenTokenCounter Counter = QwenTokenCounter.Shared;
    private static readonly QwenChatTemplate Template = QwenChatTemplate.LoadDefault();
    private static readonly PromptResources Resources = PromptResources.LoadDefault();

    private static PromptBuilder NewBuilder(BudgetOptions? budget = null) =>
        new(Template, Resources, Counter, budget ?? new BudgetOptions());

    private static EditTarget Target(string symbol = "Calc.Add(int, int)", string file = @"C:\repo\src\Calc.cs") =>
        new(EditKind.CSharp, file, symbol, new TextSpanInfo(3, 5), null);

    private static SlicePart Part(string kind, string text, int priority = 1) =>
        new(kind, text, Counter.Count(text), priority);

    private static Slice FullSlice(string hint = "C# 7.3") => new(
        new[]
        {
            Part("target", "public int Add(int a, int b)\n{\n    return a + b;\n}"),
            Part("usings", "using System;"),
            Part("fields", "private int _x;"),
            Part("signatures", "public int Sub(int a, int b);"),
            Part("helpers", "private int Twice(int v)\n{\n    return v * 2;\n}"),
        },
        Array.Empty<SlicePart>(), hint);

    private static string User(string prompt)
    {
        var start = prompt.IndexOf("<|im_start|>user\n", StringComparison.Ordinal) + "<|im_start|>user\n".Length;
        var end = prompt.IndexOf("<|im_end|>\n<|im_start|>assistant\n", start, StringComparison.Ordinal);
        return prompt[start..end];
    }

    [Fact]
    public void Prompt_DtoSectionOrder()
    {
        var slice = FullSlice();
        slice = slice with { Included = [.. slice.Included, Part("dto", "class Order: int OrderId")] };

        var u = User(NewBuilder().BuildCSharp(Target(), slice, "req", null).BasePrompt);

        string[] order = ["[READ-ONLY: fields]", "[READ-ONLY: dto]", "[READ-ONLY: signatures]", "[EDITABLE: usings]", "[EDITABLE: helper]", "[EDITABLE: target]"];
        var positions = order.Select(h => u.IndexOf(h, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.OrderBy(p => p), positions);
        Assert.Contains("[READ-ONLY: dto]\nclass Order: int OrderId\n", u);
    }

    [Fact]
    public void FewShot_TokenCount_Between200And260()
    {
        var n = Counter.Count(Resources.FewShotCs);
        Assert.InRange(n, 200, 260);
    }

    [Fact]
    public void Resources_VersionIsCsV2_AndHintReplaced()
    {
        Assert.Equal("cs-v2", Resources.Version);
        Assert.DoesNotContain("# version", Resources.SystemCs);
        Assert.DoesNotContain("# version", Resources.FewShotCs);
        Assert.Contains("{LanguageHint}", Resources.SystemCs);
    }

    [Fact]
    public void SystemPrompt_Rule3_NamesHelperRegion()
    {
        Assert.Contains("3. SEARCH must copy contiguous lines verbatim from [EDITABLE: target], [EDITABLE: usings] or [EDITABLE: helper]. Never copy from [READ-ONLY] sections.",
            Resources.SystemCs.Split('\n'));
    }

    [Fact]
    public void SystemPrompt_IsStableAcrossTargets()
    {
        var b = NewBuilder();
        var p1 = b.BuildCSharp(Target(), FullSlice(), "make it faster", null).BasePrompt;
        var p2 = b.BuildCSharp(Target("Other.Run()", "/x/Other.cs"), new Slice(new[] { Part("target", "void Run() { }") }, Array.Empty<SlicePart>(), "C# 7.3"),
            "different request", new Feedback("compile error", 3)).BasePrompt;
        const string marker = "<|im_end|>\n<|im_start|>user\n";
        var s1 = p1[..p1.IndexOf(marker, StringComparison.Ordinal)];
        var s2 = p2[..p2.IndexOf(marker, StringComparison.Ordinal)];
        Assert.Equal(s1, s2);
        Assert.Contains("Target language: C# 7.3.", s1);
        Assert.StartsWith("<|im_start|>system\nYou are a code editor", s1);
        Assert.DoesNotContain("{LanguageHint}", s1);
    }

    [Fact]
    public void User_HasExactLayout()
    {
        var r = NewBuilder().BuildCSharp(Target(), FullSlice(), "  add a guard \n", new Feedback("CS1002 ; expected", 5));
        Assert.Equal(PromptBudgetError.None, r.Error);
        var expected = string.Join("\n",
            "### TARGET",
            "Calc.Add(int, int) \u2014 file: Calc.cs",
            "### CONTEXT",
            "[READ-ONLY: fields]",
            "private int _x;",
            "[READ-ONLY: signatures]",
            "public int Sub(int a, int b);",
            "[EDITABLE: usings]",
            "using System;",
            "[EDITABLE: helper]",
            "private int Twice(int v)\n{\n    return v * 2;\n}",
            "[EDITABLE: target]",
            "public int Add(int a, int b)\n{\n    return a + b;\n}",
            "### REQUEST",
            "add a guard",
            "### FEEDBACK",
            "CS1002 ; expected");
        Assert.Equal(expected, User(r.BasePrompt));
        Assert.EndsWith("<|im_start|>assistant\n", r.BasePrompt);
        Assert.Equal(Counter.Count(r.BasePrompt), r.Tokens);
    }

    [Fact]
    public void User_SectionsInDesignOrder()
    {
        var u = User(NewBuilder().BuildCSharp(Target(), FullSlice(), "req", new Feedback("fb", 1)).BasePrompt);
        var t = u.IndexOf("### TARGET", StringComparison.Ordinal);
        var c = u.IndexOf("### CONTEXT", StringComparison.Ordinal);
        var r = u.IndexOf("### REQUEST", StringComparison.Ordinal);
        var f = u.IndexOf("### FEEDBACK", StringComparison.Ordinal);
        Assert.True(t >= 0 && t < c && c < r && r < f);
    }

    [Fact]
    public void NoFeedback_NoFeedbackSection()
    {
        var p = NewBuilder().BuildCSharp(Target(), FullSlice(), "req", null).BasePrompt;
        Assert.DoesNotContain("### FEEDBACK", User(p));
    }

    [Fact]
    public void OmittedParts_AreNotInPrompt()
    {
        var slice = new Slice(
            new[] { Part("target", "void T() { }") },
            new[] { Part("fields", "private int _omittedField;") }, "C# 7.3");
        var u = User(NewBuilder().BuildCSharp(Target(), slice, "req", null).BasePrompt);
        Assert.DoesNotContain("_omittedField", u);
        Assert.DoesNotContain("[READ-ONLY: fields]", u);
        Assert.DoesNotContain("[EDITABLE: usings]", u);
        Assert.DoesNotContain("[EDITABLE: helper]", u);
        Assert.Contains("[EDITABLE: target]", u);
    }

    [Fact]
    public void RequestOver200Tokens_IsRequestTooLong()
    {
        var request = string.Join(' ', Enumerable.Range(0, 300).Select(i => "word" + i));
        var r = NewBuilder().BuildCSharp(Target(), FullSlice(), request, null);
        Assert.Equal(PromptBudgetError.RequestTooLong, r.Error);
        Assert.Contains("200", r.Detail);
        Assert.False(string.IsNullOrEmpty(r.BasePrompt));
    }

    [Fact]
    public void FeedbackOver296Tokens_IsFeedbackTooLong()
    {
        var text = string.Join(' ', Enumerable.Range(0, 400).Select(i => "err" + i));
        var r = NewBuilder().BuildCSharp(Target(), FullSlice(), "req", new Feedback(text, Counter.Count(text)));
        Assert.Equal(PromptBudgetError.FeedbackTooLong, r.Error);
        Assert.Contains("296", r.Detail);
    }

    [Fact]
    public void TotalOver4096_IsTotalTooLong()
    {
        var big = string.Join('\n', Enumerable.Range(0, 1200).Select(i => $"    var value{i} = Compute({i});"));
        var slice = new Slice(new[] { Part("target", big) }, Array.Empty<SlicePart>(), "C# 7.3");
        var r = NewBuilder().BuildCSharp(Target(), slice, "req", null);
        Assert.Equal(PromptBudgetError.TotalTooLong, r.Error);
        Assert.Contains("4096", r.Detail);
        Assert.True(r.Tokens > 4096);
    }

    [Fact]
    public void TotalCountIncludesTemplateTokens()
    {
        var b = NewBuilder(new BudgetOptions { InputTotal = 1 });
        var r = b.BuildCSharp(Target(), FullSlice(), "req", null);
        Assert.Equal(PromptBudgetError.TotalTooLong, r.Error);
    }

    private static string System(string prompt) => prompt[..prompt.IndexOf("<|im_start|>user\n", StringComparison.Ordinal)];

    [Fact]
    public void BuildCSharp_InsertsRulesBetweenContextAndRequest()
    {
        var rules = new[] { new ProjectRule(3, ["*"], "주석은 한국어로 작성한다", null) };
        var with = NewBuilder().BuildCSharp(Target(), FullSlice(), "req", null, rules);
        var without = NewBuilder().BuildCSharp(Target(), FullSlice(), "req", null);

        Assert.Contains("### RULES\n- 주석은 한국어로 작성한다\n### REQUEST", User(with.BasePrompt));
        Assert.True(User(with.BasePrompt).IndexOf("[EDITABLE: target]", StringComparison.Ordinal) < User(with.BasePrompt).IndexOf("### RULES", StringComparison.Ordinal));
        Assert.Equal(System(without.BasePrompt), System(with.BasePrompt));
        Assert.Single(with.Rules!.Included);
        Assert.True(with.Tokens > without.Tokens);
    }

    [Fact]
    public void BuildCSharp_NoRules_NoRulesSection()
    {
        var plain = NewBuilder().BuildCSharp(Target(), FullSlice(), "req", null);
        Assert.DoesNotContain("### RULES", plain.BasePrompt);
        Assert.Null(plain.Rules);

        var unmatched = NewBuilder().BuildCSharp(Target(), FullSlice(), "req", null, [new ProjectRule(1, ["nomatch"], "x", null)]);
        Assert.Equal(plain.BasePrompt, unmatched.BasePrompt);
        Assert.NotNull(unmatched.Rules);
        Assert.Empty(unmatched.Rules!.Included);
    }
}
