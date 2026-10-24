using System.Text.RegularExpressions;
using PellyCoder.Core.Models;
using PellyCoder.Core.Rules;

namespace PellyCoder.Core.Tests.Rules;

public sealed class ForbiddenPatternCheckerTests
{
    private static ProjectRule Rule(string? forbid, string text = "rule", int line = 5) => new(line, ["*"], text, forbid);

    [Fact]
    public void AddedLines_ExcludesLinesAlreadyInSearch()
    {
        var blocks = new[] { new PatchBlock("    var x = task.Result;\n    a();", "    var x = task.Result;\n    b();") };

        Assert.Equal(["    b();"], ForbiddenPatternChecker.AddedLines(blocks));
    }

    [Fact]
    public void AddedLines_CountsDuplicates()
    {
        var blocks = new[] { new PatchBlock("    x();", "    x();\n    x();") };

        Assert.Equal(["    x();"], ForbiddenPatternChecker.AddedLines(blocks));
    }

    [Fact]
    public void AddedLines_NormalizesCrLfAndIgnoresTrailingNewline()
    {
        var blocks = new[] { new PatchBlock("a();\r\n", "a();\r\nb();\r\n") };

        Assert.Equal(["b();"], ForbiddenPatternChecker.AddedLines(blocks));
    }

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
