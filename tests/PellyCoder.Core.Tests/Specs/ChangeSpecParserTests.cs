using System.Text;
using PellyCoder.Core.Specs;

namespace PellyCoder.Core.Tests.Specs;

public sealed class ChangeSpecParserTests
{
    private const string Front = "---\nid: x\nstatus: draft\n---\n# 제목\n\n";

    private static string Spec(string tasks, string criteria = "- AC1: 하나\n- AC2: 둘\n") =>
        Front + "## 수용 기준\n" + criteria + "\n## 작업\n" + tasks;

    private const string PrdExample =
        "---\n" +
        "id: order-cancel\n" +
        "status: draft            # draft | in-progress | done\n" +
        "---\n" +
        "# 주문 취소 시 배송 상태 확인\n" +
        "\n" +
        "## 목표\n" +
        "이미 배송된 주문은 취소할 수 없게 한다.\n" +
        "\n" +
        "## 수용 기준\n" +
        "- AC1: 배송 완료 주문을 취소하면 InvalidOperationException 발생\n" +
        "  test: OrderServiceTests.Cancel_WhenShipped_Throws\n" +
        "- AC2: 취소 SP는 배송 완료 주문의 상태를 바꾸지 않는다\n" +
        "\n" +
        "## 작업\n" +
        "- [ ] T1 cs  src/Orders/OrderService.cs#Cancel     배송 완료 상태면 InvalidOperationException 던지기 (AC1)\n" +
        "- [ ] T2 sql db/sp/usp_CancelOrder.sql             WHERE 조건에 배송 완료 제외 추가 (AC2)\n";

    [Fact]
    public void Parses_prd_example()
    {
        var spec = ChangeSpecParser.Parse(PrdExample);

        Assert.Equal("order-cancel", spec.Id);
        Assert.Equal(SpecStatus.Draft, spec.Status);
        Assert.Equal("주문 취소 시 배송 상태 확인", spec.Title);
        Assert.Equal(2, spec.Criteria.Count);
        Assert.Equal("OrderServiceTests.Cancel_WhenShipped_Throws", spec.Criteria[0].Test);
        Assert.Null(spec.Criteria[1].Test);
        Assert.Equal(2, spec.Tasks.Count);

        var t1 = spec.Tasks[0];
        Assert.Equal("T1", t1.Id);
        Assert.Equal(TaskKind.CSharp, t1.Kind);
        Assert.Equal("src/Orders/OrderService.cs", t1.Path);
        Assert.Equal("Cancel", t1.Symbol);
        Assert.Equal(new[] { "AC1" }, t1.AcRefs);
        Assert.Equal("배송 완료 상태면 InvalidOperationException 던지기", t1.Request);
        Assert.False(t1.Done);

        var t2 = spec.Tasks[1];
        Assert.Equal(TaskKind.Sql, t2.Kind);
        Assert.Equal("db/sp/usp_CancelOrder.sql", t2.Path);
        Assert.Null(t2.Symbol);
        Assert.Equal(new[] { "AC2" }, t2.AcRefs);
        Assert.Equal(16, t1.Line);

        Assert.Empty(spec.Issues);
    }

    [Fact]
    public void Parses_backslash_paths_and_parentheses_inside_request()
    {
        var spec = ChangeSpecParser.Parse(Spec("- [ ] T1 cs Src\\A.cs#B.M 값 (괄호) 처리 #주의 (AC1)\n"));

        var task = Assert.Single(spec.Tasks);
        Assert.Equal("Src\\A.cs", task.Path);
        Assert.Equal("B.M", task.Symbol);
        Assert.Equal("값 (괄호) 처리 #주의", task.Request);
        Assert.Equal(new[] { "AC1" }, task.AcRefs);
        Assert.Empty(spec.Issues);
    }

