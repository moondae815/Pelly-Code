using System.IO.Abstractions;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CharTests;
using PellyCoder.Core.Config;
using PellyCoder.Core.CSharp;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Tests.CharTests;

public sealed class CharTestPromptBuilderTests
{
    private static readonly QwenTokenCounter Counter = QwenTokenCounter.Shared;
    private static readonly QwenChatTemplate Template = QwenChatTemplate.LoadDefault();
    private static readonly PromptResources Resources = PromptResources.LoadDefault();
    private static readonly FrameworkInfo Xunit = TestFrameworks.Info(TestFramework.XunitV3);

    private static CharTestPromptBuilder NewBuilder(BudgetOptions? budget = null) =>
        new(Counter, budget ?? new BudgetOptions(), Template, Resources);

    private static CharTestPromptInput Input(
        string target = "public int Add(int a, int b)\n{\n    return a + b;\n}",
        string? helpers = "private static Calc NewCalc();",
        string? deps = "interface ILog { void Write(string message); }",
        string? dto = "class Money: decimal Amount",
        string? example = "[Fact]\npublic void Add_Two_Sums() { Assert.Equal(3, new Calc().Add(1, 2)); }",
        IReadOnlyList<string>? usings = null,
        FrameworkInfo? framework = null,
        string? hint = null,
        Feedback? feedback = null) =>
        new(target, "Calc.Add(int, int)", "Calc", "Demo", "public Calc(ILog log);\nprivate readonly ILog _log;",
            helpers, deps, dto, example, usings ?? ["using System;", "using Xunit;"], framework ?? Xunit, 3, hint, feedback);

    private static string User(string prompt)
    {
        var start = prompt.IndexOf(Template.UserStart, StringComparison.Ordinal) + Template.UserStart.Length;
        var end = prompt.IndexOf(Template.UserEnd + Template.AssistantStart, start, StringComparison.Ordinal);
        return prompt[start..end];
    }

    private static readonly string[] Headers =
    [
        "[READ-ONLY: target]", "[READ-ONLY: construction]", "[READ-ONLY: test helpers]", "[READ-ONLY: dependencies]",
        "[READ-ONLY: dto]", "[READ-ONLY: example test]", "[READ-ONLY: test usings]", "### REQUEST",
    ];

    [Fact]
    public void Build_ContainsPartsInOrder()
    {
        var result = NewBuilder().Build(Input());

        Assert.Equal(PromptBudgetError.None, result.Error);
        var u = User(result.BasePrompt);
        var positions = Headers.Select(h => u.IndexOf(h, StringComparison.Ordinal)).ToArray();
        Assert.All(positions, p => Assert.True(p >= 0));
        Assert.Equal(positions.Order(), positions);
        Assert.Contains("[READ-ONLY: dependencies]\ninterface ILog { void Write(string message); }\n", u);
        Assert.Contains("Calc", u[..u.IndexOf("[READ-ONLY: construction]", StringComparison.Ordinal)]);
        Assert.Contains("Demo", u[..u.IndexOf("[READ-ONLY: construction]", StringComparison.Ordinal)]);
        Assert.Contains("### REQUEST\nWrite 3 tests that pin the CURRENT behavior of the target. Assert what it does now, even if it looks wrong.", u);
        Assert.EndsWith(Template.AssistantStart, result.BasePrompt);
        Assert.DoesNotContain("### USINGS", u);
    }

    [Fact]
    public void Build_SystemNamesFrameworkAttributeAndLanguage()
    {
        var input = Input(framework: TestFrameworks.Info(TestFramework.MsTest)) with { LangVersion = "C# 7.3" };

        var prompt = NewBuilder().Build(input).BasePrompt;
        var system = prompt[..prompt.IndexOf(Template.UserStart, StringComparison.Ordinal)];

        Assert.Contains("[TestMethod]", system);
        Assert.Contains("C# 7.3", system);
        Assert.Contains("### USINGS", system);
        Assert.Contains("### MEMBERS", system);
        Assert.DoesNotContain("{TestAttribute}", system);
        Assert.DoesNotContain("{LangVersion}", system);
    }

    [Fact]
    public void Build_NoExampleTest_UsesFrameworkExample()
    {
        var u = User(NewBuilder().Build(Input(example: null)).BasePrompt);

        Assert.Contains("[READ-ONLY: example test]\n" + Xunit.ExampleTest, u);
    }

    [Fact]
    public void Build_MissingOptionalParts_Omitted()
    {
        var u = User(NewBuilder().Build(Input(helpers: null, deps: null, dto: null, usings: [])).BasePrompt);

        Assert.DoesNotContain("[READ-ONLY: test helpers]", u);
        Assert.DoesNotContain("[READ-ONLY: dependencies]", u);
        Assert.DoesNotContain("[READ-ONLY: dto]", u);
        Assert.DoesNotContain("[READ-ONLY: test usings]", u);
    }

