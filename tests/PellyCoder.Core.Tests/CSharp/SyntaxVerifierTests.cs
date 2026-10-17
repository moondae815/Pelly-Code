using System.Globalization;
using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.CSharp;

namespace PellyCoder.Core.Tests.CSharp;

public sealed class SyntaxVerifierTests
{
    [Fact]
    public void Verify_ValidCode_Passes()
    {
        var r = SyntaxVerifier.Verify("class A { void M() { } }\n", LanguageVersion.CSharp12, "A.cs");
        Assert.True(r.Passed);
        Assert.Empty(r.NewErrors);
        Assert.Empty(r.NewWarnings);
        Assert.Empty(r.Notices);
    }

    [Fact]
    public void Verify_MissingSemicolon_ReportsCS1002WithLine()
    {
        var r = SyntaxVerifier.Verify("class A\n{\n    int x = 1\n}\n", LanguageVersion.CSharp12, "A.cs");
        Assert.False(r.Passed);
        var d = Assert.Single(r.NewErrors);
        Assert.Equal("CS1002", d.Code);
        Assert.Equal("error", d.Severity);
        Assert.Equal("A.cs", d.File);
        Assert.Equal(3, d.Line);
        Assert.Equal(14, d.Column);
    }

    [Fact]
    public void Verify_UsesRequestedLanguageVersion()
    {
        const string code = "record struct R(int X);\n";
        Assert.False(SyntaxVerifier.Verify(code, LanguageVersion.CSharp7_3, "R.cs").Passed);
        Assert.True(SyntaxVerifier.Verify(code, LanguageVersion.CSharp12, "R.cs").Passed);
    }

    [Fact]
    public void Verify_MessagesAreEnglish()
    {
        var old = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("ko-KR");
            var r = SyntaxVerifier.Verify("class A { int x = 1 }\n", LanguageVersion.CSharp12, "A.cs");
            Assert.Contains("; expected", r.NewErrors[0].Message);
        }
        finally { CultureInfo.CurrentUICulture = old; }
    }
}
