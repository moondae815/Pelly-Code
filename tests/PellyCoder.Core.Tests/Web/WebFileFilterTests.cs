using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class WebFileFilterTests
{
    private static readonly string[] Defaults =
        { "**/lib/**", "**/plugin*/**", "**/vendor/**", "**/jquery*.js", "**/jquery*.css", "**/bootstrap*" };

    private const string Root = "/site";

    [Fact]
    public void DoubleStar_AnyDepth()
    {
        Assert.True(WebGlob.IsMatch("**/lib/**", "lib/a.js"));
        Assert.True(WebGlob.IsMatch("**/lib/**", "x/y/lib/z/a.js"));
        Assert.False(WebGlob.IsMatch("**/lib/**", "x/library/a.js"));
        Assert.True(WebGlob.IsMatch("**/jquery*.js", "jquery.js"));
    }

    [Fact]
    public void Star_OneSegment()
    {
        Assert.True(WebGlob.IsMatch("js/*.js", "js/a.js"));
        Assert.False(WebGlob.IsMatch("js/*.js", "js/sub/a.js"));
        Assert.True(WebGlob.IsMatch("a?.js", "ab.js"));
        Assert.False(WebGlob.IsMatch("a?.js", "a/.js"));
    }

    [Fact]
    public void CaseInsensitive_Backslash()
    {
        Assert.True(WebGlob.IsMatch("**/Lib/**", "x\\LIB\\a.js"));
    }

    [Fact]
    public void MinName_Minified()
    {
        var f = new WebFileFilter(Root, Defaults);
        var r = f.Classify("/site/js/app.min.js", "var a=1;\n");
        Assert.Equal(WebFileVerdict.Minified, r.Verdict);
        Assert.Contains("압축된 파일은 고치지 않습니다", r.Reason);
    }

    [Fact]
    public void LongLines_Minified()
    {
        var f = new WebFileFilter(Root, Defaults);
        var text = new string('a', 2000) + "\n" + new string('b', 1500) + "\n";
        Assert.Equal(WebFileVerdict.Minified, f.Classify("/site/js/app.js", text).Verdict);
        var avg = string.Join("\n", Enumerable.Repeat(new string('x', 600), 3)) + "\n";
        Assert.Equal(WebFileVerdict.Minified, f.Classify("/site/js/b.js", avg).Verdict);
    }

    [Fact]
    public void NormalFile_Ok()
    {
        var f = new WebFileFilter(Root, Defaults);
        var r = f.Classify("/site/js/app.js", "// app\nvar a = 1;\nfunction f() { return a; }\n");
        Assert.Equal(WebFileVerdict.Ok, r.Verdict);
        Assert.Null(r.Reason);
    }

    [Fact]
    public void LibPath_Vendor()
    {
        var f = new WebFileFilter(Root, Defaults);
        var r = f.Classify("/site/lib/a.js", "var a = 1;\n");
        Assert.Equal(WebFileVerdict.Vendor, r.Verdict);
        Assert.Contains("라이브러리 파일은 고치지 않습니다(**/lib/**", r.Reason);
        Assert.Contains("web.vendorPaths에서 빼면 고칠 수 있습니다", r.Reason);
    }

    [Fact]
    public void JqueryName_Vendor()
    {
        var f = new WebFileFilter(Root, Defaults);
        Assert.Equal(WebFileVerdict.Vendor, f.Classify("/site/js/jquery-1.8.3.js", "var a = 1;\n").Verdict);
    }

    [Fact]
    public void LicenseHeader_Vendor()
    {
        var f = new WebFileFilter(Root, Defaults);
        Assert.Equal(WebFileVerdict.Vendor,
            f.Classify("/site/js/x.js", "/*!\n * jQuery JavaScript Library v1.8.3\n */\nvar a;\n").Verdict);
        Assert.Equal(WebFileVerdict.Vendor,
            f.Classify("/site/js/y.js", "/**\n * @license MIT\n */\nvar a;\n").Verdict);
        Assert.Equal(WebFileVerdict.Vendor,
            f.Classify("/site/js/z.js", "/*\n * (c) 2012 Someone. All rights under License X\n */\nvar a;\n").Verdict);
        Assert.Equal(WebFileVerdict.Ok,
            f.Classify("/site/js/w.js", "/*\n * (c) 2012 Someone\n */\nvar a;\n").Verdict);
    }

    [Fact]
    public void VendorPathRemoved_Ok()
    {
        var f = new WebFileFilter(Root, Defaults.Where(p => p != "**/lib/**").ToArray());
        Assert.Equal(WebFileVerdict.Ok, f.Classify("/site/lib/a.js", "var a = 1;\n").Verdict);
    }

    [Fact]
    public void OutsideSiteRoot_PathRulesIgnored()
    {
        var f = new WebFileFilter(Root, Defaults);
        Assert.Equal(WebFileVerdict.Ok, f.Classify("/other/lib/a.js", "var a = 1;\n").Verdict);
    }
}
