using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Tests.Asp;

public class VbLexerTests
{
    private static IReadOnlyList<VbStatement> Lex(string code, List<AspProblem>? problems = null) =>
        VbLexer.Statements([(1, 1, code)], problems ?? []);

    [Fact]
    public void String_DoubledQuote()
    {
        var problems = new List<AspProblem>();
        var stmt = Assert.Single(Lex("s = \"say \"\"hi\"\" now\"", problems));
        Assert.Empty(problems);
        Assert.Equal(3, stmt.Tokens.Count);
        Assert.Equal(VbTokenKind.String, stmt.Tokens[2].Kind);
        Assert.Equal("\"say \"\"hi\"\" now\"", stmt.Tokens[2].Text);
        Assert.Equal(5, stmt.Tokens[2].Column);
    }

    [Fact]
    public void Comment_ApostropheAndRem()
    {
        var statements = Lex("x = 1 ' note: \"End If\"\nRem whole line comment\n' another\ny = 2");
        Assert.Equal(2, statements.Count);
        Assert.Equal("x = 1", statements[0].Text);
        Assert.Equal(3, statements[0].Tokens.Count);
        Assert.Equal("y = 2", statements[1].Text);
        Assert.Equal(4, statements[1].StartLine);
    }

    [Fact]
    public void LineContinuation_JoinsStatement()
    {
        var stmt = Assert.Single(Lex("sql = \"SELECT * \" & _\n      \"FROM t\""));
        Assert.Equal(1, stmt.StartLine);
        Assert.Equal(2, stmt.EndLine);
        Assert.Equal("sql = \"SELECT * \" & \"FROM t\"", stmt.Text);
        Assert.Equal(5, stmt.Tokens.Count);
        Assert.Equal(2, stmt.Tokens[4].Line);
        Assert.Equal(7, stmt.Tokens[4].Column);
    }

    [Fact]
    public void Colon_SplitsStatements()
    {
        var statements = Lex("a = 1 : b = \"x:y\" : Call Foo(a)");
        Assert.Equal(3, statements.Count);
        Assert.Equal("a = 1", statements[0].Text);
        Assert.Equal("b = \"x:y\"", statements[1].Text);
        Assert.Equal("Call Foo(a)", statements[2].Text);
        Assert.Equal(VbTokenKind.Keyword, statements[2].Tokens[0].Kind);
        Assert.Equal(VbTokenKind.OpenParen, statements[2].Tokens[2].Kind);
    }

    [Fact]
    public void SingleLineIf_NotBlock()
    {
        var statements = Lex("If a Then b = 1 : c = 2 Else d = 3\ne = 4");
        Assert.Equal(2, statements.Count);
        Assert.Equal("If a Then b = 1 : c = 2 Else d = 3", statements[0].Text);
        Assert.Equal("e = 4", statements[1].Text);
    }

    [Fact]
    public void UnclosedString_ASP003()
    {
        var problems = new List<AspProblem>();
        var statements = VbLexer.Statements([(3, 5, "x = 1\ns = \"abc\ny = 2")], problems);
        var problem = Assert.Single(problems);
        Assert.Equal("ASP003", problem.Code);
        Assert.Equal(4, problem.Line);
        Assert.Equal(5, problem.Column);
        Assert.DoesNotContain("4", problem.Message);
        Assert.Equal(3, statements.Count);
        Assert.Equal(5, statements[2].StartLine);
    }
}
