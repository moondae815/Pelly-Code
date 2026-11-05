using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class CssChecksTests
{
    private const string Plain = ".a { color: red; }\n.b { margin: 0; }\n";

    [Fact]
    public void NewUnclosedBlock_Reported()
    {
        var problems = CssChecks.NewProblems(CssSheet.Parse(Plain), CssSheet.Parse(".a { color: red;\n.b { margin: 0; }\n"));
        Assert.NotEmpty(problems);
    }

    [Fact]
    public void ExistingProblem_NotCounted()
    {
        const string original = ".a { color: red;\n.b { margin: 0; }\n";
        Assert.NotEmpty(CssSheet.Parse(original).Problems);
        Assert.Empty(CssChecks.NewProblems(CssSheet.Parse(original), CssSheet.Parse("/* c */\n" + original)));
    }

    [Fact]
    public void Shape_SelectorChanged()
    {
        var state = WebTestState.Css(Plain, ".a");
        Assert.Equal("선택자 .a를 바꿀 수 없습니다", CssChecks.ShapeViolation(state, CssSheet.Parse(".c { color: red; }\n.b { margin: 0; }\n")));
        Assert.Null(CssChecks.ShapeViolation(state, CssSheet.Parse(".a { color: blue; }\n.b { margin: 0; }\n")));
        Assert.Equal("선택자 .a를 바꿀 수 없습니다", CssChecks.ShapeViolation(state, CssSheet.Parse(".a { color: red; }\n.a { margin: 0; }\n")));
    }

    [Fact]
    public void Shape_LinesTarget_Null() =>
        Assert.Null(CssChecks.ShapeViolation(WebTestState.Css(Plain), CssSheet.Parse("")));
}
