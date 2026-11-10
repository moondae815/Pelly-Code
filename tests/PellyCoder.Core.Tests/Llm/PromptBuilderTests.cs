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
    public void Group_prompt_differs_only_in_rule_5()
    {
        var normal = Resources.SystemCs.Split('\n');
        var group = Resources.SystemCsGroup.Split('\n');
        Assert.Equal(normal.Length, group.Length);
        var differing = normal.Zip(group).Where(p => p.First != p.Second).ToList();
        var pair = Assert.Single(differing);
        Assert.StartsWith("5.", pair.First);
        Assert.StartsWith("5.", pair.Second);
        Assert.Equal("5. Change this member's signature only as far as the request asks; you may rename it. Do not change any other member. You may add a private helper method right after the target member.", pair.Second);
    }

    [Fact]
    public void Group_flag_selects_group_system_prompt()
    {
        var b = NewBuilder();
        var group = b.BuildCSharp(Target(), FullSlice(), "rename it", null, null, group: true).BasePrompt;
        var normal = b.BuildCSharp(Target(), FullSlice(), "rename it", null).BasePrompt;
        Assert.Contains("you may rename it", group);
        Assert.DoesNotContain("you may rename it", normal);
        Assert.Contains("Do not change the member's signature", normal);
    }

    [Fact]
    public void Group_flag_changes_only_the_system_part()
    {
        // The group prompt is its own cache prefix; everything after the system part must be what a v1 run sends.
        var b = NewBuilder();
        var rules = new[] { new ProjectRule(3, ["*"], "주석은 한국어로 작성한다", null) };
        var feedback = new Feedback("BUILD FAILED (1 new errors)\nsrc/Calc.cs(4): CS0029: Cannot convert", 12);
        var group = b.BuildCSharp(Target(), FullSlice(), "rename it", feedback, rules, group: true);
        var normal = b.BuildCSharp(Target(), FullSlice(), "rename it", feedback, rules);

        const string marker = "<|im_end|>\n<|im_start|>user\n";
        var groupUser = group.BasePrompt[group.BasePrompt.IndexOf(marker, StringComparison.Ordinal)..];
        var normalUser = normal.BasePrompt[normal.BasePrompt.IndexOf(marker, StringComparison.Ordinal)..];
        Assert.Equal(normalUser, groupUser);
        Assert.Equal(User(normal.BasePrompt), User(group.BasePrompt));
        Assert.Contains("주석은 한국어로 작성한다", User(group.BasePrompt));
        Assert.NotEqual(normal.BasePrompt, group.BasePrompt);
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

    private static Slice AspSlice() => new(
        new[]
        {
            Part("target", "If q <> \"\" Then\n    w = w & \" AND Name LIKE '%\" & Replace(q, \"'\", \"''\") & \"%'\"\nEnd If"),
            Part("page", "<%@ Language=\"VBScript\" CodePage=949 %>\nOption Explicit: yes\nEncoding: CP949"),
            Part("procedures", "Function Pad(v)"),
            Part("variables", "Dim q, w"),
            Part("sql", "PROCEDURE dbo.usp_X (@Id int)"),
            Part("around", "<table>"),
        },
        Array.Empty<SlicePart>(), "Classic ASP (VBScript); file encoding CP949; Option Explicit: yes");

    private static EditTarget AspTarget() => new(EditKind.Asp, @"C:\site\admin\page.asp", "줄 10-12", new TextSpanInfo(10, 12), null);

    [Fact]
    public void BuildAsp_HeaderOrder()
    {
        var result = NewBuilder().BuildAsp(AspTarget(), AspSlice(), "작성자 조건을 추가한다", null);

        Assert.Equal(PromptBudgetError.None, result.Error);
        var user = User(result.BasePrompt);
        Assert.StartsWith("### TARGET\n줄 10-12 — file: page.asp\n### CONTEXT\n", user);
        string[] headers = ["[READ-ONLY: page]", "[READ-ONLY: procedures]", "[READ-ONLY: variables]", "[READ-ONLY: sql]",
            "[READ-ONLY: around]", "[EDITABLE: target]", "### REQUEST"];
        var positions = headers.Select(h => user.IndexOf(h, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
        Assert.Contains("Target: Classic ASP (VBScript); file encoding CP949; Option Explicit: yes", System(result.BasePrompt));
        Assert.DoesNotContain("{LanguageHint}", result.BasePrompt);
        Assert.DoesNotContain("Site SQL functions", result.BasePrompt);
    }

    [Fact]
    public void BuildAsp_SiteSanitizersLine()
    {
        var rules = new[] { new ProjectRule(1, ["*"], "주석은 한국어로 작성한다", null) };
        var with = NewBuilder().BuildAsp(AspTarget(), AspSlice(), "req", null, rules, ["SafeText", "SafeNum"]);
        var without = NewBuilder().BuildAsp(AspTarget(), AspSlice(), "req", null, null, ["SafeText", "SafeNum"]);

        Assert.Contains("### RULES\nSite SQL functions: SafeText, SafeNum\n- 주석은 한국어로 작성한다\n### REQUEST", User(with.BasePrompt));
        Assert.Contains("### RULES\nSite SQL functions: SafeText, SafeNum\n### REQUEST", User(without.BasePrompt));
        Assert.Equal(System(without.BasePrompt), System(with.BasePrompt));
        Assert.DoesNotContain("Site SQL functions", NewBuilder().BuildAsp(AspTarget(), AspSlice(), "req", null, null, []).BasePrompt);
    }

    private static Slice JsSlice() => new(
        new[]
        {
            Part("target", "function saveForm(id) {\n  return true;\n}"),
            Part("env", "Language level: ES5 — do not use let/const\njQuery 1.8.3\nFile encoding: CP949"),
            Part("callees", "function fmtPrice(n) {"),
            Part("file", "function other() {"),
            Part("around", "var a = 1;"),
        },
        Array.Empty<SlicePart>(), "JavaScript ES5 (script); jQuery 1.8.3; file encoding CP949");

    [Fact]
    public void BuildWeb_Js_HeaderOrder()
    {
        var target = new EditTarget(EditKind.Js, @"C:\site\js\order.js", "function saveForm", new TextSpanInfo(6, 13), null);
        var rules = new[] { new ProjectRule(1, ["*"], "주석은 한국어로 작성한다", null) };

        var result = NewBuilder().BuildWeb(target, JsSlice(), "저장 전에 확인 창을 띄운다", new Feedback("fix it", 2), rules);

        Assert.Equal(PromptBudgetError.None, result.Error);
        var user = User(result.BasePrompt);
        Assert.StartsWith("### TARGET\nfunction saveForm — file: order.js\n### CONTEXT\n", user);
        string[] headers = ["[READ-ONLY: env]", "[READ-ONLY: callees]", "[READ-ONLY: file]", "[READ-ONLY: around]",
            "[EDITABLE: target]", "### RULES", "### REQUEST", "### FEEDBACK"];
        var positions = headers.Select(h => user.IndexOf(h, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
        var system = System(result.BasePrompt);
        Assert.Contains("Target: JavaScript ES5 (script); jQuery 1.8.3; file encoding CP949", system);
        Assert.Contains("[READ-ONLY: callees]", system);
        Assert.DoesNotContain("{LanguageHint}", result.BasePrompt);
        Assert.DoesNotContain("VBScript", system);
    }

    [Fact]
    public void BuildWeb_Css_HeaderOrder()
    {
        var target = new EditTarget(EditKind.Css, "/site/css/site.css", ".card", new TextSpanInfo(4, 6), null);
        var slice = new Slice(
            new[]
            {
                Part("target", ".card {\n  margin: 0;\n}"),
                Part("env", "File encoding: UTF-8\nIE hacks used in this file: no\nVendor prefixes used in this file: no"),
                Part("related", ".box, .card {\n  color: red;\n}"),
                Part("around", ".other {"),
            },
            Array.Empty<SlicePart>(), "CSS; file encoding UTF-8; IE hacks: no");

        var result = NewBuilder().BuildWeb(target, slice, "여백을 4px로 한다", null);

        Assert.Equal(PromptBudgetError.None, result.Error);
        var user = User(result.BasePrompt);
        Assert.StartsWith("### TARGET\n.card — file: site.css\n### CONTEXT\n", user);
        string[] headers = ["[READ-ONLY: env]", "[READ-ONLY: related]", "[READ-ONLY: around]", "[EDITABLE: target]", "### REQUEST"];
        var positions = headers.Select(h => user.IndexOf(h, StringComparison.Ordinal)).ToList();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
        var system = System(result.BasePrompt);
        Assert.Contains("Target: CSS; file encoding UTF-8; IE hacks: no", system);
        Assert.Contains("expression()", system);
        Assert.DoesNotContain("JavaScript ES5", system);
        Assert.DoesNotContain("### FEEDBACK", user);
    }

    private const string NoRename = "Do not rename the target function and do not change its parameters.";
    private const string MayRename = "Rename the target function or change its parameters only when the request asks for it; other tasks update its callers.";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BuildWeb_Js_SignatureAllowed_SentenceReplaced(bool page)
    {
        var target = new EditTarget(EditKind.Js, @"C:\site\js\order.js", "function saveForm", new TextSpanInfo(6, 13), null);
        var allowed = NewBuilder().BuildWeb(target, JsSlice(), "이름을 바꾼다", null, page: page, allowSignature: true).BasePrompt;
        Assert.Contains(MayRename, allowed);
        Assert.DoesNotContain(NoRename, allowed);
        Assert.Contains("Do not add new top-level declarations outside the target function.", allowed);
        var plain = NewBuilder().BuildWeb(target, JsSlice(), "이름을 바꾼다", null, page: page).BasePrompt;
        Assert.Contains(NoRename, plain);
        Assert.DoesNotContain(MayRename, plain);
    }
}
