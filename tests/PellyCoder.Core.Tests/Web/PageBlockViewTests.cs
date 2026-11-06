using Acornima;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class PageBlockViewTests
{
    private static PageBlockView Build(string text, int index = 0) => PageBlockView.Build(AspPage.Parse(text), text, index);

    [Fact]
    public void View_keeps_line_count_and_only_block_content()
    {
        var text = "<html>\n<body>\n<script>\nvar a = 1;\n</script>\n<p>x</p>\n";
        var v = Build(text);
        Assert.Equal(text.Split('\n').Length, v.Text.Split('\n').Length);
        var lines = v.Text.Split('\n');
        var src = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            Assert.Equal(src[i].Length, lines[i].Length);
            if (i != 3) Assert.True(string.IsNullOrWhiteSpace(lines[i]), $"line {i + 1}");
        }
        Assert.Equal("var a = 1;", lines[3]);
        Assert.Equal(PageServerCode.None, v.ServerCode);
    }

    [Fact]
    public void Output_becomes_placeholder_inside_and_outside_strings()
    {
        var text = "<script>\nvar a='<%= x %>'; var b=<%= y %>;\n</script>";
        var v = Build(text);
        Assert.Contains("var a='__asp0__'; var b=__asp1__;", v.Text);
        Assert.Null(JsDocument.Parse(v.Text, EcmaVersion.Latest, false).Error);
        Assert.Equal(PageServerCode.Output, v.ServerCode);
    }

    [Fact]
    public void Output_with_quotes_inside_js_string_parses()
    {
        var text = "<script>\nvar s = '<%= Replace(v, \"'\", \"\\'\") %>';\n</script>";
        var v = Build(text);
        Assert.Null(JsDocument.Parse(v.Text, EcmaVersion.Latest, false).Error);
    }

    [Fact]
    public void Multiline_output_keeps_lines()
    {
        var text = "<script>\nvar a = <%= f(1,\n  2,\n  3) %>;\n</script>";
        var v = Build(text);
        var lines = v.Text.Split('\n');
        Assert.Equal(text.Split('\n').Length, lines.Length);
        Assert.StartsWith("var a = __asp0__", lines[1]);
        Assert.True(string.IsNullOrWhiteSpace(lines[2]));
        Assert.True(string.IsNullOrWhiteSpace(lines[3]) || lines[3].Trim() == ";");
    }

    [Fact]
    public void Short_output_followed_by_code_keeps_following_text()
    {
        var v = Build("<script>\nf(<%=a%>,1);\n</script>");
        Assert.Contains("f(__asp0__,1);", v.Text);
    }

    [Fact]
    public void Statements_mark_block()
    {
        var text = "<script>\n<% If a Then %>x();<% End If %>\n</script>";
        var v = Build(text);
        Assert.Equal(PageServerCode.Statements, v.ServerCode);
        Assert.Equal("x();", v.Text.Split('\n')[1].Trim());
        Assert.DoesNotContain("<%", v.Text);
    }

    [Fact]
    public void Old_html_comment_wrappers_are_blanked()
    {
        var text = "<script>\n  <!--\nvar a = 1;\n//-->  \n</script>";
        var lines = Build(text).Text.Split('\n');
        Assert.True(string.IsNullOrWhiteSpace(lines[1]));
        Assert.True(string.IsNullOrWhiteSpace(lines[3]));
        Assert.Equal("var a = 1;", lines[2]);

        var cdata = Build("<script>\n//<![CDATA[\nvar a = 1;\n</script>").Text.Split('\n');
        Assert.Equal("//<![CDATA[", cdata[1]);
    }

    [Fact]
    public void SameBlocks_detects_early_close()
    {
        var o = "<p>a</p>\n<script>\nvar a = 1;\n</script>\n";
        var p = "<p>a</p>\n<script>\nvar a = '</script>';\n</script>\n";
        Assert.False(PageBlockView.SameBlocks(AspPage.Parse(o), AspPage.Parse(p), 0, o, p));
    }

    [Fact]
    public void SameBlocks_detects_change_outside_block()
    {
        var o = "<p>a</p>\n<script>\nvar a = 1;\n</script>\n";
        var outside = "<p>b</p>\n<script>\nvar a = 1;\n</script>\n";
        var inside = "<p>a</p>\n<script>\nvar a = 2;\nvar b = 3;\n</script>\n";
        Assert.False(PageBlockView.SameBlocks(AspPage.Parse(o), AspPage.Parse(outside), 0, o, outside));
        Assert.True(PageBlockView.SameBlocks(AspPage.Parse(o), AspPage.Parse(inside), 0, o, inside));
    }

    [Fact]
    public void ServerSegments_in_order()
    {
        var text = "<script>\nvar a = <%= a %>;\n<% b %>\n</script>";
        Assert.Equal(["<%= a %>", "<% b %>"], Build(text).ServerSegments);
    }

    [Fact]
    public void IsPlaceholder_matches_only_asp_names()
    {
        Assert.True(PageBlockView.IsPlaceholder("__asp12__"));
        Assert.False(PageBlockView.IsPlaceholder("__asp__"));
        Assert.False(PageBlockView.IsPlaceholder("asp1"));
    }
}
