using System.Text.RegularExpressions;
using PellyCoder.Core.Rules;

namespace PellyCoder.Core.Tests.Rules;

public sealed class ForbiddenPatternCheckerTests
{
    private static ProjectRule Rule(string? forbid, string text = "rule", int line = 5) => new(line, ["*"], text, forbid);

    [Fact]
    public void AddedLines_ExcludesLinesAlreadyInOriginal() =>
        Assert.Equal(["    b();"], ForbiddenPatternChecker.AddedLines("    var x = task.Result;\n    a();\n", "    var x = task.Result;\n    b();\n"));

    [Fact]
    public void AddedLines_CountsDuplicates() =>
        Assert.Equal(["    x();"], ForbiddenPatternChecker.AddedLines("    x();\n", "    x();\n    x();\n"));

    [Fact]
    public void AddedLines_NormalizesCrLfAndIgnoresTrailingNewline() =>
        Assert.Equal(["b();"], ForbiddenPatternChecker.AddedLines("a();\r\n", "a();\nb();\r\n"));

    [Fact]
    public void Check_RepeatedIdenticalLine_OneViolationPerRule() =>
        Assert.Single(ForbiddenPatternChecker.Check([Rule("x")], ["x", "x"]));

    [Fact]
    public void Check_AllForbidRules_NotOnlySelected()
    {
        var rules = new[] { Rule(@"\.Result\b", "no result", 5), Rule(@"Thread\.Sleep", "no sleep", 6), Rule(null, "plain", 7) };

        var violations = ForbiddenPatternChecker.Check(rules, ["x = t.Result;", "Thread.Sleep(1);"]);

        Assert.Equal(["no result", "no sleep"], violations.Select(v => v.Rule.Text));
    }

    [Fact]
    public void Check_NoForbid_NoViolation() =>
        Assert.Empty(ForbiddenPatternChecker.Check([Rule(null)], ["anything"]));

    [Fact]
    public void Check_CatastrophicPattern_Throws() =>
        Assert.Throws<RegexMatchTimeoutException>(() =>
            ForbiddenPatternChecker.Check([Rule("(a+)+$")], ["aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!"]));
}