    [Fact]
    public void Reads_done_run_comment_and_failure_comment()
    {
        var spec = ChangeSpecParser.Parse(Spec(
            "- [x] T1 cs A.cs#M 요청 <!-- run: 20261024-101010-ab12 -->\n" +
            "- [ ] T2 cs A.cs#N 요청 <!-- failed: 20261024-101011-cd34 VerifyFailed -->\n"));

        Assert.True(spec.Tasks[0].Done);
        Assert.Equal("20261024-101010-ab12", spec.Tasks[0].RunId);
        Assert.Null(spec.Tasks[0].Failure);
        Assert.Equal("요청", spec.Tasks[0].Request);

        Assert.False(spec.Tasks[1].Done);
        Assert.Equal("20261024-101011-cd34", spec.Tasks[1].RunId);
        Assert.Equal("VerifyFailed", spec.Tasks[1].Failure);
        Assert.Empty(spec.Issues);
    }

    [Fact]
    public void Group_with_trailing_punctuation_is_read()
    {
        var spec = ChangeSpecParser.Parse(Spec("- [ ] T1 cs A.cs#M 요청 [G1].\n"));

        Assert.Equal("G1", Assert.Single(spec.Tasks).Group);
        Assert.Equal("요청", spec.Tasks[0].Request);
    }

    [Theory]
    [InlineData("  - [ ] T1 cs A.cs#M x")]
    [InlineData("* [ ] T1 cs A.cs#M x")]
    [InlineData("1. [ ] T1 cs A.cs#M x")]
    [InlineData("- [X] T1 cs A.cs#M x")]
    [InlineData("- [ ] T1 vb A.cs x")]
    public void Task_shaped_but_off_grammar_lines_are_errors(string line)
    {
        var spec = ChangeSpecParser.Parse(Spec(line + "\n"));

        Assert.Empty(spec.Tasks);
        var issue = Assert.Single(spec.Issues, i => i.Code == "InvalidTaskLine");
        Assert.Equal(SpecIssueSeverity.Error, issue.Severity);
    }

    [Fact]
    public void Duplicate_ids_are_errors() =>
        AssertCode("DuplicateId", Spec("- [ ] T1 cs A.cs#M x\n- [ ] T1 cs A.cs#N y\n"));

    [Fact]
    public void Too_many_tasks_are_errors()
    {
        var sb = new StringBuilder();
        for (var i = 1; i <= 21; i++) sb.Append($"- [ ] T{i} cs A.cs#M{i} x\n");
        AssertCode("TooManyTasks", Spec(sb.ToString()));
    }

    [Fact]
    public void Non_contiguous_group_is_an_error() =>
        AssertCode("GroupNotContiguous", Spec(
            "- [ ] T1 cs A.cs#M x [G1]\n- [ ] T2 cs B.cs#M x\n- [ ] T3 cs C.cs#M x [G1]\n"));

    [Fact]
    public void Sql_in_group_is_an_error() =>
        AssertCode("SqlInGroup", Spec("- [ ] T1 sql db/a.sql x [G1]\n"));

    [Fact]
    public void Unknown_ac_is_an_error() =>
        AssertCode("UnknownAc", Spec("- [ ] T1 cs A.cs#M x (AC9)\n"));

    [Fact]
    public void No_tasks_is_an_error() =>
        AssertCode("NoTasks", Spec(""));

    [Fact]
    public void Missing_front_matter_is_an_error()
    {
        var spec = ChangeSpecParser.Parse("# 제목\n\n## 작업\n- [ ] T1 cs A.cs#M x\n");

        var issue = Assert.Single(spec.Issues, i => i.Code == "FrontMatter");
        Assert.Equal(SpecIssueSeverity.Error, issue.Severity);
        Assert.Null(spec.Id);
        Assert.Single(spec.Tasks);
    }

    [Fact]
    public void Unknown_status_is_an_error() =>
        AssertCode("FrontMatter", "---\nid: x\nstatus: weird\n---\n## 작업\n- [ ] T1 cs A.cs#M x\n");

    [Fact]
    public void Reads_in_progress_and_done_status()
    {
        Assert.Equal(SpecStatus.InProgress,
            ChangeSpecParser.Parse("---\nid: x\nstatus: in-progress\n---\n").Status);
        Assert.Equal(SpecStatus.Done,
            ChangeSpecParser.Parse("---\nid: x\nstatus: done\n---\n").Status);
    }

    private static void AssertCode(string code, string text)
    {
        var issue = Assert.Single(ChangeSpecParser.Parse(text).Issues, i => i.Code == code);
        Assert.Equal(SpecIssueSeverity.Error, issue.Severity);
    }
}
