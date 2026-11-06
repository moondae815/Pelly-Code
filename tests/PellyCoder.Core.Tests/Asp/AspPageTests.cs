using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Tests.Asp;

public class AspPageTests
{
    [Fact]
    public void Segments_CodeOutputDirectiveHtml()
    {
        var page = AspPage.Parse("<%@ Language=\"VBScript\" %>\n<html><% x = 1 %>\n<b><%= x %></b>");
        var kinds = page.Segments.Select(s => s.Kind).ToArray();
        Assert.Equal(
            [AspSegmentKind.Directive, AspSegmentKind.Html, AspSegmentKind.Code, AspSegmentKind.Html,
             AspSegmentKind.Output, AspSegmentKind.Html],
            kinds);
        Assert.Equal(" Language=\"VBScript\" ", page.Segments[0].Text);
        Assert.Equal("\n<html>", page.Segments[1].Text);
        var code = page.Segments[2];
        Assert.Equal(" x = 1 ", code.Text);
        Assert.Equal((2, 7, 2, 17), (code.StartLine, code.StartColumn, code.EndLine, code.EndColumn));
        Assert.Equal(" x ", page.Segments[4].Text);
        Assert.Equal("</b>", page.Segments[5].Text);
        Assert.Equal("VBScript", page.Language);
        Assert.Empty(page.Problems);
        Assert.Equal(2, page.Statements.Count);
    }

    [Fact]
    public void PercentGreaterInsideString_EndsSegment()
    {
        var page = AspPage.Parse("<% s = \"a %> b\" %>");
        Assert.Equal(2, page.Segments.Count);
        Assert.Equal(AspSegmentKind.Code, page.Segments[0].Kind);
        Assert.Equal(" s = \"a ", page.Segments[0].Text);
        Assert.Equal(AspSegmentKind.Html, page.Segments[1].Kind);
        Assert.Equal(" b\" %>", page.Segments[1].Text);
        var problem = Assert.Single(page.Problems);
        Assert.Equal(("ASP003", 1, 8), (problem.Code, problem.Line, problem.Column));
    }

    [Fact]
    public void EndIfInComment_Ignored()
    {
        var page = AspPage.Parse("<%\nIf a Then\n  x = 1 ' End If\nEnd If\nRem End If\n%>");
        Assert.Empty(page.Problems);
    }

    [Fact]
    public void Blocks_SpanHtmlSegments()
    {
        var page = AspPage.Parse("<% If a Then %><b>x</b><% Else %>y<% End If %>");
        Assert.Empty(page.Problems);
    }

    [Fact]
    public void Blocks_AllPairKinds_Balanced()
    {
        var page = AspPage.Parse(
            "<%\nDo While a\n  Select Case b\n    Case 1: c = 1\n  End Select\nLoop\nWhile d\n  With rs\n    .MoveNext\n  End With\nWend\n"
            + "For Each x In xs\n  If x Then y = 1 Else y = 2\nNext\nIf e Then\nElseIf f Then\nElse\nEnd If\n%>");
        Assert.Empty(page.Problems);

        var broken = AspPage.Parse("<%\nWhile a\n  Function F()\n  End Function\n%>");
        Assert.Equal("`While`이 닫히지 않았습니다", Assert.Single(broken.Problems).Message);
    }

    [Fact]
    public void UnclosedFor_ASP005()
    {
        var page = AspPage.Parse("<%\nFor i = 1 To 3\n  x = i\n%>");
        var problem = Assert.Single(page.Problems);
        Assert.Equal("ASP005", problem.Code);
        Assert.Equal(2, problem.Line);
        Assert.Equal("`For`가 닫히지 않았습니다", problem.Message);
    }

    [Fact]
    public void StrayEndIf_ASP004()
    {
        var page = AspPage.Parse("<%\nx = 1\nEnd If\n%>");
        var problem = Assert.Single(page.Problems);
        Assert.Equal("ASP004", problem.Code);
        Assert.Equal(3, problem.Line);
        Assert.Equal("`End If`의 여는 짝이 없습니다", problem.Message);
    }

    [Theory]
    [InlineData("Else", "`Else`")]
    [InlineData("ElseIf y Then", "`ElseIf`")]
    [InlineData("Case 1", "`Case`")]
    public void StrayElseElseIfCase_ASP004(string line, string name)
    {
        var page = AspPage.Parse("<%\nx = 1\n" + line + "\n%>");
        var problem = Assert.Single(page.Problems);
        Assert.Equal("ASP004", problem.Code);
        Assert.Equal(3, problem.Line);
        Assert.Equal(name + "의 여는 짝이 없습니다", problem.Message);
    }

    [Fact]
    public void MatchedElseElseIfCase_NoProblem()
    {
        var page = AspPage.Parse("<%\nIf a Then\n x = 1\nElseIf b Then\n x = 2\nElse\n x = 3\nEnd If\nSelect Case x\nCase 1\n y = 1\nCase Else\n y = 2\nEnd Select\n%>");
        Assert.Empty(page.Problems);
    }

