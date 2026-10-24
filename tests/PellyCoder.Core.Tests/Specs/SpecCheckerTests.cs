using System.IO.Abstractions.TestingHelpers;
using PellyCoder.Core.Specs;

namespace PellyCoder.Core.Tests.Specs;

public sealed class SpecCheckerTests
{
    private const string Cs =
        "namespace A;\npublic class OrderService\n{\n    public void Cancel() { throw new InvalidOperationException(); }\n    public void Dup() { }\n    public void Dup(int x) { }\n}\n" +
        "public enum OrderStatus { Shipped }\n";

    private const string Sql =
        "CREATE PROCEDURE dbo.usp_One AS SELECT 1;\n";

    private const string TwoSql =
        "CREATE PROCEDURE dbo.usp_One AS SELECT 1;\nGO\nCREATE PROCEDURE dbo.usp_Two AS SELECT 2;\n";

    private static MockFileSystem Fs(string tasks, string criteria = "- AC1: 하나\n")
    {
        var fs = new MockFileSystem();
        fs.AddFile("/ws/App.sln", "");
        fs.AddFile("/ws/src/App/App.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        fs.AddFile("/ws/src/App/OrderService.cs", Cs);
        fs.AddFile("/ws/src/Tests/OrderTests.cs", "public class OrderTests { public void Cancel_works() { } }\n");
        fs.AddFile("/ws/db/one.sql", Sql);
        fs.AddFile("/ws/db/two.sql", TwoSql);
        fs.AddFile("/ws/changes/x.md", "---\nid: x\nstatus: draft\n---\n# 제목\n\n## 수용 기준\n" + criteria + "\n## 작업\n" + tasks);
        return fs;
    }

    private static SpecCheckResult Check(MockFileSystem fs) => SpecChecker.Check(fs, "/ws/changes/x.md", 160);

    private static string[] Codes(SpecCheckResult r) => r.Issues.Select(i => i.Code).ToArray();

    [Fact]
    public void Clean_spec_has_no_issues()
    {
        var fs = Fs("- [ ] T1 cs src/App/OrderService.cs#OrderService.Cancel InvalidOperationException을 던지게 한다 (AC1)\n" +
                    "- [ ] T2 sql db/one.sql 한 줄 추가\n");
        var r = Check(fs);
        Assert.Empty(r.Issues);
        Assert.False(r.HasErrors);
        // MockFileSystem roots "/ws" at the current drive on Windows (C:\ws).
        Assert.Equal(fs.Path.GetFullPath("/ws"), r.Workspace.Root);
    }

    [Fact]
    public void Missing_file_symbol_ambiguous_symbol_and_sql_object_are_errors()
    {
        var r = Check(Fs(
            "- [ ] T1 cs src/App/Nope.cs#X 요청\n" +
            "- [ ] T2 cs src/App/OrderService.cs#Missing 요청\n" +
            "- [ ] T3 cs src/App/OrderService.cs#Dup 요청\n" +
            "- [ ] T4 sql db/one.sql#dbo.usp_Nope 요청\n" +
            "- [ ] T5 sql db/two.sql 요청\n" +
            "- [ ] T6 cs src/App/OrderService.cs 요청\n" +
            "- [ ] T7 cs db/one.sql#X 요청\n" +
            "- [ ] T8 sql src/App/OrderService.cs 요청\n"));
        Assert.True(r.HasErrors);
        Assert.Equal(["FileNotFound", "SymbolNotFound", "SymbolAmbiguous", "SqlObjectNotFound", "SqlObjectAmbiguous", "SymbolRequired", "KindMismatch", "KindMismatch"],
            r.Issues.Where(i => i.Severity == SpecIssueSeverity.Error).Select(i => i.Code).ToArray());
    }

    [Fact]
    public void Cs_task_without_symbol_is_an_error()
    {
        var r = Check(Fs("- [ ] T1 cs src/App/OrderService.cs 요청\n"));
        Assert.Equal(["SymbolRequired"], Codes(r));
        Assert.True(r.HasErrors);
    }

    [Fact]
    public void Group_is_unsupported_in_v2_1a()
    {
        var r = Check(Fs("- [ ] T1 cs src/App/OrderService.cs#Cancel 요청 [G1]\n"));
        var issue = Assert.Single(r.Issues);
        Assert.Equal("GroupUnsupported", issue.Code);
        Assert.Equal(SpecIssueSeverity.Error, issue.Severity);
        Assert.Contains("V2-1b", issue.Message);
    }

    [Fact]
    public void Path_outside_workspace_is_an_error()
    {
        var r = Check(Fs("- [ ] T1 cs ../x.cs#M 요청\n"));
        Assert.Equal(["OutsideWorkspace"], Codes(r));
    }

    [Fact]
    public void Unknown_identifier_in_request_is_a_warning()
    {
        var r = Check(Fs("- [ ] T1 sql db/one.sql CancelTime 컬럼 추가\n"));
        var issue = Assert.Single(r.Issues);
        Assert.Equal("UnknownIdentifier", issue.Code);
        Assert.Equal(SpecIssueSeverity.Warning, issue.Severity);
        Assert.Contains("CancelTime", issue.Message);
        Assert.False(r.HasErrors);
    }

    [Fact]
    public void Known_identifiers_do_not_warn()
    {
        var r = Check(Fs("- [ ] T1 cs src/App/OrderService.cs#Cancel OrderStatus가 InvalidOperationException이면 처리\n"));
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Ac_test_missing_in_workspace_is_a_warning()
    {
        var ok = Check(Fs("- [ ] T1 sql db/one.sql 요청 (AC1)\n", "- AC1: 하나\n  test: OrderTests.Cancel_works\n"));
        Assert.Empty(ok.Issues);
        var bare = Check(Fs("- [ ] T1 sql db/one.sql 요청 (AC1)\n", "- AC1: 하나\n  test: Cancel_works\n"));
        Assert.Empty(bare.Issues);
        var bad = Check(Fs("- [ ] T1 sql db/one.sql 요청 (AC1)\n", "- AC1: 하나\n  test: OrderTests.Nope\n"));
        var issue = Assert.Single(bad.Issues);
        Assert.Equal("TestNotFound", issue.Code);
        Assert.Equal(SpecIssueSeverity.Warning, issue.Severity);
        Assert.False(bad.HasErrors);
    }

    [Fact]
    public void Parser_errors_are_carried_over()
    {
        var r = Check(Fs("- [ ] T1 cs src/App/OrderService.cs#Cancel 요청\n- [ ] T1 sql db/one.sql 요청\n"));
        Assert.Contains("DuplicateId", Codes(r));
    }
}
