using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Tests.Asp;

public class AspBuiltinsTests
{
    private static readonly AspBuiltins B = AspBuiltins.Default;

    [Theory]
    [InlineData("dim")]
    [InlineData("END")]
    [InlineData("Wend")]
    [InlineData("Me")]
    public void Keywords_CaseInsensitive(string word) => Assert.True(B.IsKeyword(word));

    [Fact]
    public void Keywords_CoverLexerKeywords()
    {
        foreach (var k in new[] { "And", "ByRef", "ElseIf", "Eqv", "GoTo", "Preserve", "Xor", "Nothing", "Null", "Empty" })
            Assert.True(B.IsKeyword(k), k);
        Assert.False(B.IsKeyword("Left"));
    }

    [Theory]
    [InlineData("Left")]
    [InlineData("mid")]
    [InlineData("FormatNumber")]
    [InlineData("ISNULL")]
    [InlineData("vbCrLf")]
    [InlineData("Err")]
    public void Functions_Known(string name) => Assert.True(B.IsKnown(name, false));

    [Fact]
    public void AdoConstants_OnlyWhenEnabled()
    {
        Assert.False(B.IsKnown("adOpenStatic", false));
        Assert.True(B.IsKnown("adOpenStatic", true));
        Assert.False(B.IsKnown("NoSuchName", true));
    }

    [Theory]
    [InlineData("Response")]
    [InlineData("request")]
    [InlineData("Server")]
    [InlineData("Session")]
    [InlineData("Application")]
    [InlineData("ObjectContext")]
    [InlineData("ASPError")]
    public void AspObjects_Known(string name) => Assert.True(B.IsKnown(name, false));
}
