using PellyCoder.Eval;

namespace PellyCoder.Eval.Tests;

public class EvalArgumentsTests
{
    [Fact]
    public void Parse_SubcommandFrame()
    {
        var validate = EvalArguments.Parse(["validate", "--suite", "x"]);
        Assert.Null(validate.Error);
        Assert.Equal(("validate", "x"), (validate.Command, validate.Suite));

        var run = EvalArguments.Parse(["--suite", "x"]);
        Assert.Null(run.Error);
        Assert.Equal(("run", "x"), (run.Command, run.Suite));

        var explicitRun = EvalArguments.Parse(["run", "--tasks", "sql", "--repeat", "2"]);
        Assert.Null(explicitRun.Error);
        Assert.Equal(("run", "sql", 2), (explicitRun.Command, explicitRun.Tasks, explicitRun.Repeat));

        var compare = EvalArguments.Parse(["compare", "a", "b"]);
        Assert.Null(compare.Error);
        Assert.Equal("compare", compare.Command);
        Assert.Equal(["a", "b"], compare.Positional);

        Assert.NotNull(EvalArguments.Parse(["compare", "a"]).Error);
        Assert.NotNull(EvalArguments.Parse(["compare", "a", "b", "c"]).Error);
        Assert.Equal("알 수 없는 인자: --repeat", EvalArguments.Parse(["validate", "--repeat", "3"]).Error);
        Assert.Equal("알 수 없는 인자: --suite", EvalArguments.Parse(["compare", "a", "b", "--suite", "x"]).Error);
        Assert.Equal("알 수 없는 인자: stray", EvalArguments.Parse(["--tasks", "sql", "stray"]).Error);
        Assert.Equal("--suite에 값이 필요합니다", EvalArguments.Parse(["--suite"]).Error);
        Assert.True(EvalArguments.Parse(["validate", "--help"]).Help);
    }

    [Fact]
    public void Usage_MentionsSuite() =>
        Assert.Contains("--suite <폴더>    스위트 폴더(suite.json이 있는 곳, 기본: 저장소의 eval/benchmark)", EvalArguments.Usage, StringComparison.Ordinal);
}
