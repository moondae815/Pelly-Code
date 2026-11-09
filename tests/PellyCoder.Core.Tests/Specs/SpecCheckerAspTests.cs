using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Config;
using PellyCoder.Core.Specs;

namespace PellyCoder.Core.Tests.Specs;

public sealed class SpecCheckerAspTests
{
    private const string Page =
        "<%\nFunction GetOne()\n  GetOne = 1\nEnd Function\n\nSub DoIt()\nEnd Sub\n%>\n<html></html>\n";

    private const string DupPage =
        "<%\nSub Twice()\nEnd Sub\nSub Twice()\nEnd Sub\n%>\n";

    private static MockFileSystem Fs(string tasks)
    {
        var fs = new MockFileSystem();
        fs.AddFile("/ws/App.sln", "");
        fs.AddFile("/ws/web/a.asp", Page);
        fs.AddFile("/ws/web/dup.asp", DupPage);
        fs.AddFile("/ws/web/a.aspx", Page);
        fs.AddFile("/ws/web/a.txt", Page);
        fs.AddFile("/ws/changes/x.md", "---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n- AC1: 하나\n\n## 작업\n" + tasks);
        return fs;
    }

    private static SpecCheckResult Check(MockFileSystem fs, AspOptions? asp = null) =>
        SpecChecker.Check(fs, "/ws/changes/x.md", 160, asp);

    private static string[] Codes(SpecCheckResult r) => r.Issues.Select(i => i.Code).ToArray();

    [Fact]
    public void Valid_asp_task_has_no_issues()
    {
        var r = Check(Fs("- [ ] T1 asp web/a.asp#GetOne 값을 바꾼다\n- [ ] T2 asp web/dup.asp#L2-4 줄을 바꾼다\n"));
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Asp_extension_not_configured_is_KindMismatch()
    {
        var r = Check(Fs("- [ ] T1 asp web/a.txt#GetOne 바꾼다\n"));
        var issue = Assert.Single(r.Issues);
        Assert.Equal("KindMismatch", issue.Code);
        Assert.Contains(".asp, .inc", issue.Message);
    }

    [Fact]
    public void Asp_extensions_setting_is_respected()
    {
        var fs = Fs("- [ ] T1 asp web/a.aspx#GetOne 바꾼다\n");
        Assert.Equal(["KindMismatch"], Codes(Check(fs)));
        Assert.Empty(Check(fs, new AspOptions { Extensions = [".aspx"] }).Issues);
    }

    [Fact]
    public void Asp_without_target_is_AspTargetRequired() =>
        Assert.Equal(["AspTargetRequired"], Codes(Check(Fs("- [ ] T1 asp web/a.asp 바꾼다\n"))));

    [Fact]
    public void Asp_procedure_missing_is_SymbolNotFound() =>
        Assert.Equal(["SymbolNotFound"], Codes(Check(Fs("- [ ] T1 asp web/a.asp#Nope 바꾼다\n"))));

    [Fact]
    public void Asp_procedure_twice_is_SymbolAmbiguous() =>
        Assert.Equal(["SymbolAmbiguous"], Codes(Check(Fs("- [ ] T1 asp web/dup.asp#Twice 바꾼다\n"))));

    [Fact]
    public void Asp_lines_past_end_is_LinesOutOfFile()
    {
        var r = Check(Fs("- [ ] T1 asp web/a.asp#L5-500 바꾼다\n"));
        var issue = Assert.Single(r.Issues);
        Assert.Equal("LinesOutOfFile", issue.Code);
        Assert.Contains("L5-500", issue.Message);
    }

    [Fact]
    public void Asp_second_lines_task_on_same_file_is_AspLinesAfterEdit()
    {
        var r = Check(Fs("- [ ] T1 asp web/a.asp#L2-3 바꾼다\n- [ ] T2 asp web\\a.asp#L6-7 바꾼다\n"));
        var issue = Assert.Single(r.Issues);
        Assert.Equal("AspLinesAfterEdit", issue.Code);
        Assert.Equal(SpecIssueSeverity.Error, issue.Severity);
        Assert.True(r.HasErrors);
        Assert.Contains("T2", issue.Message);
        Assert.Contains("T1", issue.Message);
    }

    [Fact]
    public void Asp_first_lines_task_then_procedure_task_is_ok() =>
        Assert.Empty(Check(Fs("- [ ] T1 asp web/a.asp#L2-3 바꾼다\n- [ ] T2 asp web/a.asp#DoIt 바꾼다\n")).Issues);

    [Fact]
    public void Asp_unreadable_bytes_is_AspEncoding()
    {
        var fs = Fs("- [ ] T1 asp web/bad.asp#L1-1 바꾼다\n");
        fs.AddFile("/ws/web/bad.asp", new MockFileData([.. "<%@ CodePage=65001 %>\n"u8.ToArray(), 0xFF, 0xFE, 0xFD, (byte)'\n']));
        Assert.Equal(["AspEncoding"], Codes(Check(fs)));
    }

    [Fact]
    public void Done_asp_task_target_not_checked() =>
        Assert.Empty(Check(Fs("- [x] T1 asp web/a.asp#Gone 바꾼다 <!-- run: R-1 -->\n- [x] T2 asp web/a.asp 바꾼다 <!-- run: R-2 -->\n")).Issues);
}
