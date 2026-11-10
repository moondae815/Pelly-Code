using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Config;
using PellyCoder.Core.Specs;

namespace PellyCoder.Core.Tests.Specs;

public sealed class SpecCheckerWebTests
{
    private const string OrderJs =
        "function fnSearch(f) { return 1; }\n" +
        "function twice() {}\n" +
        "function twice() {}\n";

    private const string ListPage =
        "<%@ Language=\"VBScript\" %>\n" + // 1
        "<%\nSub Search()\nEnd Sub\n%>\n" + // 2-5
        "<style>\n" +                      // 6
        ".grid td { padding: 2px; }\n" +   // 7
        "</style>\n" +                     // 8
        "<script>\n" +                     // 9
        "function goPage(n) {\n" +         // 10
        "  fnSearch(document.f, n);\n" +   // 11
        "}\n" +                            // 12
        "</script>\n";                     // 13

    private static MockFileSystem Fs(string tasks)
    {
        var fs = new MockFileSystem();
        fs.AddFile("/ws/App.sln", "");
        fs.AddFile("/ws/web/js/order.js", OrderJs);
        fs.AddFile("/ws/web/css/admin.css", ".search-box { width: 100px; }\n#main { margin: 0; }\n");
        fs.AddFile("/ws/web/admin/list.asp", ListPage);
        fs.AddFile("/ws/web/lib/jquery-1.8.3.min.js", "/*! jQuery v1.8.3 jquery.com */\n(function(){})();\n");
        fs.AddFile("/ws/web/js/bad.js", new MockFileData([0xFF, 0xFE, 0x61, 0x00]));
        fs.AddFile("/ws/changes/x.md", "---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n- AC1: 하나\n\n## 작업\n" + tasks);
        return fs;
    }

    private static readonly AspOptions Asp = new() { SiteRoot = "web", Encoding = "utf-8" };

    private static SpecCheckResult Check(MockFileSystem fs) => SpecChecker.Check(fs, "/ws/changes/x.md", 160, Asp);

    private static string[] Codes(SpecCheckResult r) => r.Issues.Select(i => i.Code).ToArray();

    private static SpecIssue Only(string tasks) => Assert.Single(Check(Fs(tasks)).Issues);

    [Fact]
    public void Clean_JsAndCssTasks()
    {
        var r = Check(Fs(
            "- [ ] T1 js web/js/order.js#fnSearch 매개변수를 더한다\n" +
            "- [ ] T2 js web/admin/list.asp#goPage 넘긴다\n" +
            "- [ ] T3 css web/css/admin.css#.search-box 넓힌다\n" +
            "- [ ] T4 css web/css/admin.css##main 줄인다\n" +
            "- [ ] T5 css web/admin/list.asp#.grid 줄인다\n"));
        Assert.Contains(r.Issues, i => i.Code == "SymbolNotFound" && i.Message.Contains("T5"));
        Assert.Single(r.Issues);
    }

    [Fact]
    public void KindMismatch_JsOnCss()
    {
        var issue = Only("- [ ] T1 js web/css/admin.css#x 바꾼다\n");
        Assert.Equal("KindMismatch", issue.Code);
        Assert.Contains("js 작업의 파일이 .js, .asp, .inc, .htm, .html 중 하나가 아닙니다", issue.Message);
    }

    [Fact]
    public void KindMismatch_CssOnJs() =>
        Assert.Equal("KindMismatch", Only("- [ ] T1 css web/js/order.js#.x 바꾼다\n").Code);

    [Fact]
    public void WebTargetRequired_Js_And_Css()
    {
        var js = Only("- [ ] T1 js web/js/order.js 바꾼다\n");
        Assert.Equal("WebTargetRequired", js.Code);
        Assert.Equal("작업 T1: js 작업에는 대상(#함수 또는 #L시작-끝)이 필요합니다", js.Message);
        var css = Only("- [ ] T1 css web/css/admin.css 바꾼다\n");
        Assert.Equal("작업 T1: css 작업에는 대상(#선택자 또는 #L시작-끝)이 필요합니다", css.Message);
    }

    [Fact]
    public void SymbolNotFound_Function() =>
        Assert.Equal("SymbolNotFound", Only("- [ ] T1 js web/js/order.js#nope 바꾼다\n").Code);

    [Fact]
    public void SymbolAmbiguous_Function() =>
        Assert.Equal("SymbolAmbiguous", Only("- [ ] T1 js web/js/order.js#twice 바꾼다\n").Code);

    [Fact]
    public void SymbolNotFound_Selector() =>
        Assert.Equal("SymbolNotFound", Only("- [ ] T1 css web/css/admin.css#.nope 바꾼다\n").Code);

    [Fact]
    public void LinesOutOfFile_Js() =>
        Assert.Equal("LinesOutOfFile", Only("- [ ] T1 js web/js/order.js#L2-9 바꾼다\n").Code);

    [Fact]
    public void LinesOutsideBlock_PageHtml()
    {
        var js = Only("- [ ] T1 js web/admin/list.asp#L8-11 바꾼다\n");
        Assert.Equal("LinesOutsideBlock", js.Code);
        Assert.Equal("작업 T1: 줄 범위가 <script> 블록 하나 안에 있지 않습니다. asp 작업으로 쓰십시오", js.Message);
        var css = Only("- [ ] T1 css web/admin/list.asp#L10-11 바꾼다\n");
        Assert.Equal("작업 T1: 줄 범위가 <style> 블록 하나 안에 있지 않습니다. asp 작업으로 쓰십시오", css.Message);
    }

    [Fact]
    public void LinesInsideBlock_Clean() =>
        Assert.Empty(Check(Fs("- [ ] T1 js web/admin/list.asp#L10-12 바꾼다\n")).Issues);

    [Fact]
    public void WebVendorFile()
    {
        var issue = Only("- [ ] T1 js web/lib/jquery-1.8.3.min.js#L1-1 바꾼다\n");
        Assert.Equal("WebVendorFile", issue.Code);
        Assert.StartsWith("작업 T1: 고칠 수 없는 파일입니다", issue.Message);
    }

    [Fact]
    public void WebEncoding() =>
        Assert.Equal("WebEncoding", Only("- [ ] T1 js web/js/bad.js#L1-1 바꾼다\n").Code);

    [Fact]
    public void LinesAfterEdit_AcrossKinds()
    {
        var issue = Only("- [ ] T1 asp web/admin/list.asp#Search 바꾼다\n- [ ] T2 js web/admin/list.asp#L10-12 바꾼다\n");
        Assert.Equal("LinesAfterEdit", issue.Code);
        Assert.Equal("T2의 줄 범위는 같은 파일을 먼저 고치는 T1 뒤에 어긋날 수 있습니다. 프로시저 대상(#이름)으로 쓰거나 작업을 하나로 합치십시오",
            issue.Message);
    }

    [Fact]
    public void LinesAfterEdit_AspStillUsesOldCode() =>
        Assert.Equal("AspLinesAfterEdit",
            Only("- [ ] T1 js web/admin/list.asp#goPage 바꾼다\n- [ ] T2 asp web/admin/list.asp#L2-5 바꾼다\n").Code);

    [Fact]
    public void FirstTaskLines_Allowed() =>
        Assert.Empty(Check(Fs("- [ ] T1 js web/admin/list.asp#L10-12 바꾼다\n- [ ] T2 asp web/admin/list.asp#Search 바꾼다\n")).Issues);
}
