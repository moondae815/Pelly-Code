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

    [Fact]
    public void Verify_WithBaseline_ReportsOnlyNewErrors_MultisetIgnoringLines()
    {
        var baseline = SyntaxBaseline.Of("class A\n{\n    int x = 1\n}\n", LanguageVersion.CSharp12, "A.cs");
        Assert.Single(baseline.Errors);

        // Same error moved to another line: not new.
        var moved = SyntaxVerifier.Verify("class A\n{\n\n    int x = 1\n}\n", LanguageVersion.CSharp12, "A.cs", baseline);
        Assert.True(moved.Passed);

        // A second error with the same code and message: exactly one is new.
        var twice = SyntaxVerifier.Verify("class A\n{\n    int x = 1\n    int y = 2\n}\n", LanguageVersion.CSharp12, "A.cs", baseline);
        Assert.False(twice.Passed);
        var d = Assert.Single(twice.NewErrors);
        Assert.Equal("CS1002", d.Code);
        Assert.Equal(4, d.Line); // the error on the original's error line is the old one

        // The new same-key error comes first in the file: the old one (on its unchanged line) still matches first.
        var before = SyntaxVerifier.Verify("class A\n{\n    int y = 2\n    int x = 1\n}\n", LanguageVersion.CSharp12, "A.cs", baseline);
        Assert.Equal(3, Assert.Single(before.NewErrors).Line);
    }
}
