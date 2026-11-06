using PellyCoder.Core.Asp;
using PellyCoder.Core.Models;
using PellyCoder.Core.Reporting;

namespace PellyCoder.Core.Tests.Asp;

/// <summary>Edits that touch a client block get JS and CSS checks (design 4.5).</summary>
public sealed class AspClientChecksTests
{
    private static AspClientVerdict Judge(string original, string patched, int fromLine, int toLine, string? jsTarget = null) =>
        AspClientChecks.Judge(AspPage.Parse(original), original, AspPage.Parse(patched), patched,
            [new EditableRegion(new TextSpanInfo(fromLine, toLine), "target")], jsTarget);

    private static AspCheckStatus Status(AspClientVerdict v, string check) => v.Rows.Single(r => r.Check == check).Status;

    private const string OneScript =
        "<html>\n" +                       // 1
        "<script>\n" +                     // 2
        "function a() { return 1; }\n" +   // 3
        "</script>\n" +                    // 4
        "</html>\n";                       // 5

    [Fact]
    public void Lines_across_block_with_js_syntax_error_rejected()
    {
        var patched = OneScript.Replace("return 1;", "return ( ;");

        var v = Judge(OneScript, patched, 3, 3);

        Assert.Equal(FailureKinds.JsSyntaxErrors, v.FailureKind);
        Assert.Equal(AspCheckStatus.Failed, Status(v, "JsSyntax"));
        Assert.NotEmpty(v.Diagnostics);
    }

    [Fact]
    public void Lines_across_es5_block_with_arrow_rejected()
    {
        var patched = OneScript.Replace("return 1;", "var f = () => 1; return f();");

        var v = Judge(OneScript, patched, 3, 3);

        Assert.Equal(FailureKinds.JsLevelExceeded, v.FailureKind);
        Assert.Equal(AspCheckStatus.Failed, Status(v, "JsLevel"));
        Assert.Equal(AspCheckStatus.Passed, Status(v, "JsSyntax"));
        Assert.Equal(3, v.LevelLine);
    }

    [Fact]
    public void Lines_across_block_with_new_eval_is_a_safety_rejection()
    {
        var patched = OneScript.Replace("return 1;", "return eval('1');");

        var v = Judge(OneScript, patched, 3, 3);

        Assert.Equal(FailureKinds.JsSafetyRejected, v.FailureKind);
        Assert.True(v.IsSafety);
        Assert.Equal(AspCheckStatus.Failed, Status(v, "JsSafety"));
    }

    [Fact]
    public void Style_block_structure_and_safety_are_judged()
    {
        const string page = "<style>\na { color: red; }\n</style>\n";

        var broken = Judge(page, page.Replace("color: red; }", "color: red;"), 2, 2);
        var unsafeCss = Judge(page, page.Replace("color: red;", "width: expression(1);"), 2, 2);

        Assert.Equal(FailureKinds.CssStructureErrors, broken.FailureKind);
        Assert.Equal(AspCheckStatus.Failed, Status(broken, "CssSyntax"));
        Assert.Equal(FailureKinds.CssSafetyRejected, unsafeCss.FailureKind);
        Assert.True(unsafeCss.IsSafety);
        Assert.Equal(AspCheckStatus.Failed, Status(unsafeCss, "CssSafety"));
    }

    [Fact]
    public void Proc_containing_script_block_is_judged()
    {
        const string page =
            "<% Sub DrawScript() %><script>\n" + // 1
            "var a = 1;\n" +                     // 2
            "</script><% End Sub %>\n";          // 3
        var patched = page.Replace("var a = 1;", "var a = eval('1');");

        var v = Judge(page, patched, 1, 3);

        Assert.Equal(FailureKinds.JsSafetyRejected, v.FailureKind);
    }

    [Fact]
    public void New_block_baseline_is_lowest_other_block_level()
    {
        const string page =
            "<script>\n" +                      // 1
            "var a = function () {};\n" +       // 2
            "</script>\n" +                     // 3
            "<p>x</p>\n";                       // 4
        var patched = page + "<script>\nvar b = () => 1;\n</script>\n";

        var es5 = Judge(page, patched, 2, 4);
        var latest = Judge(page, patched, 2, 4, "latest");

        Assert.Equal(FailureKinds.JsLevelExceeded, es5.FailureKind);
        Assert.Null(latest.FailureKind);
    }

