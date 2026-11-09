using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Config;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Web;

public class WebSiteIndexSpCallTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\site" : "/site";
    private const string Cache = "/ws/.pelly/cache/web-site-index.json";

    private static MockFileSystem Fs(params (string Path, string Text)[] files)
    {
        var fs = new MockFileSystem();
        foreach (var (p, t) in files) fs.AddFile(p, new MockFileData(Encoding.UTF8.GetBytes(t)));
        return fs;
    }

    private static WebSiteIndexResult Load(MockFileSystem fs, IReadOnlyList<AspSpCallFunction>? wrappers = null) =>
        WebSiteIndex.Load(fs, Root, new WebFileFilter(Root, []), new WebTextCodec("cp949"), Cache,
            spCallFunctions: wrappers);

    [Fact]
    public void Index_skips_backup_and_build_folders()
    {
        var fs = Fs(
            ("/site/.pelly/backup/x/a.asp", "<%\nrs.Open \"EXEC usp_x 1\", conn\n%>"),
            ("/site/bin/b.asp", "<%\nrs.Open \"EXEC usp_x 1\", conn\n%>"),
            ("/site/node_modules/m/c.asp", "<%\nrs.Open \"EXEC usp_x 1\", conn\n%>"),
            ("/site/a.asp", "<%\nrs.Open \"EXEC usp_x 1\", conn\n%>"));
        var result = Load(fs);
        Assert.Equal("a.asp", Assert.Single(result.SpCalls).RelativePath);
    }

    [Fact]
    public void Index_lists_direct_exec_call_with_argument_count()
    {
        var fs = Fs(("/site/a.asp", "<%\nrs.Open \"EXEC usp_x 1, 2\", conn\n%>"));
        var call = Assert.Single(Load(fs).SpCalls);
        Assert.Equal("a.asp", call.RelativePath);
        Assert.Equal(2, call.Line);
        Assert.Equal("usp_x", call.Object.Name);
        Assert.Equal(2, call.Arguments);
        Assert.Null(call.Procedure);
    }

    [Fact]
    public void Index_lists_call_syntax()
    {
        var fs = Fs(("/site/a.asp", "<%\ncmd.CommandText = \"{call dbo.usp_x(?, ?)}\"\n%>"));
        var call = Assert.Single(Load(fs).SpCalls);
        Assert.Equal("dbo", call.Object.Schema);
        Assert.Equal("usp_x", call.Object.Name);
        Assert.Equal(2, call.Arguments);
    }

    [Fact]
    public void Index_lists_wrapper_function_call()
    {
        var fs = Fs(("/site/a.asp", "<%\nExecSP \"usp_w\", 1, 2\n%>"));
        Assert.Empty(Load(fs).SpCalls);
        var call = Assert.Single(Load(fs, [new AspSpCallFunction { Name = "ExecSP", Argument = 1 }]).SpCalls);
        Assert.Equal("usp_w", call.Object.Name);
    }

    [Fact]
    public void Index_records_enclosing_procedure()
    {
        var fs = Fs(("/site/a.asp",
            "<%\nFunction Load()\n  rs.Open \"EXEC usp_in\", conn\nEnd Function\nrs.Open \"EXEC usp_out\", conn\n%>"));
        var calls = Load(fs).SpCalls;
        Assert.Equal("Load", calls.Single(c => c.Object.Name == "usp_in").Procedure);
        Assert.Null(calls.Single(c => c.Object.Name == "usp_out").Procedure);
    }

    [Fact]
    public void Index_lists_calls_in_inc_files()
    {
        var fs = Fs(("/site/lib/common.inc", "<%\nrs.Open \"EXEC usp_inc\", conn\n%>"));
        var call = Assert.Single(Load(fs).SpCalls);
        Assert.Equal("lib/common.inc", call.RelativePath);
    }

    [Fact]
    public void Index_cache_reused_and_refreshed_on_change()
    {
        var fs = Fs(("/site/a.asp", "<%\nrs.Open \"EXEC usp_one\", conn\n%>"));
        Assert.Equal("usp_one", Assert.Single(Load(fs).SpCalls).Object.Name);
        Assert.Contains("usp_one", fs.File.ReadAllText(Cache));
        Assert.Equal("usp_one", Assert.Single(Load(fs).SpCalls).Object.Name);
        fs.File.WriteAllText("/site/a.asp", "<%\nrs.Open \"EXEC usp_two 1\", conn\n%>");
        var call = Assert.Single(Load(fs).SpCalls);
        Assert.Equal("usp_two", call.Object.Name);
        Assert.Equal(1, call.Arguments);
    }

    [Fact]
    public void Html_files_have_no_sp_calls()
    {
        var fs = Fs(("/site/a.htm", "<%\nrs.Open \"EXEC usp_x\", conn\n%>"));
        Assert.Empty(Load(fs).SpCalls);
    }
}
