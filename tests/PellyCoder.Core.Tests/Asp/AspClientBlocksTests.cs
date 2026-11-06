using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Tests.Asp;

public class AspClientBlocksTests
{
    private static IReadOnlyList<AspClientBlock> Blocks(string text) => AspPage.Parse(text).ClientBlocks;

    private static string Content(string text, AspClientBlock b) => text[b.ContentStart..b.ContentEnd];

    [Fact]
    public void Plain_script_and_style_are_blocks()
    {
        const string text = "<script>a();</script>\n<style>p{}</style>";
        var blocks = Blocks(text);
        Assert.Equal(2, blocks.Count);
        Assert.Equal(AspClientBlockKind.Script, blocks[0].Kind);
        Assert.Equal("a();", Content(text, blocks[0]));
        Assert.Equal(AspClientBlockKind.Style, blocks[1].Kind);
        Assert.Equal("p{}", Content(text, blocks[1]));
    }

    [Theory]
    [InlineData("<script type=\"text/javascript\">a</script>", 1)]
    [InlineData("<script type=\"application/javascript\">a</script>", 1)]
    [InlineData("<script type=\"text/jscript\">a</script>", 1)]
    [InlineData("<script type=\"module\">a</script>", 1)]
    [InlineData("<script language=\"JavaScript1.2\">a</script>", 1)]
    [InlineData("<script language=\"jscript\">a</script>", 1)]
    [InlineData("<script type=\"text/template\"><div></div></script>", 0)]
    [InlineData("<script language=\"vbscript\">a</script>", 0)]
    [InlineData("<script type=\"text/vbscript\">a</script>", 0)]
    [InlineData("<style type=\"text/less\">a</style>", 0)]
    public void Type_and_language_filter(string text, int count) => Assert.Equal(count, Blocks(text).Count);

    [Fact]
    public void Runat_server_is_not_a_client_block()
    {
        var page = AspPage.Parse("<script runat=\"server\" language=\"vbscript\">\nSub A\nEnd Sub\n</script>");
        Assert.Empty(page.ClientBlocks);
        Assert.Contains(page.Segments, s => s.Kind == AspSegmentKind.ServerScript);
    }

    [Fact]
    public void Script_inside_html_comment_is_ignored() =>
        Assert.Empty(Blocks("<!-- <script>x()</script> -->"));

    [Fact]
    public void Script_text_inside_vbscript_string_is_ignored() =>
        Assert.Empty(Blocks("<% Response.Write \"<script>alert(1)</script>\" %>"));

    [Fact]
    public void Closing_tag_inside_js_string_ends_block()
    {
        const string text = "<script>var s=\"</script>\";</script>";
        var blocks = Blocks(text);
        Assert.Equal("var s=\"", Content(text, blocks[0]));
    }

    [Fact]
    public void Closing_tag_inside_asp_block_is_ignored()
    {
        const string text = "<script>var a=1;<% x = \"</script>\" %>var b=2;</script>";
        var blocks = Blocks(text);
        Assert.Single(blocks);
        Assert.Equal(text.LastIndexOf("</script>", StringComparison.Ordinal), blocks[0].ContentEnd);
    }

    [Fact]
    public void Output_expression_inside_block_keeps_block()
    {
        var page = AspPage.Parse("<script>var n='<%= no %>';</script>");
        Assert.Single(page.ClientBlocks);
        Assert.Single(page.Segments, s => s.Kind == AspSegmentKind.Output);
    }

    [Fact]
    public void Src_script_is_listed_with_empty_content()
    {
        var blocks = Blocks("<script src=\"/js/common.js\"></script>");
        Assert.Equal("/js/common.js", blocks[0].Src);
        Assert.Equal(blocks[0].ContentStart, blocks[0].ContentEnd);
    }

    [Fact]
    public void Unclosed_block()
    {
        var blocks = Blocks("<script>\nvar a;");
        Assert.Single(blocks);
        Assert.False(blocks[0].Closed);
    }

    [Fact]
    public void Case_insensitive_tags()
    {
        const string text = "<SCRIPT TYPE=\"TEXT/JAVASCRIPT\">a</Script>";
        var blocks = Blocks(text);
        Assert.Single(blocks);
        Assert.Equal("a", Content(text, blocks[0]));
    }

    [Fact]
    public void Lines_are_page_lines()
    {
        var blocks = Blocks("<p>1</p>\n<p>2</p>\n<p>3</p>\n<script>a();\nb();\n</script>");
        Assert.Equal(4, blocks[0].OpenLine);
        Assert.Equal(4, blocks[0].ContentStartLine);
        Assert.Equal(5, blocks[0].ContentEndLine);
        Assert.Equal(6, blocks[0].CloseLine);

        var next = Blocks("<p>1</p>\n<p>2</p>\n<p>3</p>\n<script>\na();\nb();\n</script>");
        Assert.Equal(4, next[0].OpenLine);
        Assert.Equal(5, next[0].ContentStartLine);
        Assert.Equal(6, next[0].ContentEndLine);
        Assert.Equal(7, next[0].CloseLine);
    }

    [Fact]
    public void ClientBlockAt_content_lines_only()
    {
        var page = AspPage.Parse("<p>x</p>\n<script>\na();\nb();\n</script>\n<style>\np{}\n</style>");
        Assert.Null(page.ClientBlockAt(1));
        Assert.Null(page.ClientBlockAt(2));
        Assert.Equal(0, page.ClientBlockAt(3)?.Index);
        Assert.Equal(0, page.ClientBlockAt(4)?.Index);
        Assert.Null(page.ClientBlockAt(5));
        Assert.Equal(1, page.ClientBlockAt(7)?.Index);
        Assert.Null(page.ClientBlockAt(8));
    }
}
