using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Tests.Feedbacks;

public sealed class FeedbackBuilderBudgetTests
{
    private const int Budget = 296;
    private const string Target = "Shop/Svc.cs";
    private static readonly QwenTokenCounter Counter = QwenTokenCounter.Shared;

    private static Diagnostic Err(string file, int line, string msg) => new(file, line, 5, "error", "CS0103", msg);

    [Theory]
    [InlineData(5000)]
    [InlineData(20000)]
    public void HugeErrorLine_FeedbackWithinBudget(int lineLength)
    {
        var file = "ok\n" + new string('x', lineLength) + "\nok\n";
        var errors = new[] { Err(Target, 2, "name missing") };

        var diag = new FeedbackBuilder(Counter, Budget).ForDiagnostics("SYNTAX ERRORS", errors, file);
        var build = new FeedbackBuilder(Counter, Budget).ForBuild(errors, Target, file);

        foreach (var fb in new[] { diag, build })
        {
            Assert.True(fb.Tokens <= Budget, $"{fb.Tokens} > {Budget}");
            Assert.Equal(Counter.Count(fb.Text), fb.Tokens);
            Assert.Contains("CS0103", fb.Text);
        }
        Assert.StartsWith("SYNTAX ERRORS", diag.Text);
        Assert.StartsWith("BUILD FAILED", build.Text);
    }

    [Fact]
    public void HugePath_FeedbackWithinBudget()
    {
        var path = string.Join("/", Enumerable.Repeat(new string('d', 200), 30)) + "/Svc.cs";
        var errors = new[] { Err(path, 1, "m") };

        var diag = new FeedbackBuilder(Counter, Budget).ForDiagnostics("SYNTAX ERRORS", errors, "a\n");
        var build = new FeedbackBuilder(Counter, Budget).ForBuild(errors, path, "a\n");

        Assert.True(diag.Tokens <= Budget);
        Assert.True(build.Tokens <= Budget);
        Assert.Contains("CS0103", diag.Text);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(12)]
    public void TinyBudget_StillWithinBudget(int tiny)
    {
        var errors = new[] { Err(Target, 2, new string('m', 500)) };
        var b = new FeedbackBuilder(Counter, tiny);

        Assert.True(b.ForDiagnostics("SYNTAX ERRORS", errors, "a\nb\n").Tokens <= tiny);
        Assert.True(b.ForBuild(errors, Target, "a\nb\n").Tokens <= tiny);
        Assert.True(b.ForPatchFailure(new PatchFailure(PatchFailureKinds.Ambiguous, "d", null)).Tokens <= tiny);
        Assert.True(b.ForShapeRejection(new string('r', 400)).Tokens <= tiny);
    }

    [Fact]
    public void Truncation_NeverSplitsSurrogatePair()
    {
        var line = string.Concat(Enumerable.Repeat("😀", 3000));
        var errors = new[] { Err(Target, 1, "m") };
        var b = new FeedbackBuilder(Counter, Budget);

        foreach (var text in new[]
        {
            b.ForDiagnostics("SYNTAX ERRORS", errors, line).Text,
            b.ForBuild(errors, Target, line).Text,
            b.ForShapeRejection(line).Text,
        })
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (char.IsHighSurrogate(text[i])) Assert.True(i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]));
                if (char.IsLowSurrogate(text[i])) Assert.True(i > 0 && char.IsHighSurrogate(text[i - 1]));
            }
        }
    }
}
