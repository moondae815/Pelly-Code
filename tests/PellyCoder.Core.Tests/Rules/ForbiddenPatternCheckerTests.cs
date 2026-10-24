using System.Text.RegularExpressions;
using PellyCoder.Core.Rules;

namespace PellyCoder.Core.Tests.Rules;

public sealed class ForbiddenPatternCheckerTests
{
    private static ProjectRule Rule(string? forbid, string text = "rule", int line = 5) => new(line, ["*"], text, forbid);

    /// <summary>An added line whose code is its whole text (nothing masked).</summary>
    private static AddedLine L(string text) => new(text, text);

    private static string[] Texts(IEnumerable<AddedLine> lines) => lines.Select(l => l.Text).ToArray();

    [Fact]
    public void AddedLines_ExcludesLinesAlreadyInOriginal() =>
        Assert.Equal(["    b();"], Texts(ForbiddenPatternChecker.AddedLines("    var x = task.Result;\n    a();\n", "    var x = task.Result;\n    b();\n")));

    [Fact]
    public void AddedLines_CountsDuplicates() =>
        Assert.Equal(["    x();"], Texts(ForbiddenPatternChecker.AddedLines("    x();\n", "    x();\n    x();\n")));

    [Fact]
    public void AddedLines_NormalizesCrLfAndIgnoresTrailingNewline() =>
        Assert.Equal(["b();"], Texts(ForbiddenPatternChecker.AddedLines("a();\r\n", "a();\nb();\r\n")));

    [Fact]
    public void AddedLines_WithoutCode_CodeIsText() =>
        Assert.Equal([L("b();")], ForbiddenPatternChecker.AddedLines("a();\n", "a();\nb();\n"));

    [Fact]
    public void AddedLines_TakesCodeFromTheSameLineOfTheMaskedText()
    {
        const string patched = "a();\nb(); // t.Result\n";
        const string code = "a();\nb();            \n";

        Assert.Equal([new AddedLine("b(); // t.Result", "b();            ")], ForbiddenPatternChecker.AddedLines("a();\n", patched, code));
    }

    [Fact]
    public void AddedLines_MaskedTextWithOtherLineCount_FallsBackToText() =>
        Assert.Equal([L("b();")], ForbiddenPatternChecker.AddedLines("a();\n", "a();\nb();\n", "a();\n"));

    [Fact]
    public void Check_MatchesCodeNotText_ReportsText()
    {
        var violations = ForbiddenPatternChecker.Check([Rule(@"\.Result\b")],
            [new AddedLine("x(); // t.Result", "x();            "), new AddedLine("y = t.Result; // ok", "y = t.Result;      ")]);

        Assert.Equal(["y = t.Result; // ok"], violations.Select(v => v.AddedLine));
    }

    [Fact]
    public void Check_RepeatedIdenticalLine_OneViolationPerRule() =>
        Assert.Single(ForbiddenPatternChecker.Check([Rule("x")], [L("x"), L("x")]));

    [Fact]
    public void Check_AllForbidRules_NotOnlySelected()
    {
        var rules = new[] { Rule(@"\.Result\b", "no result", 5), Rule(@"Thread\.Sleep", "no sleep", 6), Rule(null, "plain", 7) };

        var violations = ForbiddenPatternChecker.Check(rules, [L("x = t.Result;"), L("Thread.Sleep(1);")]);

        Assert.Equal(["no result", "no sleep"], violations.Select(v => v.Rule.Text));
    }

    [Fact]
    public void Check_NoForbid_NoViolation() =>
        Assert.Empty(ForbiddenPatternChecker.Check([Rule(null)], [L("anything")]));

    [Fact]
    public void Check_CatastrophicPattern_Throws() =>
        Assert.Throws<RegexMatchTimeoutException>(() =>
            ForbiddenPatternChecker.Check([Rule("(a+)+$")], [L("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaa!")]));
}
