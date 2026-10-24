using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;
using PellyCoder.Core.Rules;

namespace PellyCoder.Core.Tests.Rules;

public sealed class RuleSelectorTests
{
    private static readonly QwenTokenCounter Counter = QwenTokenCounter.Shared;

    private static ProjectRule Rule(int line, string text, params string[] tags) => new(line, tags, text, null);

    private static SlicePart Part(string kind, string text) => new(kind, text, Counter.Count(text), 1);

    private static Slice SliceOf(params SlicePart[] parts) => new(parts, Array.Empty<SlicePart>(), "C# 7.3");

    private static Slice Plain() => SliceOf(Part("usings", "using System;"), Part("target", "public int Add() { return 1; }"));

    [Fact]
    public void Select_AlwaysRulesAlwaysIncluded()
    {
        var sel = RuleSelector.Select([Rule(1, "항상 지킨다", "*")], Plain(), Counter, 150);
        Assert.Single(sel.Included);
        Assert.Empty(sel.Dropped);
        Assert.True(sel.Tokens > 0);
    }

    [Fact]
    public void Select_TagMatchesSliceTextCaseInsensitive()
    {
        var slice = SliceOf(Part("usings", "using Dapper;"), Part("target", "public async Task Run() { }"));
        var rules = new[] { Rule(1, "a", "dapper"), Rule(2, "b", "ASYNC"), Rule(3, "c", "zzz", "Task") };
        var sel = RuleSelector.Select(rules, slice, Counter, 150);
        Assert.Equal([1, 2, 3], sel.Included.Select(r => r.Line));
    }

    [Fact]
    public void Select_UnmatchedTagExcluded()
    {
        var sel = RuleSelector.Select([Rule(1, "a", "nomatch")], Plain(), Counter, 150);
        Assert.Empty(sel.Included);
        Assert.Empty(sel.Dropped);
    }

    [Fact]
    public void Select_OverBudgetDropsFromBottom()
    {
        var rules = new[] { Rule(1, "첫째 규칙", "*"), Rule(2, "둘째 규칙", "*"), Rule(3, "셋째 규칙", "*") };
        var two = Counter.Count("- 첫째 규칙\n") + Counter.Count("- 둘째 규칙\n");
        var sel = RuleSelector.Select(rules, Plain(), Counter, two);
        Assert.Equal([1, 2], sel.Included.Select(r => r.Line));
        Assert.Equal([3], sel.Dropped.Select(r => r.Line));
        Assert.Equal(two, sel.Tokens);
    }

    [Fact]
    public void Select_OverBudget_NoSkipAheadToSmallerRule()
    {
        var rules = new[] { Rule(1, "x", "*"), Rule(2, new string('길', 40), "*"), Rule(3, "y", "*") };
        var budget = Counter.Count("- x\n") + 1;
        var sel = RuleSelector.Select(rules, Plain(), Counter, budget);
        Assert.Equal([1], sel.Included.Select(r => r.Line));
        Assert.Equal([2, 3], sel.Dropped.Select(r => r.Line));
    }

    [Fact]
    public void Select_KeepsFileOrder()
    {
        var rules = new[] { Rule(5, "a", "using"), Rule(7, "b", "*"), Rule(9, "c", "Add") };
        var sel = RuleSelector.Select(rules, Plain(), Counter, 150);
        Assert.Equal([5, 7, 9], sel.Included.Select(r => r.Line));
    }

    [Fact]
    public void Select_Empty_ReturnsEmpty()
    {
        var sel = RuleSelector.Select([], Plain(), Counter, 150);
        Assert.Empty(sel.Included);
        Assert.Empty(sel.Dropped);
        Assert.Equal(0, sel.Tokens);
    }

    [Fact]
    public void Render_OneLinePerRule()
    {
        Assert.Equal("- a\n- b", RuleSelector.Render([Rule(1, "a", "*"), Rule(2, "b", "*")]));
    }
}
