using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Asp;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class PageScriptsTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\site" : "/site";
    private const string Cache = "/ws/.pelly/cache/web-site-index.json";
    private static readonly string[] Vendors = { "**/lib/**", "**/jquery*.js" };
    private static readonly string[] Exts = { ".asp", ".inc" };

    private static MockFileSystem Fs(params (string Path, string Text)[] files)
    {
        var fs = new MockFileSystem();
        foreach (var (p, t) in files) fs.AddFile(p, new MockFileData(Encoding.UTF8.GetBytes(t)));
        return fs;
    }

    private static readonly WebCodecRouter Codec = new(new WebTextCodec("cp949"), new AspTextCodec("cp949"), Exts);

    private static WebSiteIndexResult Index(MockFileSystem fs) =>
        WebSiteIndex.Load(fs, Root, new WebFileFilter(Root, Vendors), Codec, Cache, aspExtensions: Exts);

    private static PageScriptContext Collect(MockFileSystem fs, string rel, int block = 0, string? configured = null, WebSiteIndexResult? index = null)
    {
        var full = Root + "/" + rel;
        var text = Codec.Decode(full, fs.File.ReadAllBytes(full)).Text;
        var page = AspPage.Parse(text);
        return PageScripts.Collect(fs, Root, full, page, block, Codec, index ?? Index(fs), JqueryVersions.Find(fs, Root), configured);
    }

    private const string Two = "<script>\nfunction first() {}\n</script>\n<script>\nfunction second() {}\n</script>\n";

    [Fact]
    public void Visible_names_include_other_blocks_includes_and_script_src()
    {
        var fs = Fs(
            ("/site/a/p.asp", "<!--#include virtual=\"/inc/head.inc\"-->\n<script src=\"../js/common.js\"></script>\n<script src=\"/lib/plug.js\"></script>\n" + Two),
            ("/site/inc/head.inc", "<script>\nfunction fromInc() {}\n</script>\n"),
            ("/site/js/common.js", "function fromCommon() {}\n"),
            ("/site/lib/plug.js", "function fromLib() {}\n"));
        var ctx = Collect(fs, "a/p.asp", block: 2); // the two src blocks come first
        Assert.Contains("second", ctx.VisibleNames);
        Assert.DoesNotContain("first", ctx.VisibleNames); // the target block itself
        Assert.Contains("fromInc", ctx.VisibleNames);
        Assert.Contains("fromCommon", ctx.VisibleNames);
        Assert.Contains("fromLib", ctx.VisibleNames); // a library path still counts
        Assert.Equal(new[] { "js/common.js", "lib/plug.js" }, ctx.ScriptSources);
    }

    [Fact]
    public void Name_only_in_other_page_warns_for_page_target()
    {
        var fs = Fs(("/site/a.asp", Two), ("/site/b.asp", "<script>\nfunction onlyInB() {}\n</script>\n"), ("/site/c.js", "function inJs() {}\n"));
        var index = Index(fs);
        var ctx = Collect(fs, "a.asp", 0, index: index);
        var known = JsNameCheck.KnownSet(WebBuiltins.Default, false, JsNameCheck.SiteNames(index, ctx.VisibleNames), []);
        Assert.False(known("onlyInB"));
        Assert.True(known("inJs"));
        Assert.True(known("second"));
    }

    [Fact]
    public void Js_file_target_knows_page_block_names()
    {
        var fs = Fs(("/site/b.asp", "<script>\nfunction onlyInB() {}\n</script>\n"), ("/site/c.js", "function inJs() {}\n"));
        var index = Index(fs);
        var known = JsNameCheck.KnownSet(WebBuiltins.Default, false, JsNameCheck.SiteNames(index, null), []);
        Assert.True(known("onlyInB"));
        Assert.True(known("inJs"));
        Assert.DoesNotContain("onlyInB", index.JsNames);
        Assert.Contains("onlyInB", index.AllNames);
    }

    [Fact]
    public void Jquery_from_page_script_src()
    {
        var fs = Fs(("/site/p.asp", "<script src=\"js/jquery-1.8.3.min.js\"></script>\n" + Two), ("/site/js/jquery-1.8.3.min.js", "x"), ("/site/old/jquery-1.4.2.js", "x"));
        var ctx = Collect(fs, "p.asp");
        Assert.Equal(new Version(1, 8, 3), ctx.Jquery);
        Assert.Equal("page", ctx.JquerySource);
    }

    [Fact]
    public void Jquery_setting_wins()
    {
        var fs = Fs(("/site/p.asp", "<script src=\"js/jquery-1.8.3.min.js\"></script>\n" + Two), ("/site/js/jquery-1.8.3.min.js", "x"));
        var ctx = Collect(fs, "p.asp", configured: "1.12.4");
        Assert.Equal(new Version(1, 12, 4), ctx.Jquery);
        Assert.Equal("setting", ctx.JquerySource);
    }

    [Fact]
    public void Jquery_site_when_page_has_none()
    {
        var fs = Fs(("/site/p.asp", Two), ("/site/old/jquery-1.4.2.js", "x"), ("/site/new/jquery-3.6.0.js", "x"));
        var ctx = Collect(fs, "p.asp");
        Assert.Equal(new Version(1, 4, 2), ctx.Jquery);
        Assert.Equal("site", ctx.JquerySource);
    }

    [Fact]
    public void Script_src_relative_and_root_paths()
    {
        var fs = Fs(
            ("/site/a/b/p.asp", "<script src=\"x.js\"></script>\n<script src=\"../y.js?v=3\"></script>\n<script src=\"/z/z.js\"></script>\n" + Two),
            ("/site/a/b/x.js", "function xx() {}\n"), ("/site/a/y.js", "function yy() {}\n"), ("/site/z/z.js", "function zz() {}\n"),
            ("/site/x.js", "function wrongX() {}\n"));
        var ctx = Collect(fs, "a/b/p.asp");
        Assert.Equal(new[] { "a/b/x.js", "a/y.js", "z/z.js" }, ctx.ScriptSources);
        Assert.Contains("xx", ctx.VisibleNames);
        Assert.Contains("yy", ctx.VisibleNames);
        Assert.Contains("zz", ctx.VisibleNames);
        Assert.DoesNotContain("wrongX", ctx.VisibleNames);
    }

    [Fact]
    public void External_and_server_src_ignored()
    {
        var fs = Fs(("/site/p.asp",
            "<script src=\"http://cdn.example/a.js\"></script>\n<script src=\"https://cdn.example/b.js\"></script>\n<script src=\"//cdn.example/c.js\"></script>\n"
            + "<script src=\"<%=base%>/d.js\"></script>\n" + Two));
        var ctx = Collect(fs, "p.asp");
        Assert.Empty(ctx.ScriptSources);
    }

    [Fact]
    public void Inc_without_parents_looks_down_only()
    {
        var fs = Fs(("/site/inc/lonely.inc", "<script src=\"/js/own.js\"></script>\n" + Two), ("/site/js/own.js", "function own() {}\n"),
            ("/site/p.asp", "<script>\nfunction notParent() {}\n</script>\n"));
        var ctx = Collect(fs, "inc/lonely.inc");
        Assert.Contains("own", ctx.VisibleNames);
        Assert.Contains("second", ctx.VisibleNames);
        Assert.DoesNotContain("notParent", ctx.VisibleNames);
    }

    [Fact]
    public void Inc_parents_with_different_jquery_take_lowest()
    {
        var fs = Fs(
            ("/site/inc/shared.inc", Two),
            ("/site/new.asp", "<script src=\"/js/jquery-3.6.0.js\"></script>\n<script>\nfunction newOnly() {}\n</script>\n<!--#include virtual=\"/inc/shared.inc\"-->\n"),
            ("/site/old.asp", "<script src=\"/js/jquery-1.8.3.js\"></script>\n<!--#include file=\"inc/mid.inc\"-->\n"),
            ("/site/inc/mid.inc", "<!--#include file=\"shared.inc\"-->\n"),
            ("/site/js/jquery-3.6.0.js", "x"), ("/site/js/jquery-1.8.3.js", "x"));
        var ctx = Collect(fs, "inc/shared.inc");
        Assert.Equal(new Version(1, 8, 3), ctx.Jquery);
        Assert.Equal("page", ctx.JquerySource);
        Assert.Contains("newOnly", ctx.VisibleNames);
        Assert.DoesNotContain("first", ctx.VisibleNames); // the target block does not come back through its includers
        Assert.Contains("second", ctx.VisibleNames);
    }

    [Fact]
    public void Index_limits_are_separate_for_js_and_pages()
    {
        var files = Enumerable.Range(0, 2001).Select(i => ($"/site/pages/p{i:D4}.asp", "<script>\nfunction f" + i + "() {}\n</script>\n")).ToList();
        files.Add(("/site/z.js", "function zjs() {}\n"));
        var r = Index(Fs(files.ToArray()));
        Assert.Contains("zjs", r.JsNames);
        Assert.Equal(1, r.Files);
        Assert.Equal(2000, r.PageFiles);
        Assert.True(r.Truncated);
    }

    [Fact]
    public void Index_cache_refreshes_when_page_changes()
    {
        var fs = Fs(("/site/p.asp", "<script>\nfunction oldName() {}\n</script>\n<!--#include file=\"a.inc\"-->\n"));
        var first = Index(fs);
        Assert.Contains("oldName", first.AllNames);
        Assert.Equal(new[] { "p.asp" }, first.IncludersOf("a.inc"));
        fs.AddFile("/site/p.asp", new MockFileData("<script>\nfunction newName() {}\n</script>\n<!--#include file=\"b.inc\"-->\n"));
        var second = Index(fs);
        Assert.Contains("newName", second.AllNames);
        Assert.DoesNotContain("oldName", second.AllNames);
        Assert.Empty(second.IncludersOf("a.inc"));
        Assert.Equal(new[] { "p.asp" }, second.IncludersOf("b.inc"));
    }

    [Fact]
    public void Old_cache_version_is_rebuilt()
    {
        var fs = Fs(("/site/p.asp", "<script>\nfunction nm() {}\n</script>\n"));
        fs.AddFile(Cache, new MockFileData("{\"version\":1,\"files\":{\"p.asp\":{\"sha256\":\"x\",\"entries\":[]}}}"));
        Assert.Contains("nm", Index(fs).AllNames);
    }
}
