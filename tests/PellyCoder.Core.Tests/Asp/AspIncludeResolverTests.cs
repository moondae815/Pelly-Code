using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Asp;

namespace PellyCoder.Core.Tests.Asp;

public class AspIncludeResolverTests
{
    private const string Root = "/w/site";
    private static readonly AspTextCodec Codec = new("cp949");

    private static MockFileSystem Fs(params (string Path, string Text)[] files)
    {
        var fs = new MockFileSystem();
        foreach (var (p, t) in files) fs.AddFile(p, new MockFileData(Encoding.UTF8.GetBytes(t)));
        return fs;
    }

    private static AspIncludeSet Run(MockFileSystem fs, string path, int maxDepth = 5, int maxFiles = 40)
    {
        var page = AspPage.Parse(fs.File.ReadAllText(path));
        return AspIncludeResolver.Resolve(fs, path, page, Root, Codec, maxDepth, maxFiles);
    }

    private static string Full(string path) => new MockFileSystem().Path.GetFullPath(path);

    private static string Inc(string kind, string path) => $"<!-- #include {kind}=\"{path}\" -->\n";

    [Fact]
    public void File_RelativeToIncludingFile()
    {
        var fs = Fs(("/w/site/a/main.asp", Inc("file", "..\\lib\\util.inc")), ("/w/site/lib/util.inc", "<% x = 1 %>"));

        var set = Run(fs, "/w/site/a/main.asp");

        Assert.Equal([Full("/w/site/lib/util.inc")], set.Files.Select(f => f.Path));
        Assert.Empty(set.Unresolved);
        Assert.False(set.Truncated);
    }

    [Fact]
    public void Virtual_FromSiteRoot()
    {
        var fs = Fs(("/w/site/a/main.asp", Inc("virtual", "/lib/util.inc")), ("/w/site/lib/util.inc", "x"));

        var set = Run(fs, "/w/site/a/main.asp");

        Assert.Equal([Full("/w/site/lib/util.inc")], set.Files.Select(f => f.Path));
    }

    [Fact]
    public void Nested_DepthLimit_Truncated()
    {
        var fs = Fs(("/w/site/f0.asp", Inc("file", "f1.inc")), ("/w/site/f1.inc", Inc("file", "f2.inc")),
            ("/w/site/f2.inc", Inc("file", "f3.inc")), ("/w/site/f3.inc", "x"));

        var set = Run(fs, "/w/site/f0.asp", maxDepth: 2);

        Assert.Equal(2, set.Files.Count);
        Assert.True(set.Truncated);
    }

    [Fact]
    public void Cycle_Ignored()
    {
        var fs = Fs(("/w/site/a.asp", Inc("file", "b.inc")), ("/w/site/b.inc", Inc("file", "a.asp") + Inc("file", "b.inc")));

        var set = Run(fs, "/w/site/a.asp");

        Assert.Equal([Full("/w/site/b.inc")], set.Files.Select(f => f.Path));
        Assert.Empty(set.Unresolved);
        Assert.False(set.Truncated);
    }

    [Fact]
    public void Missing_Unresolved()
    {
        var fs = Fs(("/w/site/a/main.asp", Inc("file", "gone.inc") + Inc("virtual", "/lib/none.inc")));

        var set = Run(fs, "/w/site/a/main.asp");

        Assert.Empty(set.Files);
        Assert.Equal(["a/gone.inc", "lib/none.inc"], set.Unresolved);
    }

    [Fact]
    public void MaxFiles_Truncated()
    {
        var fs = Fs(("/w/site/a.asp", Inc("file", "1.inc") + Inc("file", "2.inc") + Inc("file", "3.inc")),
            ("/w/site/1.inc", "x"), ("/w/site/2.inc", "x"), ("/w/site/3.inc", "x"));

        var set = Run(fs, "/w/site/a.asp", maxFiles: 2);

        Assert.Equal(2, set.Files.Count);
        Assert.True(set.Truncated);
    }

    [Fact]
    public void Cp949Include_Decoded()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var fs = Fs(("/w/site/a.asp", Inc("file", "k.inc")));
        fs.AddFile("/w/site/k.inc", new MockFileData(Encoding.GetEncoding(949).GetBytes("<% ' 한글\r\nx = 1 %>")));

        var set = Run(fs, "/w/site/a.asp");

        Assert.Single(set.Files);
        Assert.Contains("한글", set.Files[0].Page.LineText(1));
    }
}
