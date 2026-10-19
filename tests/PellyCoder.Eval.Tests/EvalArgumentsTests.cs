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
        Assert.Equal("p.json", EvalArguments.Parse(["validate", "--config", "p.json"]).Config);

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

    [Fact]
    public void Parse_KeepWork_AcceptsAllFailedNone_RejectsOther()
    {
        Assert.Equal(KeepWork.All, EvalArguments.Parse([]).KeepWork);
        Assert.Equal(KeepWork.Failed, EvalArguments.Parse(["--keep-work", "failed"]).KeepWork);
        Assert.Equal(KeepWork.None, EvalArguments.Parse(["run", "--keep-work", "none"]).KeepWork);
        Assert.Equal(KeepWork.All, EvalArguments.Parse(["--keep-work", "all"]).KeepWork);
        Assert.Equal("--keep-work는 all, failed, none 중 하나: x", EvalArguments.Parse(["--keep-work", "x"]).Error);
        Assert.Equal("--keep-work는 all, failed, none 중 하나: Failed", EvalArguments.Parse(["--keep-work", "Failed"]).Error);
        Assert.Equal("알 수 없는 인자: --keep-work", EvalArguments.Parse(["validate", "--keep-work", "none"]).Error);
    }
}
