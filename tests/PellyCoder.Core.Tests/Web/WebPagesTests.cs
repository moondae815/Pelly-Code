using PellyCoder.Core.Files;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class WebPagesTests
{
    private static readonly string[] Default = [".asp", ".inc"];

    [Theory]
    [InlineData("/w/a.ASP", true)]
    [InlineData("/w/a.inc", true)]
    [InlineData("/w/a.htm", true)]
    [InlineData("/w/a.HTML", true)]
    [InlineData("/w/a.js", false)]
    [InlineData("/w/a.css", false)]
    public void IsPage_uses_asp_extensions_plus_htm_html(string path, bool expected) =>
        Assert.Equal(expected, WebPages.IsPage(path, Default));

    [Fact]
    public void IsPage_follows_configured_asp_extensions()
    {
        string[] custom = [".aspx"];
        Assert.False(WebPages.IsPage("/w/a.asp", custom));
        Assert.True(WebPages.IsPage("/w/a.aspx", custom));
        Assert.Contains(".htm", WebPages.PageExtensions(custom));
    }

    private sealed class FakeCodec(string name) : ITextFileCodec
    {
        public FileSnapshot Decode(string path, byte[] bytes) =>
            new(name, System.Text.Encoding.UTF8, false, "\n", false, "", "", false, bytes);
        public byte[] Encode(FileSnapshot like, string textLf) => [];
        public FileSnapshot AfterWrite(FileSnapshot current, string newTextLf, byte[] written) => current with { Path = name };
    }

    [Fact]
    public void Router_sends_pages_to_asp_codec()
    {
        var router = new WebCodecRouter(new FakeCodec("file"), new FakeCodec("page"), Default);

        Assert.Equal("page", router.Decode("/w/a.htm", []).Path);
        Assert.Equal("page", router.Decode("/w/a.asp", []).Path);
        Assert.Equal("file", router.Decode("/w/a.js", []).Path);
        Assert.Equal("file", router.Decode("/w/a.css", []).Path);
    }
}
