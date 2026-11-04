using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Tests.Asp;

/// <summary>The VBScript text cscript compiles: one line per page line, HTML blanked, execution stopped by <c>WScript.Quit 0</c>.</summary>
public class VbsCompileScriptTests
{
    private static string[] Build(string textLf) => VbsCompileScript.Build(AspPage.Parse(textLf), textLf).Split('\n');

    [Fact]
    public void Build_SameLineCount()
    {
        const string text =
            "<%@ Language=\"VBScript\" CodePage=949 %>\n" +
            "<!--#include file=\"util.inc\"-->\n" +
            "<html>\n" +
            "<%\n" +
            "Dim x\n" +
            "x = 1\n" +
            "%>\n" +
            "<b><%= x %></b>\n" +
            "<script runat=\"server\" language=\"VBScript\">\n" +
            "Sub S()\n" +
            "End Sub\n" +
            "</script>\n" +
            "</html>\n";
        var page = AspPage.Parse(text);

        var lines = Build(text);

        Assert.Equal(page.LineCount, lines.Length);
        Assert.Equal("Dim x", lines[4]);
        Assert.Equal("x = 1", lines[5]);
        Assert.Equal("Sub S()", lines[9]);
        Assert.Equal("End Sub", lines[10]);
    }

    [Fact]
    public void Build_HtmlBlanked()
    {
        const string text = "<html>\n<head><title>목록</title></head>\n<% x = 1 %>\n<!--METADATA TYPE=\"typelib\" FILE=\"x.dll\"-->\n</html>";

        var lines = Build(text);

        Assert.Equal(5, lines.Length);
        Assert.Equal("", lines[1]);
        Assert.Equal("x = 1", lines[2]);
        Assert.Equal("", lines[3]);
        Assert.Equal("", lines[4]);
    }

    [Fact]
    public void Build_OutputExpression_ResponseWrite()
    {
        const string text = "<% Option Explicit %>\n<td><%= FormatNumber(total, 0) %></td>";

        var lines = Build(text);

        Assert.Equal("Response.Write (FormatNumber(total, 0))", lines[1]);
    }

    [Fact]
    public void Build_OptionExplicit_QuitAfter()
    {
        const string text = "<%@ Language=\"VBScript\" %>\n<%\nOption Explicit\nDim x\n%>";

        var lines = Build(text);

        Assert.Equal("Option Explicit : WScript.Quit 0", lines[2]);
        Assert.Equal("", lines[0]);
        Assert.Equal(1, lines.Count(l => l.Contains("WScript.Quit", StringComparison.Ordinal)));
    }

    [Fact]
    public void Build_DirectiveFirstLine_QuitOnBlank()
    {
        var lines = Build("<%@ Language=\"VBScript\" %>\n<% x = 1 %>");
        Assert.Equal("WScript.Quit 0", lines[0]);
        Assert.Equal("x = 1", lines[1]);

        lines = Build("<% x = 1 %>\n<% y = 2 %>");
        Assert.Equal("WScript.Quit 0 : x = 1", lines[0]);
    }

    [Fact]
    public void Build_MultipleChunksOneLine_Joined()
    {
        const string text = "<%@ Language=\"VBScript\" %>\n<% If a Then %><b>예</b><% Else %><%= b %><% End If %>";

        var lines = Build(text);

        Assert.Equal("If a Then : Else : Response.Write (b) : End If", lines[1]);
    }

    [Fact]
    public void Build_CommentInJoinedChunk_Dropped()
    {
        const string text = "<%@ Language=\"VBScript\" %>\n<% If a Then ' it's \"a\" %>x<% End If %>\n<% s = \"don't\" ' note %>";

        var lines = Build(text);

        Assert.Equal("If a Then : End If", lines[1]);
        Assert.Equal("s = \"don't\"", lines[2]);
    }
}
