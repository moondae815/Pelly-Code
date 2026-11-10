using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class WebSiteIndexTests
{
    // A full path on each OS: the index enumerates full paths, as the real site root always is.
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\site" : "/site";
    private const string Cache = "/ws/.pelly/cache/web-site-index.json";
    private static readonly string[] Vendors = { "**/lib/**", "**/plugin*/**", "**/vendor/**", "**/jquery*.js" };

    private static MockFileSystem Fs(params (string Path, string Text)[] files)
    {
        var fs = new MockFileSystem();
        foreach (var (p, t) in files) fs.AddFile(p, new MockFileData(Encoding.UTF8.GetBytes(t)));
        return fs;
    }

    private static WebSiteIndexResult Load(MockFileSystem fs, int maxFiles = 2000, long maxBytes = 1_048_576) =>
        WebSiteIndex.Load(fs, Root, new WebFileFilter(Root, Vendors), new WebTextCodec("cp949"), Cache, maxFiles, maxBytes);

    [Fact]
    public void Index_TopLevelFunctionsAndVars()
    {
        var fs = Fs(("/site/js/a.js", "function foo(a) {\n}\nvar x = 1, y = 2;\nlet z = function () {};\nconst w = () => 1;\n"));
        var r = Load(fs);
        Assert.Equal(1, r.Files);
        Assert.Equal(new[] { "foo", "w", "x", "y", "z" }, r.Names.OrderBy(n => n, StringComparer.Ordinal));
        var foo = r.Entries.Single(e => e.Name == "foo");
        Assert.Equal("js/a.js", foo.RelativePath);
        Assert.Equal(1, foo.Line);
        Assert.Equal("function foo(a) {", foo.Declaration);
        Assert.Equal(3, r.Entries.Single(e => e.Name == "x").Line);
        Assert.Equal(4, r.Entries.Single(e => e.Name == "z").Line);
    }

    [Fact]
    public void Index_NestedFunctionsExcluded()
    {
        var fs = Fs(("/site/a.js", "$(function () {\n  function inner() {}\n  var local = 1;\n});\nfunction outer() { function deep() {} }\n"));
        var r = Load(fs);
        Assert.Equal(new[] { "outer" }, r.Names);
    }

    [Fact]
    public void Index_SkipsVendorAndMinified()
    {
        var fs = Fs(("/site/lib/v.js", "function vendorFn() {}\n"),
            ("/site/b.min.js", "function minFn() {}\n"),
            ("/site/ok.js", "function okFn() {}\n"));
        var r = Load(fs);
        Assert.Equal(1, r.Files);
        Assert.Equal(new[] { "okFn" }, r.Names);
    }

    [Fact]
    public void Index_UnparseableFile_FallbackScan()
    {
        var fs = Fs(("/site/a.js", "function good() {\n}\nfunction bad( {\n  var = ;;\nfunction other() {}\n"));
        var r = Load(fs);
        Assert.Contains("good", r.Names);
        Assert.Equal(1, r.Files);
    }

    [Fact]
    public void Index_CacheWritten_AndUpdatedOnChange()
    {
        var fs = Fs(("/site/a.js", "function one() {}\n"), ("/site/b.js", "function gone() {}\n"));
        Assert.Equal(new[] { "gone", "one" }, Load(fs).Names.OrderBy(n => n, StringComparer.Ordinal));
        Assert.True(fs.File.Exists(Cache));
        var json = fs.File.ReadAllText(Cache);
        Assert.Contains("\"version\":5", json);
        Assert.Contains("\"a.js\"", json);
        Assert.DoesNotContain(fs.AllFiles, f => f.EndsWith(".tmp"));

        fs.File.WriteAllText("/site/a.js", "function two() {}\n");
        fs.File.Delete("/site/b.js");
        var r = Load(fs);
        Assert.Equal(new[] { "two" }, r.Names);
        Assert.DoesNotContain("gone", fs.File.ReadAllText(Cache));
    }

    [Fact]
    public void Index_CorruptCache_Rebuilt()
    {
        var fs = Fs(("/site/a.js", "function one() {}\n"), (Cache, "{ not json"));
        Assert.Equal(new[] { "one" }, Load(fs).Names);
        Assert.Contains("\"version\":5", fs.File.ReadAllText(Cache));

        fs.File.WriteAllText(Cache, "{\"version\":99,\"files\":{}}");
        Assert.Equal(new[] { "one" }, Load(fs).Names);
    }

    [Theory]
    [InlineData("{\"version\":5,\"files\":null}")]
    [InlineData("{\"version\":5,\"files\":{\"a.js\":null}}")]
    [InlineData("{\"version\":5,\"files\":{\"a.js\":{\"sha256\":\"SHA\",\"entries\":null}}}")]
    [InlineData("{\"version\":5,\"files\":{\"a.js\":{\"sha256\":\"SHA\",\"entries\":[null]}}}")]
    [InlineData("{\"version\":5,\"files\":{\"a.js\":{\"sha256\":\"SHA\",\"entries\":[{\"name\":null,\"line\":1,\"declaration\":\"x\"}]}}}")]
    public void Index_NullMembersCache_Rebuilt(string cache)
    {
        const string text = "function one() {}\n";
        var sha = PellyCoder.Core.Files.TextFileCodec.HashHex(Encoding.UTF8.GetBytes(text));
        var fs = Fs(("/site/a.js", text), (Cache, cache.Replace("SHA", sha)));

        Assert.Equal(new[] { "one" }, Load(fs).Names);
        var json = fs.File.ReadAllText(Cache);
        Assert.Contains("\"name\":\"one\"", json);
        Assert.DoesNotContain("null", json);
    }

    [Fact]
    public void Index_Truncated_AtMaxFiles()
    {
        var fs = Fs(("/site/a.js", "function a1() {}\n"), ("/site/b.js", "function b1() {}\n"), ("/site/c.js", "function c1() {}\n"));
        var r = Load(fs, maxFiles: 2);
        Assert.True(r.Truncated);
        Assert.Equal(2, r.Files);
        Assert.Equal(new[] { "a1", "b1" }, r.Names.OrderBy(n => n, StringComparer.Ordinal));
        Assert.False(Load(fs, maxFiles: 3).Truncated);
    }

    [Fact]
    public void Index_TooLarge_Skipped()
    {
        var fs = Fs(("/site/big.js", "function big() {}\n" + new string(' ', 500)), ("/site/s.js", "function small() {}\n"));
        var r = Load(fs, maxBytes: 100);
        Assert.Equal(new[] { "small" }, r.Names);
        Assert.Equal(1, r.Files);
    }

    [Fact]
    public void Index_HasJsCallsAndScriptSources()
    {
        var fs = Fs(
            ("/site/js/order.js", "function fnSearch(f) {}\nfunction other() { fnSearch(1); }\n"),
            ("/site/admin/list.asp", "<script src=\"../js/order.js\"></script>\n<input onclick=\"fnSearch(this.form)\">\n"),
            ("/site/inc/head.inc", "<script src=\"/js/order.js?v=2\"></script>\n"));
        var r = Load(fs);
        Assert.Contains(r.JsCalls, c => c.RelativePath == "js/order.js" && c.Call.Name == "fnSearch" && c.Call.Line == 2);
        Assert.Contains(r.JsCalls, c => c.RelativePath == "admin/list.asp" && c.Call.Source == JsCallSource.Attribute && c.Call.Line == 2);
        Assert.Equal(["js/order.js"], r.ScriptSources["admin/list.asp"]);
        Assert.Equal(["js/order.js"], r.ScriptSources["inc/head.inc"]);
    }

    [Fact]
    public void Cache_Version3_IsRebuilt()
    {
        var fs = Fs(("/site/js/a.js", "go(1);\n"));
        var sha = PellyCoder.Core.Files.TextFileCodec.HashHex(Encoding.UTF8.GetBytes("go(1);\n"));
        fs.AddFile(Cache, new MockFileData("{\"version\":3,\"spKey\":\"\",\"files\":{\"js/a.js\":{\"sha256\":\"" + sha +
            "\",\"entries\":[],\"includes\":[],\"spCalls\":[]}}}"));
        var r = Load(fs);
        Assert.Contains(r.JsCalls, c => c.Call.Name == "go");
        Assert.Contains("\"version\":5", fs.File.ReadAllText(Cache));
    }

    [Fact]
    public void Cache_Reused_KeepsCalls()
    {
        var fs = Fs(("/site/js/a.js", "go(1);\n"));
        Load(fs);
        var again = Load(fs);
        Assert.Contains(again.JsCalls, c => c.Call.Name == "go" && c.Call.Arguments == 1);
    }

    [Fact]
    public void Index_HasFormFacts_PageAndJs()
    {
        var fs = Fs(
            ("/site/js/memo.js", "$.post(\"memo_save.asp\", { memoText: t });\n"),
            ("/site/admin/save.asp", "<%\nv = Request.Form(\"memoText\")\n%>\n"),
            ("/site/admin/plain.asp", "<p>x</p>\n"));
        var r = Load(fs);
        Assert.Equal(["memoText"], Assert.Single(r.FormFacts["js/memo.js"].Senders).Fields);
        Assert.Equal(new FormRead("memoText", FormReadSource.Form, 2), Assert.Single(r.FormFacts["admin/save.asp"].Reads));
        Assert.False(r.FormFacts.ContainsKey("admin/plain.asp"));
    }

    [Fact]
    public void Cache_Version4_IsRebuilt()
    {
        var text = "<%\nv = Request(\"k\")\n%>\n";
        var fs = Fs(("/site/a.asp", text));
        var sha = PellyCoder.Core.Files.TextFileCodec.HashHex(Encoding.UTF8.GetBytes(text));
        fs.AddFile(Cache, new MockFileData("{\"version\":4,\"spKey\":\"\",\"files\":{\"a.asp\":{\"sha256\":\"" + sha +
            "\",\"entries\":[],\"includes\":[],\"spCalls\":[],\"calls\":[],\"scriptSources\":[]}}}"));
        var r = Load(fs);
        Assert.Equal("k", Assert.Single(r.FormFacts["a.asp"].Reads).Key);
        Assert.Contains("\"version\":5", fs.File.ReadAllText(Cache));
    }

    [Fact]
    public void Cache_Reused_KeepsFormFacts()
    {
        var fs = Fs(("/site/a.asp", "<form action=\"s.asp\" method=\"post\"><input name=\"k\"></form>\n"));
        Load(fs);
        var again = Load(fs);
        var s = Assert.Single(again.FormFacts["a.asp"].Senders);
        Assert.Equal("s.asp", s.Url);
        Assert.True(s.Post);
        Assert.Equal(["k"], s.Fields);
    }

    [Fact]
    public void Cache_NullFormFacts_Rebuilt()
    {
        var text = "<%\nv = Request(\"k\")\n%>\n";
        var fs = Fs(("/site/a.asp", text));
        var sha = PellyCoder.Core.Files.TextFileCodec.HashHex(Encoding.UTF8.GetBytes(text));
        fs.AddFile(Cache, new MockFileData("{\"version\":5,\"spKey\":\"\",\"files\":{\"a.asp\":{\"sha256\":\"" + sha +
            "\",\"entries\":[],\"includes\":[],\"spCalls\":[],\"calls\":[],\"scriptSources\":[],\"forms\":{\"reads\":null}}}}"));
        Assert.Equal("k", Assert.Single(Load(fs).FormFacts["a.asp"].Reads).Key);
    }
}
