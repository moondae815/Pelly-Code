using System.IO.Abstractions.TestingHelpers;
using System.Text;
using PellyCoder.Core.Impact;
using PellyCoder.Core.Models;
using PellyCoder.Core.Specs;
using PellyCoder.Core.Web;

namespace PellyCoder.Core.Tests.Impact;

public sealed class JsImpactFinderTests
{
    private static readonly string Root = OperatingSystem.IsWindows() ? @"C:\site" : "/site";

    private const string OrderJs =
        "function fnSearch(f) {\n" +   // 1
        "  return f;\n" +               // 2
        "}\n" +                         // 3
        "function again() {\n" +        // 4
        "  fnSearch(1);\n" +            // 5
        "}\n";                          // 6

    private const string ListPage =
        "<script src=\"../js/order.js\"></script>\n" +            // 1
        "<input type=\"button\" onclick=\"fnSearch(this)\">\n" +  // 2
        "<script>\n" +                                            // 3
        "function goPage(n) {\n" +                                // 4
        "  fnSearch(n);\n" +                                      // 5
        "  setTimeout(fnSearch, 10);\n" +                         // 6
        "}\n" +                                                   // 7
        "</script>\n";                                            // 8

    private readonly MockFileSystem _fs = new();

    private void Add(string rel, string text) => _fs.AddFile(Root + "/" + rel, new MockFileData(Encoding.UTF8.GetBytes(text)));

    private IReadOnlyList<JsImpactHit> Find(string seedPath, string function, params SpecTask[] tasks)
    {
        var index = WebSiteIndex.Load(_fs, Root, new WebFileFilter(Root, []), new WebTextCodec("utf-8"), "/ws/.pelly/cache/i.json");
        string? Read(string rel)
        {
            var full = Root + "/" + rel;
            return _fs.File.Exists(full) ? _fs.File.ReadAllText(full) : null;
        }
        return JsImpactFinder.Find([new JsImpactSeed("T1", seedPath, function, 1)], tasks, index, Read);
    }

    private static SpecTask Task(string id, TaskKind kind, string path, string? symbol, TextSpanInfo? lines = null) =>
        new SpecTask(id, kind, path, symbol, "x", [], null, false, null, null, 1) { Lines = lines };

    public JsImpactFinderTests()
    {
        Add("js/order.js", OrderJs);
        Add("admin/list.asp", ListPage);
    }

    [Fact]
    public void Seed_FindsJsPageAndAttributeCalls()
    {
        var hits = Find("js/order.js", "fnSearch");
        Assert.Contains(hits, h => h.RelativePath == "js/order.js" && h.Call.Line == 5 && h.Call.Source == JsCallSource.Js);
        Assert.Contains(hits, h => h.RelativePath == "admin/list.asp" && h.Call.Line == 2 && h.Call.Source == JsCallSource.Attribute);
        Assert.Contains(hits, h => h.RelativePath == "admin/list.asp" && h.Call.Line == 5 && h.Call.Source == JsCallSource.PageScript);
        Assert.All(hits, h => Assert.False(h.Covered));
    }

    [Fact]
    public void References_Included() =>
        Assert.Contains(Find("js/order.js", "fnSearch"), h => h.RelativePath == "admin/list.asp" && h.Call.Line == 6 && h.Call.Reference);

    [Fact]
    public void Qualified_Seed_MatchesOnlyQualified()
    {
        Add("js/common.js", "var Common = { fnX: function (a) {} };\nCommon.fnX(1);\nother.fnX(2);\nfnX(3);\n");
        var hits = Find("js/common.js", "Common.fnX");
        Assert.Equal([2], hits.Select(h => h.Call.Line));
    }

    [Fact]
    public void PageDeclaresSameName_Excluded()
    {
        Add("admin/own.asp", "<script>\nfunction fnSearch(a) {}\nfnSearch(1);\n</script>\n");
        Assert.DoesNotContain(Find("js/order.js", "fnSearch"), h => h.RelativePath == "admin/own.asp");
    }

    [Fact]
    public void PageWithoutScriptSrc_Flagged()
    {
        Add("admin/bare.asp", "<input onclick=\"fnSearch(1)\">\n");
        var hits = Find("js/order.js", "fnSearch");
        Assert.True(hits.Single(h => h.RelativePath == "admin/bare.asp").PageDoesNotLoadSeedFile);
        Assert.All(hits.Where(h => h.RelativePath == "admin/list.asp"), h => Assert.False(h.PageDoesNotLoadSeedFile));
    }

    [Fact]
    public void IncludeScriptSrc_NotFlagged()
    {
        Add("inc/head.inc", "<script src=\"/js/order.js\"></script>\n");
        Add("admin/inc.asp", "<!--#include virtual=\"/inc/head.inc\"-->\n<input onclick=\"fnSearch(1)\">\n");
        Assert.False(Find("js/order.js", "fnSearch").Single(h => h.RelativePath == "admin/inc.asp").PageDoesNotLoadSeedFile);
    }

    [Fact]
    public void Covered_ByFunctionTask_ByLinesTask_ByAspProc()
    {
        Add("admin/proc.asp", "<%\nSub Draw()\n%>\n<input onclick=\"fnSearch(1)\">\n<%\nEnd Sub\n%>\n");
        var hits = Find("js/order.js", "fnSearch",
            Task("T2", TaskKind.Js, "js/order.js", "again"),
            Task("T3", TaskKind.Js, "admin/list.asp", null, new TextSpanInfo(4, 7)),
            Task("T4", TaskKind.Asp, "admin/proc.asp", "Draw"));
        Assert.Equal(["T2"], hits.Single(h => h.RelativePath == "js/order.js").CoveredBy);
        Assert.Equal(["T3"], hits.Single(h => h.RelativePath == "admin/list.asp" && h.Call.Line == 5).CoveredBy);
        Assert.Equal(["T4"], hits.Single(h => h.RelativePath == "admin/proc.asp").CoveredBy);
    }

    [Fact]
    public void Attribute_CoveredOnlyByAsp()
    {
        var hits = Find("js/order.js", "fnSearch",
            Task("T2", TaskKind.Js, "admin/list.asp", null, new TextSpanInfo(1, 3)),
            Task("T3", TaskKind.Asp, "admin/list.asp", null, new TextSpanInfo(2, 2)));
        Assert.Equal(["T3"], hits.Single(h => h.Call.Source == JsCallSource.Attribute).CoveredBy);
    }
}
