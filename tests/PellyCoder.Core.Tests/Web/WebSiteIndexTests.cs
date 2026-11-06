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
        Assert.Contains("\"version\":2", json);
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
        Assert.Contains("\"version\":2", fs.File.ReadAllText(Cache));

        fs.File.WriteAllText(Cache, "{\"version\":99,\"files\":{}}");
        Assert.Equal(new[] { "one" }, Load(fs).Names);
    }

    [Theory]
    [InlineData("{\"version\":2,\"files\":null}")]
    [InlineData("{\"version\":2,\"files\":{\"a.js\":null}}")]
    [InlineData("{\"version\":2,\"files\":{\"a.js\":{\"sha256\":\"SHA\",\"entries\":null}}}")]
    [InlineData("{\"version\":2,\"files\":{\"a.js\":{\"sha256\":\"SHA\",\"entries\":[null]}}}")]
    [InlineData("{\"version\":2,\"files\":{\"a.js\":{\"sha256\":\"SHA\",\"entries\":[{\"name\":null,\"line\":1,\"declaration\":\"x\"}]}}}")]
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
}