    private static string Filler(string word, int lines) => string.Join("\n", Enumerable.Range(0, lines).Select(i => $"// {word} line {i}"));

    [Fact]
    public void Build_OverBudget_DropsExampleThenUsingsThenDto()
    {
        // Each optional part is ~250 tokens; budgets step down by one part at a time.
        var input = Input(
            example: "[Fact]\npublic void Ex() { }\n" + Filler("example", 40),
            usings: Enumerable.Range(0, 40).Select(i => $"using Ns{i}.Sub;").ToList(),
            dto: Filler("dto", 40),
            deps: Filler("deps", 40),
            helpers: Filler("helpers", 40));
        var full = NewBuilder(new BudgetOptions { InputTotal = 100_000 }).Build(input);
        Assert.Equal(PromptBudgetError.None, full.Error);

        string Built(int total)
        {
            var r = NewBuilder(new BudgetOptions { InputTotal = total }).Build(input);
            Assert.Equal(PromptBudgetError.None, r.Error);
            Assert.True(r.Tokens <= total);
            return User(r.BasePrompt);
        }

        var one = Built(full.Tokens - 100);
        Assert.DoesNotContain("[READ-ONLY: test usings]", one);
        Assert.Contains("[READ-ONLY: example test]", one);

        var two = Built(full.Tokens - 400);
        Assert.DoesNotContain("[READ-ONLY: example test]", two);
        Assert.Contains("[READ-ONLY: dto]", two);

        var three = Built(full.Tokens - 700);
        Assert.DoesNotContain("[READ-ONLY: dto]", three);
        Assert.Contains("[READ-ONLY: dependencies]", three);
        Assert.Contains("[READ-ONLY: test helpers]", three);

        var five = Built(full.Tokens - 1300);
        Assert.DoesNotContain("[READ-ONLY: dependencies]", five);
        Assert.DoesNotContain("[READ-ONLY: test helpers]", five);
        Assert.Contains("[READ-ONLY: target]", five);
        Assert.Contains("[READ-ONLY: construction]", five);
    }

    [Fact]
    public void Build_TargetAloneTooLarge_BudgetError()
    {
        var huge = "public int Add(int a, int b)\n{\n" + Filler("body", 600) + "\n    return a + b;\n}";

        var r = NewBuilder().Build(Input(target: huge));

        Assert.Equal(PromptBudgetError.TotalTooLong, r.Error);
        Assert.Contains("target", r.Detail);
    }

    [Fact]
    public void Build_HintAppended()
    {
        var u = User(NewBuilder().Build(Input(hint: "  negative numbers  ")).BasePrompt);

        Assert.Contains("even if it looks wrong.\nFocus: negative numbers\n", u + "\n");
    }

    [Fact]
    public void Build_HintTooLong_RequestTooLong()
    {
        var r = NewBuilder().Build(Input(hint: string.Join(" ", Enumerable.Repeat("negative", 300))));

        Assert.Equal(PromptBudgetError.RequestTooLong, r.Error);
    }

