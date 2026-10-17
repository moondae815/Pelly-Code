using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Tests.Feedbacks;

public sealed class FeedbackBuilderBuildTests
{
    private const string Target = "Shop.Api/Services/OrderService.cs";
    private static readonly QwenTokenCounter Counter = QwenTokenCounter.Shared;

    private static string FileOf(int lines) =>
        string.Join("\n", Enumerable.Range(1, lines).Select(i => $"line{i}"));

    private static Diagnostic Err(string file, int line, string code, string message) => new(file, line, 5, "error", code, message);

    [Fact]
    public void ForBuild_TargetFileFirst_WithContext_OthersOneLine()
    {
        var errors = new[]
        {
            Err("Shop.Api/Controllers/OrdersController.cs", 12, "CS7036", "There is no argument given"),
            Err(Target, 41, "CS0103", "The name 'shippedAt' does not exist in the current context"),
            Err("Shop.Api/Models/Order.cs", 3, "CS0246", "The type or namespace name 'X' could not be found"),
        };

        var fb = new FeedbackBuilder(Counter, 1000).ForBuild(errors, Target, FileOf(50));

        Assert.Equal(string.Join("\n",
            "BUILD FAILED (3 new errors)",
            "Shop.Api/Services/OrderService.cs(41): CS0103 The name 'shippedAt' does not exist in the current context",
            "  38 | line38",
            "  39 | line39",
            "  40 | line40",
            "> 41 | line41",
            "  42 | line42",
            "  43 | line43",
            "  44 | line44",
            "",
            "Shop.Api/Controllers/OrdersController.cs(12): CS7036 There is no argument given",
            "Shop.Api/Models/Order.cs(3): CS0246 The type or namespace name 'X' could not be found"), fb.Text);
        Assert.Equal(Counter.Count(fb.Text), fb.Tokens);
    }

    [Fact]
    public void ForBuild_Singular_And_Plural_Header()
    {
        var one = new FeedbackBuilder(Counter, 1000).ForBuild([Err(Target, 2, "CS0103", "m")], Target, FileOf(5));
        var two = new FeedbackBuilder(Counter, 1000).ForBuild(
            [Err(Target, 2, "CS0103", "m"), Err(Target, 4, "CS0029", "n")], Target, FileOf(5));

        Assert.StartsWith("BUILD FAILED (1 new error)\n", one.Text);
        Assert.StartsWith("BUILD FAILED (2 new errors)\n", two.Text);
    }

    [Fact]
    public void ForBuild_FilelessError_IsOneLineWithoutLocation()
    {
        var fb = new FeedbackBuilder(Counter, 1000).ForBuild(
            [new Diagnostic("", 0, 0, "error", "BUILD", "build failed with exit code 1")], Target, FileOf(5));

        Assert.Equal("BUILD FAILED (1 new error)\nBUILD build failed with exit code 1", fb.Text);
    }

    [Fact]
    public void ForBuild_OverBudget_KeepsFirstTargetError()
    {
        var longMessage = string.Join(' ', Enumerable.Repeat("word", 400));
        var errors = new[]
        {
            Err("Other.cs", 1, "CS0001", longMessage),
            Err(Target, 20, "CS0103", longMessage),
            Err(Target, 30, "CS0029", longMessage),
        };

        var fb = new FeedbackBuilder(Counter, 120).ForBuild(errors, Target, FileOf(50));

        Assert.True(fb.Tokens <= 120, $"{fb.Tokens} tokens");
        Assert.StartsWith("BUILD FAILED (3 new errors)\nShop.Api/Services/OrderService.cs(20): CS0103 word", fb.Text);
        Assert.Contains("word…", fb.Text);
        Assert.DoesNotContain("Other.cs", fb.Text);
        Assert.DoesNotContain("(30)", fb.Text);
    }

    [Fact]
    public void ForBuild_OverBudget_DropsLaterEntriesBeforeShrinkingContext()
    {
        var errors = new[] { Err(Target, 20, "CS0103", "m"), Err("Other.cs", 1, "CS0001", "n") };
        var full = new FeedbackBuilder(Counter, 10_000).ForBuild(errors, Target, FileOf(50));
        var withoutSecond = new FeedbackBuilder(Counter, 10_000).ForBuild([errors[0]], Target, FileOf(50)).Text
            .Replace("(1 new error)", "(2 new errors)", StringComparison.Ordinal);

        var fb = new FeedbackBuilder(Counter, Counter.Count(full.Text) - 1).ForBuild(errors, Target, FileOf(50));

        Assert.Equal(withoutSecond, fb.Text);
    }
}
