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

    [Fact]
    public void Parse_OfflineSelfTest_IsAHiddenRunFlag()
    {
        var parsed = EvalArguments.Parse(["--offline-selftest"]);
        Assert.Null(parsed.Error);
        Assert.True(parsed.OfflineSelfTest);
        Assert.False(EvalArguments.Parse([]).OfflineSelfTest);
        Assert.Equal("알 수 없는 인자: --offline-selftest", EvalArguments.Parse(["validate", "--offline-selftest"]).Error);
        Assert.DoesNotContain("--offline-selftest", EvalArguments.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void Usage_Compare_SaysDifferingModelIdentityIsNotJudged() =>
        Assert.Contains("모델 digest나 Ollama 버전이 다르면 판정 불가(4)", EvalArguments.Usage, StringComparison.Ordinal);

    [Fact]
    public void Compare_TestFirstFlag_Parsed()
    {
        var a = EvalArguments.Parse(["compare", "--test-first", "b.json", "c.json"]);
        Assert.Null(a.Error);
        Assert.True(a.TestFirst);
        Assert.Equal(["b.json", "c.json"], a.Positional);
        Assert.False(EvalArguments.Parse(["compare", "b.json", "c.json"]).TestFirst);
    }

    [Fact]
    public void OfflineSelfTest_SameRowsAsPelly_AllOk_Exit0()
    {
        var output = new StringWriter();
        var code = SelfTestCommand.Run(output);
        var text = output.ToString();
        Assert.True(code == 0, text);
        foreach (var row in PellyCoder.Core.Doctor.OfflineSelfTest.Run())
            Assert.Contains($"| {row.Name} | Ok |", text, StringComparison.Ordinal);
    }

    [Fact]
    public void NoRules_IsARunFlag()
    {
        Assert.True(EvalArguments.Parse(["--no-rules"]).NoRules);
        Assert.True(EvalArguments.Parse(["run", "--suite", "s", "--no-rules", "--repeat", "3"]).NoRules);
        Assert.False(EvalArguments.Parse([]).NoRules);
        Assert.Equal("알 수 없는 인자: --no-rules", EvalArguments.Parse(["validate", "--no-rules"]).Error);
    }

    [Fact]
    public void Apply_command_parses_its_options()
    {
        var apply = EvalArguments.Parse(["apply", "--cases", "a*,d1", "--repeat", "3", "--out", "o", "--config", "p.json"]);
        Assert.Null(apply.Error);
        Assert.Equal(("apply", "a*,d1", 3, "o", "p.json"), (apply.Command, apply.Cases, apply.Repeat, apply.Out, apply.Config));

        var bare = EvalArguments.Parse(["apply"]);
        Assert.Null(bare.Error);
        Assert.Equal(("apply", null, 1), (bare.Command, bare.Cases, bare.Repeat));

        Assert.Equal("알 수 없는 인자: --tasks", EvalArguments.Parse(["apply", "--tasks", "x"]).Error);
        Assert.Equal("알 수 없는 인자: --cases", EvalArguments.Parse(["run", "--cases", "x"]).Error);
        Assert.Equal("--repeat는 1 이상의 정수: 0", EvalArguments.Parse(["apply", "--repeat", "0"]).Error);
        Assert.True(EvalArguments.Parse(["apply", "--help"]).Help);
        Assert.Contains("pelly-eval apply", EvalArguments.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void Args_TestFirst_RunOnly()
    {
        Assert.True(EvalArguments.Parse(["--test-first"]).TestFirst);
        Assert.True(EvalArguments.Parse(["run", "--tasks", "cs-modern", "--test-first", "--repeat", "3"]).TestFirst);
        Assert.False(EvalArguments.Parse([]).TestFirst);
        Assert.Equal("알 수 없는 인자: --test-first", EvalArguments.Parse(["apply", "--test-first"]).Error);
        Assert.Equal("알 수 없는 인자: --test-first", EvalArguments.Parse(["validate", "--test-first"]).Error);
        Assert.Contains("--test-first", EvalArguments.Usage, StringComparison.Ordinal);
    }
}
