using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

public class TaskJudgeTests
{
    private const string Original = "if (order.Status == OrderStatus.Delivered) { throw new X(); }";
    private const string Fixed = "if (order.Status == OrderStatus.Shipped) { throw new Y(); }\n" + Original;

    private static BenchmarkTask Cs(string id = "cs-modern-001", BenchmarkJudge? judge = null) => new()
    {
        Id = id, Kind = "cs", File = "corpus/Modern/x.cs", Method = "M", Request = "r",
        Judge = judge ?? new BenchmarkJudge { Build = true },
    };

    [Fact]
    public void Judge_UnchangedTarget_Fails()
    {
        var outcome = TaskJudge.Judge(Cs(), Original, Original, 0, VerificationResult.Pass);

        Assert.False(outcome.Passed);
        Assert.Contains("대상이 바뀌지 않음", outcome.Reasons);
        Assert.False(outcome.InvariantViolated);
    }

    [Fact]
    public void Judge_ReformatOnly_IsUnchanged()
    {
        const string before = "if (x)\n{\n    return a + b;\n}";
        const string reformatted = "if (x)  {\r\n\treturn a +   b;\n\n}  ";

        var outcome = TaskJudge.Judge(Cs(), before, reformatted, 0, VerificationResult.Pass);

        Assert.False(outcome.Passed);
        Assert.Contains("대상이 바뀌지 않음", outcome.Reasons);
        Assert.DoesNotContain("대상이 바뀌지 않음", TaskJudge.Judge(Cs(), before, before.Replace("a + b", "a - b", StringComparison.Ordinal), 0,
            VerificationResult.Pass).Reasons);
    }

    [Fact]
    public void Judge_MustContainMissing_Fails()
    {
        var task = Cs(judge: new BenchmarkJudge { Build = true, MustContain = ["IsDBNull(1)", "Shipped"] });

        var outcome = TaskJudge.Judge(task, Original, Fixed, 0, VerificationResult.Pass);

        Assert.False(outcome.Passed);
        Assert.Single(outcome.Reasons);
        Assert.Contains("IsDBNull(1)", outcome.Reasons[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Judge_MustAdd_CountsOccurrences()
    {
        var task = Cs(judge: new BenchmarkJudge { Build = true, MustAdd = ["OrderStatus."] });
        var reordered = Original.Replace("{ throw", "{  throw", StringComparison.Ordinal); // changed, same count

        Assert.True(TaskJudge.Judge(task, Original, Fixed, 0, VerificationResult.Pass).Passed);
        var same = TaskJudge.Judge(task, Original, reordered, 0, VerificationResult.Pass);
        Assert.False(same.Passed);
        Assert.Contains(same.Reasons, r => r.Contains("OrderStatus.", StringComparison.Ordinal) && r.Contains("1 → 1", StringComparison.Ordinal));
    }

    [Fact]
    public void Judge_MustNotContainPresent_Fails()
    {
        var task = Cs(judge: new BenchmarkJudge { Build = true, MustNotContain = ["Delivered"] });

        var outcome = TaskJudge.Judge(task, Original, Fixed, 0, VerificationResult.Pass);

        Assert.False(outcome.Passed);
        Assert.Contains(outcome.Reasons, r => r.Contains("Delivered", StringComparison.Ordinal));
        Assert.True(TaskJudge.Judge(task, Original, "if (x) { }", 0, VerificationResult.Pass).Passed);
    }

    [Fact]
    public void Judge_NonZeroExit_Fails()
    {
        var outcome = TaskJudge.Judge(Cs(), Original, Fixed, 1, VerificationResult.Skip("실행 실패"));

        Assert.False(outcome.Passed);
        Assert.Contains(outcome.Reasons, r => r.Contains("종료 코드 1", StringComparison.Ordinal));
        Assert.False(outcome.InvariantViolated);
    }

    [Fact]
    public void Judge_Exit0ButIndependentFailed_IsInvariantViolation()
    {
        var outcome = TaskJudge.Judge(Cs(), Original, Fixed, 0, VerificationResult.Fail("CS0103 undefinedVar"));

        Assert.False(outcome.Passed);
        Assert.True(outcome.InvariantViolated);
        Assert.Equal(VerificationResult.Failed, outcome.BuildCheck);
        Assert.Contains(outcome.Reasons, r => r.Contains("CS0103", StringComparison.Ordinal));
    }

    [Fact]
    public void Judge_LegacySkippedBuild_StillPassesOtherwise()
    {
        var task = Cs("cs-legacy-001", new BenchmarkJudge { Build = true, MustAdd = ["OrderStatus.Shipped"] });

        var outcome = TaskJudge.Judge(task, Original, Fixed, 0, VerificationResult.Skip(VerificationResult.LegacyBuildSkipped));

        Assert.True(outcome.Passed, string.Join(", ", outcome.Reasons));
        Assert.Equal(VerificationResult.Skipped, outcome.BuildCheck);
        Assert.False(outcome.InvariantViolated);
    }

    [Fact]
    public void Judge_NoBuildRequired_ReportsNotRequired()
    {
        var task = Cs(judge: new BenchmarkJudge());

        Assert.Equal(VerificationResult.NotRequired, TaskJudge.Judge(task, Original, Fixed, 0, VerificationResult.Pass).BuildCheck);
    }
}