    [Fact]
    public void Range_without_blocks_has_no_client_rows()
    {
        const string page = "<p>a</p>\n<script>\nvar a = 1;\n</script>\n<p>b</p>\n";

        var v = Judge(page, page.Replace("<p>b</p>", "<p>c</p>"), 5, 5);

        Assert.Empty(v.Rows);
        Assert.Null(v.FailureKind);
    }

    [Fact]
    public void Changed_output_in_block_is_allowed_in_asp()
    {
        const string page = "<script>\nvar n = <%= x %>;\n</script>\n";

        var v = Judge(page, page.Replace("<%= x %>", "<%= y %>"), 2, 2);

        Assert.Null(v.FailureKind);
        Assert.Equal(AspCheckStatus.Passed, Status(v, "JsSyntax"));
        Assert.Equal(AspCheckStatus.Passed, Status(v, "JsSafety"));
    }

    [Fact]
    public void Statements_block_falls_back_to_pairs()
    {
        const string page = "<script>\nvar a = 1;\n<% If x Then %>\nvar b = 2;\n<% End If %>\n</script>\n";

        var ok = Judge(page, page.Replace("var a = 1;", "var a = (1);"), 2, 2);
        var broken = Judge(page, page.Replace("var a = 1;", "var a = (1;"), 2, 2);

        Assert.Null(ok.FailureKind);
        Assert.Equal(AspCheckStatus.Skipped, Status(ok, "JsLevel"));
        Assert.Equal(FailureKinds.JsSyntaxErrors, broken.FailureKind);
    }

    [Fact]
    public void Unclosed_or_external_script_is_not_judged()
    {
        const string page = "<script src=\"a.js\"></script>\n<p>x</p>\n";

        var v = Judge(page, page.Replace("<p>x</p>", "<p>y</p>"), 1, 2);

        Assert.Empty(v.Rows);
    }

    [Fact]
    public void New_eval_block_in_blockless_range_rejected()
    {
        const string page = "<p>a</p>\n<p>b</p>\n";

        var v = Judge(page, "<p>a</p>\n<script>\neval('x');\n</script>\n", 2, 2);

        Assert.Equal(FailureKinds.JsSafetyRejected, v.FailureKind);
        Assert.True(v.IsSafety);
    }

    [Fact]
    public void Insert_before_overlapped_block_with_existing_eval_passes()
    {
        const string page = "<script>\nvar a = eval('1');\n</script>\n";
        var patched = "<script>\nvar n = 1;\n</script>\n" + page;

        var v = Judge(page, patched, 1, 3);

        Assert.Null(v.FailureKind);
    }

    [Fact]
    public void Insert_after_overlapped_block_with_existing_eval_passes()
    {
        const string page = "<script>\nvar a = eval('1');\n</script>\n";

        var v = Judge(page, page + "<script>\nvar n = 1;\n</script>\n", 1, 3);

        Assert.Null(v.FailureKind);
    }

    [Fact]
    public void Blocks_outside_the_region_are_not_judged_and_keep_pairing()
    {
        const string page = "<script>\nvar a = eval('1');\n</script>\n<p>x</p>\n<script>\nvar b = 1;\n</script>\n";

        // An inserted block in the middle region: the eval block before it is outside and untouched.
        var v = Judge(page, page.Replace("<p>x</p>", "<p>x</p>\n<script>\nvar c = 2;\n</script>"), 4, 4);

        Assert.Null(v.FailureKind);
        Assert.Equal(AspCheckStatus.Passed, Status(v, "JsSafety"));
    }

    [Fact]
    public void Deleted_closing_tag_rejected()
    {
        const string page = "<script>\nvar a = 1;\n</script>\n<p>x</p>\n";

        var v = Judge(page, "<script>\nvar a = 2;\n<p>x</p>\n", 2, 3);

        Assert.Equal(FailureKinds.JsSyntaxErrors, v.FailureKind);
        Assert.Equal(AspCheckStatus.Failed, Status(v, "JsSyntax"));
    }
}
