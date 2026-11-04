using PellyCoder.Core.Asp;
using PellyCoder.Core.Models;

namespace PellyCoder.Core.Tests.Asp;

/// <summary>Review warnings for silent-wrong edits (docs/asp-premeasure.md): no short-circuit, dropped Request reads.</summary>
public class AspLogicWarningsTests
{
    private static IReadOnlyList<string> New(string original, string patched)
    {
        var o = AspPage.Parse(original);
        var p = AspPage.Parse(patched);
        return AspLogicWarnings.New(o, new TextSpanInfo(1, o.LineCount), p, new TextSpanInfo(1, p.LineCount));
    }

    private const string Read = "<%\nid = Request(\"id\")\n";

    [Fact]
    public void Logic_CheckAndConvertSameCondition_Warned()
    {
        var warnings = New(Read + "x = 1\n%>", Read + "If IsNumeric(id) And CLng(id) > 0 Then x = 1\n%>");

        Assert.Equal(["And/Or는 단축 평가를 하지 않습니다: id를 같은 조건에서 검사·변환합니다"], warnings);
        Assert.Single(New(Read + "x = 1\n%>", Read + "If x = 0 Then\nElseIf Not IsDate(Request(\"d\")) Or CDate(Request(\"d\")) < Now Then\nEnd If\n%>"),
            w => w.Contains("Request(\"d\")를", StringComparison.Ordinal));
    }

    [Fact]
    public void Logic_NestedIf_NotWarned()
    {
        Assert.Empty(New(Read + "x = 1\n%>", Read + "If IsNumeric(id) Then\n  If CLng(id) > 0 Then x = 1\nEnd If\n%>"));
        // Different expressions, or no And/Or: nothing to warn about.
        Assert.Empty(New(Read + "x = 1\n%>", Read + "If IsNumeric(id) And CLng(n) > 0 Then x = 1\n%>"));
        Assert.Empty(New(Read + "x = 1\n%>", Read + "If IsNumeric(CLng(id)) Then x = 1\n%>"));
    }

    [Fact]
    public void Logic_AlreadyInOriginal_NotWarned()
    {
        const string text = Read + "If IsNumeric(id) And CLng(id) > 0 Then x = 1\n%>";
        Assert.Empty(New(text, text.Replace("x = 1", "x = 2")));
    }

    [Fact]
    public void Logic_RequestReadRemoved_Warned()
    {
        const string original = "<%\nkeyword = Trim(Request.Form(\"keyword\"))\nIf c Then page = Request.QueryString(\"page\")\nx = 1\n%>";
        var warnings = New(original, "<%\nx = 1\n%>");

        Assert.Equal(["기존 입력값 읽기 줄이 사라졌습니다: keyword", "기존 입력값 읽기 줄이 사라졌습니다: page"], warnings);
    }

    [Fact]
    public void Logic_RequestReadKeptOrChanged_NotWarned()
    {
        const string original = "<%\nkeyword = Trim(Request(\"keyword\"))\n%>";
        Assert.Empty(New(original, "<%\nDim keyword\nKEYWORD = Replace(Request(\"keyword\"), \"'\", \"''\")\n%>"));
        // Reading something other than Request into the variable is a dropped read.
        Assert.Single(New(original, "<%\nkeyword = \"\"\n%>"));
    }

    [Fact]
    public void Logic_OnlyTargetSpanCounts()
    {
        var o = AspPage.Parse("<%\nid = Request(\"id\")\nx = 1\n%>");
        var p = AspPage.Parse("<%\nid = Request(\"id\")\nx = 2\n%>");

        // The read on line 2 is outside both spans; nothing was dropped from the target.
        Assert.Empty(AspLogicWarnings.New(o, new TextSpanInfo(3, 3), p, new TextSpanInfo(3, 3)));
        Assert.Single(AspLogicWarnings.New(o, new TextSpanInfo(2, 3), p, new TextSpanInfo(3, 3)));
    }
}
