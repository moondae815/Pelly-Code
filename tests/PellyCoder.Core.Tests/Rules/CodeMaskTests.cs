using Microsoft.CodeAnalysis.CSharp;
using PellyCoder.Core.Rules;

namespace PellyCoder.Core.Tests.Rules;

public class CodeMaskTests
{
    private const LanguageVersion V = LanguageVersion.CSharp12;

    /// <summary>The expected mask: every character of <paramref name="part"/> inside <paramref name="text"/> becomes a space.</summary>
    private static string Blank(string text, params string[] parts)
    {
        foreach (var part in parts)
            text = text.Replace(part, new string(' ', part.Length));
        return text;
    }

    [Fact]
    public void CSharp_MasksLineAndBlockComments()
    {
        const string text = "x = t.Result; // t.Result\n/* t.Result */ y();\n";
        Assert.Equal(Blank(text, "// t.Result", "/* t.Result */"), CodeMask.CSharp(text, V));
    }

    [Fact]
    public void CSharp_MasksStringAndCharLiterals()
    {
        const string text = "var s = \"t.Result\"; var c = 'R'; var v = @\"t.Result\";\n";
        Assert.Equal(Blank(text, "@\"t.Result\"", "\"t.Result\"", "'R'"), CodeMask.CSharp(text, V));
    }

    [Fact]
    public void CSharp_MasksRawStringLiterals()
    {
        const string text = "var s = \"\"\"t.Result\"\"\";\n";
        Assert.Equal(Blank(text, "\"\"\"t.Result\"\"\""), CodeMask.CSharp(text, LanguageVersion.CSharp11));
    }

    [Fact]
    public void CSharp_InterpolatedString_KeepsHolesMasksText()
    {
        var masked = CodeMask.CSharp("var s = $\"wait .Result {t.Result} done\";\n", V);
        Assert.Contains("{t.Result}", masked);
        Assert.DoesNotContain("wait", masked);
        Assert.DoesNotContain("done", masked);
    }

    [Fact]
    public void CSharp_MasksDocComments()
    {
        const string text = "/// <summary>t.Result</summary>\nvoid M() { }\n";
        Assert.DoesNotContain("Result", CodeMask.CSharp(text, V));
        Assert.Contains("void M() { }", CodeMask.CSharp(text, V));
    }

    [Fact]
    public void CSharp_KeepsLineBreaksAndLength()
    {
        const string text = "a();\r\n/* one\r\ntwo */ b();\r\nvar s = \"\"\"\r\n  t.Result\r\n  \"\"\";\r\n";
        var masked = CodeMask.CSharp(text, LanguageVersion.CSharp11);
        Assert.Equal(text.Length, masked.Length);
        Assert.Equal(text.Split('\n').Length, masked.Split('\n').Length);
        Assert.Contains(" b();", masked);
        Assert.DoesNotContain("Result", masked);
    }

    [Fact]
    public void CSharp_CodeIsUnchanged()
    {
        const string text = "var x = task.Result;\n";
        Assert.Equal(text, CodeMask.CSharp(text, V));
    }

    [Fact]
    public void Sql_MasksCommentsAndStrings()
    {
        const string text = "SELECT * FROM T WITH (NOLOCK) -- NOLOCK\nSELECT 'NOLOCK', N'NOLOCK' /* NOLOCK */\n";
        Assert.Equal(Blank(text, "-- NOLOCK", "N'NOLOCK'", "'NOLOCK'", "/* NOLOCK */"), CodeMask.Sql(text, 160, quotedIdentifier: true));
    }

    [Fact]
    public void Sql_QuotedIdentifierOff_DoubleQuotesAreStrings()
    {
        const string text = "SELECT \"NOLOCK\"\n";
        Assert.DoesNotContain("NOLOCK", CodeMask.Sql(text, 160, quotedIdentifier: false));
        Assert.Contains("NOLOCK", CodeMask.Sql(text, 160, quotedIdentifier: true));
    }

    [Fact]
    public void Sql_KeepsLineBreaksAcrossBatches()
    {
        const string text = "CREATE PROC p AS\n/* a\nb */ SELECT 1\nGO\nSELECT 'x\ny'\n";
        var masked = CodeMask.Sql(text, 160, quotedIdentifier: true)!;
        Assert.Equal(text.Length, masked.Length);
        Assert.Equal(text.Split('\n').Length, masked.Split('\n').Length);
        Assert.Contains("GO", masked);
    }

    [Fact]
    public void Sql_LexError_ReturnsNull() =>
        Assert.Null(CodeMask.Sql("SELECT 'unclosed\n", 160, quotedIdentifier: true));
}