    [Fact]
    public void Build_FeedbackAppended()
    {
        var feedback = new Feedback("Add_Two_Sums failed: expected 4, actual 3", 12);

        var u = User(NewBuilder().Build(Input(feedback: feedback)).BasePrompt);

        Assert.EndsWith("### FEEDBACK\nAdd_Two_Sums failed: expected 4, actual 3", u);
        Assert.True(u.IndexOf("### REQUEST", StringComparison.Ordinal) < u.IndexOf("### FEEDBACK", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_FeedbackTooLong_FeedbackTooLong()
    {
        var text = string.Join(" ", Enumerable.Repeat("failed", 400));

        var r = NewBuilder().Build(Input(feedback: new Feedback(text, 400)));

        Assert.Equal(PromptBudgetError.FeedbackTooLong, r.Error);
    }

    [Theory]
    [InlineData(TestFramework.XunitV3)]
    [InlineData(TestFramework.MsTest)]
    [InlineData(TestFramework.NUnit)]
    public void SystemAndFewShot_FitDefaultSystemBudget(TestFramework framework)
    {
        var tokens = Counter.Count(Template.SystemStart + CharTestPromptBuilder.SystemText(Resources, TestFrameworks.Info(framework), "C# 7.3") + Template.SystemEnd);

        Assert.True(tokens <= new BudgetOptions().System, $"cs-test system prompt is {tokens} tokens; budget.system is {new BudgetOptions().System}");
    }

    [Theory]
    [InlineData(TestFramework.XunitV3, "Assert.Equal(\"Hi Kim\", result);")]
    [InlineData(TestFramework.MsTest, "Assert.AreEqual(\"Hi Kim\", result);")]
    [InlineData(TestFramework.NUnit, "Assert.That(result, Is.EqualTo(\"Hi Kim\"));")]
    public void FewShot_UsesFrameworkAssertion(TestFramework framework, string assertion)
    {
        var text = CharTestPromptBuilder.SystemText(Resources, TestFrameworks.Info(framework), "C# 12");

        Assert.Contains(assertion, text);
        Assert.DoesNotContain("{AssertEqual}", text);
        if (framework != TestFramework.XunitV3) Assert.DoesNotContain("Assert.Equal(", text);
    }

    [Fact]
    public void SystemText_RulesForFakesMockingAndAsync()
    {
        var text = CharTestPromptBuilder.SystemText(Resources, Xunit, "C# 12");

        Assert.Contains("private nested classes", text);
        Assert.Contains("Do not use Moq, NSubstitute or FakeItEasy unless they appear in [READ-ONLY: test usings]", text);
        Assert.Contains("=> throw new System.NotImplementedException()", text);
        Assert.Contains("Task<T>", text);
        Assert.Contains("public async Task", text);
        Assert.Matches(@"### USINGS\nusing [\w.]+;\n", text[text.IndexOf("Example output", StringComparison.Ordinal)..]);
    }

    [Fact]
    public void Build_TokensWithinInputTotal()
    {
        const LanguageVersion v = LanguageVersion.CSharp12;
        var src = File.ReadAllText(CorpusLocation.Get("Modern/Shop.Api/Services/OrderService.cs")).Replace("\r\n", "\n");
        var target = Assert.Single(MemberLocator.FindByName(src, v, null, "CalculateRefundAsync"));
        var lines = src.Split('\n');
        var targetSource = string.Join("\n", lines[(target.Span.StartLine - 1)..target.Span.EndLine]);
        var root = CorpusLocation.Get("Modern/Shop.Api");
        var deps = DependencySignatureCollector.Collect(new FileSystem(), root, src, target, v, ct: TestContext.Current.CancellationToken);
        var dto = DtoCollector.Collect(new FileSystem(), root, src, target, v, ct: TestContext.Current.CancellationToken);
        var testSrc = File.ReadAllText(CorpusLocation.Get("Modern/Shop.Api.Tests/OrderServiceTests.cs")).Replace("\r\n", "\n");
        var usings = testSrc.Split('\n').Where(l => l.StartsWith("using ", StringComparison.Ordinal)).ToList();
        const string construction = """
            public OrderService(IOrderRepository repository, ILogger<OrderService> logger)
            private const int MaxReasonLength = 200;
            private const decimal ReturnShippingFee = 3000m;
            private const decimal FreeShippingThreshold = 30000m;
            private const int MaxPageSize = 100;
            private readonly IOrderRepository _repository;
            private readonly ILogger<OrderService> _logger;
            """;
        const string helpers = """
            private static Order NewOrder(OrderStatus status);
            private sealed class FakeOrderRepository : IOrderRepository
            {
                public FakeOrderRepository(Order order);
                public Order Order { get; }
                public List<string> History { get; }
                public decimal RefundRequested { get; private set; }
                public Task<Order> GetByIdAsync(int orderId);
                public Task<IReadOnlyList<OrderItem>> GetItemsAsync(int orderId);
                public Task<IReadOnlyList<Order>> SearchAsync(OrderSearchRequest request);
                public Task UpdateStatusAsync(int orderId, OrderStatus status, string reason);
                public Task AddHistoryAsync(int orderId, string message);
                public Task AddRefundAsync(int orderId, decimal amount);
            }
            """;
        var example = testSrc[testSrc.IndexOf("        [Fact]", StringComparison.Ordinal)..testSrc.IndexOf("        [Fact]\n        public async Task CalculateRefundAsync_PaidOrder", StringComparison.Ordinal)].TrimEnd();
        var input = new CharTestPromptInput(targetSource, target.Display, "OrderService", "Shop.Api.Services", construction, helpers, deps, dto, example,
            usings, Xunit, 3, "shipped orders", null) { LangVersion = "C# 12" };

        var r = NewBuilder().Build(input);

        Assert.Equal(PromptBudgetError.None, r.Error);
        Assert.True(r.Tokens <= 4096, $"prompt is {r.Tokens} tokens");
        Assert.Equal(Counter.Count(r.BasePrompt), r.Tokens);
        var u = User(r.BasePrompt);
        Assert.Contains("GetByIdAsync", u[u.IndexOf("[READ-ONLY: dependencies]", StringComparison.Ordinal)..]);
    }
}
