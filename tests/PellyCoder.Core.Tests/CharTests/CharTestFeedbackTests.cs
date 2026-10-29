using PellyCoder.Core.CharTests;
using PellyCoder.Core.Feedbacks;
using PellyCoder.Core.Llm;
using PellyCoder.Core.Reporting;
using PellyCoder.Core.Testing;

namespace PellyCoder.Core.Tests.CharTests;

public sealed class CharTestFeedbackTests
{
    private static CharTestFeedback Make() =>
        new(new FeedbackBuilder(QwenTokenCounter.Shared, 296), QwenTokenCounter.Shared, 296);

    [Fact]
    public void Feedback_FailsOnCurrent_ContainsActualAndInstruction()
    {
        var f = Make().ForFailsOnCurrent([new TestCaseResult("Ns.C.A", false, "Assert.Equal() Failure\nExpected: 5\nActual:   4\nextra")]);
        Assert.Contains("Ns.C.A", f.Text);
        Assert.Contains("Actual:   4", f.Text);
        Assert.DoesNotContain("extra", f.Text);
        Assert.EndsWith("The current behavior is the truth. Change the assertion to the actual value.", f.Text);
    }

    [Fact]
    public void Feedback_Rejections_RenderReasons()
    {
        var f = Make().ForRejections([new CharTestRejection(FailureKinds.CharTestNoAssert, "T1", "does not call the target")]);
        Assert.Contains("T1", f.Text);
        Assert.Contains("does not call the target", f.Text);
    }

    [Fact]
    public void Feedback_Survivors_Instruction()
    {
        var f = Make().ForSurvivors(["Ns.C.A"]);
        Assert.Contains("Ns.C.A", f.Text);
        Assert.EndsWith("These tests pass even when the target throws. Assert the target's return value or effect.", f.Text);
    }

    [Fact]
    public void Feedback_AllWithinBudget()
    {
        var long1 = string.Join("\n", Enumerable.Range(0, 40).Select(i => "line " + i + " " + new string('x', 200)));
        var fails = Enumerable.Range(0, 50).Select(i => new TestCaseResult("Ns.C.Test" + i, false, long1)).ToList();
        var names = fails.Select(x => x.FullName + new string('y', 100)).ToList();
        var fb = Make();
        Assert.True(fb.ForFailsOnCurrent(fails).Tokens <= 296);
        Assert.True(fb.ForRegression(fails).Tokens <= 296);
        Assert.True(fb.ForFlaky(names).Tokens <= 296);
        Assert.True(fb.ForSurvivors(names).Tokens <= 296);
        Assert.True(fb.ForParse(long1).Tokens <= 296);
        Assert.True(fb.ForRejections(names.Select(n => new CharTestRejection("k", n, long1)).ToList()).Tokens <= 296);
    }

    [Fact]
    public void Feedback_Regression_ExitOnly_SaysSo() =>
        Assert.Contains("exit code only", Make().ForRegression([]).Text);
}
