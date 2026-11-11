using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Config;
using PellyCoder.Core.Impact;

namespace PellyCoder.Core.Tests.Impact;

public sealed class WebSiteAccessTests
{
    [Fact]
    public void CachedReader_ReadsEachFileOnce()
    {
        var root = OperatingSystem.IsWindows() ? @"C:\w" : "/w";
        var fs = new MockFileSystem();
        fs.AddFile(root + "/a.js", new MockFileData(Encoding.UTF8.GetBytes("one\n")));
        var read = new WebSiteAccess(fs, new AspOptions(), new WebOptions(), root).CachedReader();
        Assert.Equal("one\n", read("a.js"));
        fs.File.WriteAllText(root + "/a.js", "two\n");
        Assert.Equal("one\n", read("a.js"));
        Assert.Null(read("missing.js"));
    }
}