    [Fact]
    public void ServerScriptBlock_Parsed()
    {
        var page = AspPage.Parse(
            "<html>\n<script language=\"VBScript\" runat=\"server\">\nFunction Twice(n)\n  Twice = n * 2\nEnd Function\n</script>\n<script>var a = 1;</script>");
        var script = Assert.Single(page.Segments, s => s.Kind == AspSegmentKind.ServerScript);
        Assert.Equal(2, script.StartLine);
        Assert.Equal(6, script.EndLine);
        Assert.StartsWith("\nFunction Twice(n)", script.Text);
        var proc = Assert.Single(page.Procedures);
        Assert.Equal("Twice", proc.Name);
        Assert.Equal((3, 5), (proc.DeclLine, proc.EndLine));
        Assert.False(page.HasServerJScript);
        Assert.Empty(page.Problems);
    }

    [Fact]
    public void ServerJScript_Flagged()
    {
        var page = AspPage.Parse("<script runat=server language=JScript>\nfunction f() { return 'x'; }\n</script>");
        Assert.True(page.HasServerJScript);
        Assert.Empty(page.Problems);

        var unclosed = AspPage.Parse("<script runat=\"server\">\nx = 1\n");
        Assert.Equal("ASP002", Assert.Single(unclosed.Problems).Code);
    }

    [Fact]
    public void Includes_FileAndVirtual()
    {
        var page = AspPage.Parse(
            "<!--#include file=\"inc/db.inc\"-->\n<p>\n<!-- #INCLUDE VIRTUAL=\"/common/adovbs.inc\" -->");
        Assert.Equal(
            [new AspInclude("inc/db.inc", false, 1), new AspInclude("/common/adovbs.inc", true, 3)],
            page.Includes);
        var segments = page.Segments.Where(s => s.Kind == AspSegmentKind.Include).Select(s => s.Text);
        Assert.Equal(["inc/db.inc", "/common/adovbs.inc"], segments);
    }

    [Fact]
    public void LockedLines_DirectiveIncludeMetadata()
    {
        var page = AspPage.Parse(
            "<%@ Language=VBScript\n CodePage=949 %>\n<p>\n<!--#include file=\"a.inc\"-->\n<% x = 1 %>\n<!--METADATA TYPE=\"TypeLib\"\n UUID=\"{00000205-0000-0010-8000-00AA006D2EA4}\" -->\nend");
        Assert.Equal([1, 2, 4, 6, 7], page.LockedLines.Order());
        Assert.Contains(page.Segments, s => s.Kind == AspSegmentKind.Metadata);
        Assert.Equal("<% x = 1 %>", page.LineText(5));
    }

    [Fact]
    public void Procedures_WithLeadingCommentAndParams()
    {
        var page = AspPage.Parse(
            "<%\nDim total\n\n' 금액 계산\n' 수량 포함\nFunction GetAmount(ByVal id, qty)\n  GetAmount = id * qty\nEnd Function\n%>");
        var proc = Assert.Single(page.Procedures);
        Assert.Equal("GetAmount", proc.Name);
        Assert.Null(proc.ClassName);
        Assert.Equal("Function", proc.Keyword);
        Assert.Equal(["id", "qty"], proc.Parameters);
        Assert.Equal((4, 6, 8), (proc.StartLine, proc.DeclLine, proc.EndLine));
        Assert.Equal("Function GetAmount(ByVal id, qty)", proc.Declaration);
        Assert.Contains(new AspDeclaration("id", "Param", 6, "GetAmount"), page.Declarations);
        Assert.Contains(new AspDeclaration("total", "Dim", 2, null), page.Declarations);
        Assert.Empty(page.Problems);
    }

    [Fact]
    public void ClassMethod_HasClassName()
    {
        var page = AspPage.Parse(
            "<%\nClass Cart\n  Private m_items\n  Public Property Get Count()\n    Count = 0\n  End Property\n  Public Sub Add(item)\n    Dim n\n  End Sub\nEnd Class\n%>");
        Assert.Empty(page.Problems);
        Assert.Equal(2, page.Procedures.Count);
        Assert.All(page.Procedures, p => Assert.Equal("Cart", p.ClassName));
        Assert.Equal("Property Get", page.Procedures[0].Keyword);
        Assert.Equal("Add", page.Procedures[1].Name);
        Assert.Contains(new AspDeclaration("Cart", "Class", 2, null), page.Declarations);
        Assert.Contains(new AspDeclaration("m_items", "Private", 3, "Cart"), page.Declarations);
        Assert.Contains(new AspDeclaration("n", "Dim", 8, "Cart.Add"), page.Declarations);
    }

