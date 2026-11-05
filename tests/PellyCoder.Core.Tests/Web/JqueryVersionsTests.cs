using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class JqueryVersionsTests
{
    private static MockFileSystem Fs(params (string path, string text)[] files)
    {
        var fs = new MockFileSystem();
        foreach (var (p, t) in files) fs.AddFile(p, new MockFileData(t));
        return fs;
    }

    [Fact]
    public void Header_Version()
    {
        var fs = Fs(("/site/js/jquery.js", "/*!\n * jQuery JavaScript Library v1.8.3\n */\n"));
        var r = JqueryVersions.Find(fs, "/site");
        Assert.Single(r);
        Assert.Equal(new Version(1, 8, 3), r[0].Version);
        Assert.Equal("js/jquery.js", r[0].RelativePath);
    }

    [Fact]
    public void MinHeader_Version()
    {
        var fs = Fs(("/site/js/jquery.min.js", "/*! jQuery v1.8.3 | (c) 2005, 2012 jQuery Foundation */\n(function(){})();"));
        Assert.Equal(new Version(1, 8, 3), JqueryVersions.Find(fs, "/site").Single().Version);
    }

    [Fact]
    public void FileNameOnly_Version()
    {
        var fs = Fs(("/site/js/jquery-1.4.2.min.js", "(function(){})();"));
        Assert.Equal(new Version(1, 4, 2), JqueryVersions.Find(fs, "/site").Single().Version);
        var fs2 = Fs(("/site/js/jquery-1.7.js", "x"));
        Assert.Equal(new Version(1, 7), JqueryVersions.Find(fs2, "/site").Single().Version);
    }

    [Fact]
    public void PluginHeader_NotMatched()
    {
        var fs = Fs(("/site/js/jquery.validate.js", "/*! jQuery Validation Plugin v1.19 */\n"));
        Assert.Empty(JqueryVersions.Find(fs, "/site"));
    }

    [Fact]
    public void Several_BaselineLowest()
    {
        var fs = Fs(("/site/a/jquery-1.8.3.js", "x"), ("/site/b/jquery-1.4.2.js", "x"), ("/site/c/jquery-3.6.0.js", "x"));
        var found = JqueryVersions.Find(fs, "/site");
        Assert.Equal(3, found.Count);
        Assert.Equal(new Version(1, 4, 2), JqueryVersions.Baseline(found, null));
        Assert.Null(JqueryVersions.Baseline(Array.Empty<JqueryFound>(), null));
    }

    [Fact]
    public void Configured_Wins()
    {
        var fs = Fs(("/site/a/jquery-1.8.3.js", "x"));
        var found = JqueryVersions.Find(fs, "/site");
        Assert.Equal(new Version(2, 0), JqueryVersions.Baseline(found, "2.0"));
        Assert.Equal(new Version(1, 8, 3), JqueryVersions.Baseline(found, "junk"));
    }
}
