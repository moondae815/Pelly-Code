using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Tests.Asp;

/// <summary>
/// The VBScript text cscript compiles: each code fragment on its own script line mapped back to its page line, HTML
/// dropped, execution stopped by <c>WScript.Quit 0</c> on its own line.
/// </summary>
public class VbsCompileScriptTests
{
    private static VbsScript Script(string textLf) => VbsCompileScript.Build(AspPage.Parse(textLf), textLf);

    private static string[] Lines(VbsScript script) => script.Text.Split('\n');

    [Fact]
    public void Build_EachFragmentOwnLine_MappedToPageLine()
    {
        const string text =
            "<%@ Language=\"VBScript\" CodePage=949 %>\n" + // 1
            "<!--#include file=\"util.inc\"-->\n" +        // 2
            "<html>\n" +                                     // 3
            "<%\n" +                                         // 4
            "Dim x\n" +                                      // 5
            "x = 1\n" +                                      // 6
            "%>\n" +                                         // 7
            "<b><%= x %></b>\n" +                            // 8
            "<script runat=\"server\" language=\"VBScript\">\n" + // 9
            "Sub S()\n" +                                    // 10
            "End Sub\n" +                                    // 11
            "</script>\n" +                                  // 12
            "</html>\n";                                     // 13

        var script = Script(text);

        Assert.Equal(["WScript.Quit 0", "Dim x", "x = 1", "Response.Write (x)", "Sub S()", "End Sub"], Lines(script));
        Assert.Equal([1, 5, 6, 8, 10, 11], script.PageLines);
        Assert.Equal(AspPage.Parse(text).LineCount, script.LastPageLine);
    }

    [Fact]
    public void Build_HtmlOnlyLines_NoScriptLine()
    {
        const string text = "<html>\n<head><title>목록</title></head>\n<% x = 1 %>\n<!--METADATA TYPE=\"typelib\" FILE=\"x.dll\"-->\n</html>";

        var script = Script(text);

        Assert.Equal(["WScript.Quit 0", "x = 1"], Lines(script));
        Assert.Equal([1, 3], script.PageLines);
        Assert.Equal(5, script.LastPageLine);
    }

    [Fact]
    public void Build_OutputExpression_ResponseWrite()
    {
        const string text = "<% Option Explicit %>\n<td><%= FormatNumber(total, 0) %></td>";

        var script = Script(text);

        Assert.Equal(["Option Explicit", "WScript.Quit 0", "Response.Write (FormatNumber(total, 0))"], Lines(script));
        Assert.Equal([1, 1, 2], script.PageLines);
    }

    [Fact]
    public void Build_OptionExplicit_QuitOnOwnLineAfter()
    {
        const string text = "<%@ Language=\"VBScript\" %>\n<%\nOption Explicit\nDim x\n%>";

        var script = Script(text);

        Assert.Equal(["Option Explicit", "WScript.Quit 0", "Dim x"], Lines(script));
        Assert.Equal([3, 3, 4], script.PageLines);
    }

    [Fact]
    public void Build_NoOptionExplicit_QuitFirst()
    {
        var script = Script("<% Class C %>\n<% End Class %>");

        Assert.Equal(["WScript.Quit 0", "Class C", "End Class"], Lines(script));
        Assert.Equal([1, 1, 2], script.PageLines);
    }

    [Fact]
    public void Build_RemOptionExplicit_DoesNotMoveQuit()
    {
        const string text =
            "<%\n" +                                  // 1
            "Rem Option Explicit goes below\n" +      // 2
            "Option Explicit\n" +                     // 3
            "s = \"Option Explicit\"\n" +             // 4
            "%>";                                     // 5

        var lines = Lines(Script(text));

        Assert.Equal(["Rem Option Explicit goes below", "Option Explicit", "WScript.Quit 0", "s = \"Option Explicit\""], lines);

        // Without a real Option Explicit statement the Quit is the first line, whatever comments or strings say.
        lines = Lines(Script("<%\nRem Option Explicit\ns = \"Option Explicit\"\n%>"));
        Assert.Equal("WScript.Quit 0", lines[0]);
        Assert.Equal(1, lines.Count(l => l.Contains("WScript.Quit", StringComparison.Ordinal)));
    }

    [Fact]
    public void Build_OptionExplicitSharingLine_QuitAfterItsFragment()
    {
        var script = Script("<% Option Explicit %><% Dim x %>\n<% x = 1 %>");

        Assert.Equal(["Option Explicit", "WScript.Quit 0", "Dim x", "x = 1"], Lines(script));
        Assert.Equal([1, 1, 1, 2], script.PageLines);
    }

    [Fact]
    public void Build_MultipleChunksOneLine_SeparateLines()
    {
        const string text = "<%@ Language=\"VBScript\" %>\n<% If a Then %><b>예</b><% Else %><%= b %><% End If %>";

        var script = Script(text);

        Assert.Equal(["WScript.Quit 0", "If a Then", "Else", "Response.Write (b)", "End If"], Lines(script));
        Assert.Equal([1, 2, 2, 2, 2], script.PageLines);
    }

    [Fact]
    public void Build_CommentInChunk_Dropped()
    {
        const string text = "<%@ Language=\"VBScript\" %>\n<% If a Then ' it's \"a\" %>x<% End If %>\n<% s = \"don't\" ' note %>";

        var lines = Lines(Script(text));

        Assert.Equal(["WScript.Quit 0", "If a Then", "End If", "s = \"don't\""], lines);
    }

    [Fact]
    public void PageLine_MapsAndClampsPastEnd()
    {
        var script = Script("<%@ Language=\"VBScript\" %>\n<%\nIf a Then\n%>\n<p>x</p>\n");

        Assert.Equal(3, script.PageLine(2));
        Assert.Equal(1, script.PageLine(1));
        Assert.Equal(script.LastPageLine, script.PageLine(3));
        Assert.Equal(script.LastPageLine, script.PageLine(99));
    }
}