    [Fact]
    public void OptionExplicitAndCodePage_Read()
    {
        var page = AspPage.Parse("<%@ Language=\"VBScript\" CodePage=\"65001\" %>\n<%\nOption Explicit\nDim a\n%>");
        Assert.True(page.OptionExplicit);
        Assert.Equal(65001, page.CodePage);
        Assert.False(page.UsesDynamicCode);

        var plain = AspPage.Parse("<% x = 1 : conn.Execute sql %>");
        Assert.False(plain.OptionExplicit);
        Assert.Null(plain.CodePage);
        Assert.False(plain.UsesDynamicCode);
        Assert.True(AspPage.Parse("<% Execute code %>").UsesDynamicCode);
    }

    [Fact]
    public void NameUses_SkipMembersAfterDot()
    {
        var page = AspPage.Parse("<%\nSub Show()\n  v = rs.Fields(\"a\").Value\n  Set rs = Nothing\n  Render v\nEnd Sub\n%>");
        Assert.DoesNotContain(page.NameUses, u => u.Name is "Fields" or "Value");
        Assert.Contains(new AspNameUse("rs", 3, false, false, "Show"), page.NameUses);
        Assert.Contains(new AspNameUse("v", 3, true, false, "Show"), page.NameUses);
        Assert.Contains(new AspNameUse("rs", 4, true, false, "Show"), page.NameUses);
        Assert.Contains(new AspNameUse("Render", 5, false, true, "Show"), page.NameUses);
        Assert.DoesNotContain(page.NameUses, u => u.Name == "Show");
    }

    [Fact]
    public void HtmlTagBalance_ASP006()
    {
        var page = AspPage.Parse("<table>\n<tr><td><div>a</div>\n<% If x Then %><TABLE><% End If %>\n</table>");
        var problem = Assert.Single(page.Problems);
        Assert.Equal("ASP006", problem.Code);
        Assert.Equal("`<table>` 닫는 태그가 모자랍니다", problem.Message);

        var stray = AspPage.Parse("<form>\n</form></form>");
        Assert.Equal(("`<form>` 여는 태그 없이 닫혔습니다", 2), (Assert.Single(stray.Problems).Message, stray.Problems[0].Line));
    }

    [Fact]
    public void HtmlTagBalance_AddedBalancedTag_SameProblemMultiset()
    {
        static string[] Asp006(string text) => AspPage.Parse(text).Problems
            .Where(p => p.Code == "ASP006").Select(p => p.Code + "|" + p.Message).Order().ToArray();

        var original = "<div>a\n<div>b</div>\n<div>c</div>\n";
        var patched = "<div>a\n<div>new</div>\n<div>b</div>\n<div>c</div>\n";
        Assert.Equal(["ASP006|`<div>` 닫는 태그가 모자랍니다"], Asp006(original));
        Assert.Equal(Asp006(original), Asp006(patched));
    }

    [Fact]
    public void ClassScopes_SameMethodNameInTwoClasses_Distinct()
    {
        var page = AspPage.Parse(
            "<%\nClass A\n  Sub Add(x)\n    Dim t\n    t = x\n  End Sub\nEnd Class\nClass B\n  Sub Add(x)\n    Dim t\n  End Sub\nEnd Class\nSub Add(x)\nEnd Sub\n%>");
        Assert.Empty(page.Problems);
        Assert.Contains(new AspDeclaration("t", "Dim", 4, "A.Add"), page.Declarations);
        Assert.Contains(new AspDeclaration("t", "Dim", 10, "B.Add"), page.Declarations);
        Assert.Contains(new AspDeclaration("x", "Param", 3, "A.Add"), page.Declarations);
        Assert.Contains(new AspDeclaration("x", "Param", 9, "B.Add"), page.Declarations);
        Assert.Contains(new AspDeclaration("x", "Param", 13, "Add"), page.Declarations);
        Assert.Contains(new AspNameUse("t", 5, true, false, "A.Add"), page.NameUses);
        Assert.Equal(["Add", "Add", "Add"], page.Procedures.Select(p => p.Name));
    }

    [Fact]
    public void ClassField_ScopedToClass()
    {
        var page = AspPage.Parse("<%\nDim g\nClass Cart\n  Private m_x\n  Public y\n  Dim z\nEnd Class\n%>");
        Assert.Contains(new AspDeclaration("g", "Dim", 2, null), page.Declarations);
        Assert.Contains(new AspDeclaration("Cart", "Class", 3, null), page.Declarations);
        Assert.Contains(new AspDeclaration("m_x", "Private", 4, "Cart"), page.Declarations);
        Assert.Contains(new AspDeclaration("y", "Public", 5, "Cart"), page.Declarations);
        Assert.Contains(new AspDeclaration("z", "Dim", 6, "Cart"), page.Declarations);
    }

    [Fact]
    public void TrailingNewline_NoPhantomLine()
    {
        var page = AspPage.Parse("<%\nSub Open()\n  x = 1\n%>\n");
        Assert.Equal(4, Assert.Single(page.Procedures).EndLine);
        Assert.Equal("%>", page.LineText(4));
        Assert.Throws<IndexOutOfRangeException>(() => page.LineText(5));
        Assert.Equal(4, page.Segments[^1].EndLine);
    }
}
