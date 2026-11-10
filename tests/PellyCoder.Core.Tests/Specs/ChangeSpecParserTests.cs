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
    public void Sql_in_group_is_allowed()
    {
        var spec = ChangeSpecParser.Parse(Spec("- [ ] T1 sql db/a.sql 요청 [G1]\n"));
        Assert.DoesNotContain(spec.Issues, i => i.Code == "SqlInGroup");
        Assert.Equal("G1", Assert.Single(spec.Tasks).Group);
    }

    [Fact]
    public void Asp_task_with_procedure_symbol()
    {
        var spec = ChangeSpecParser.Parse(Spec("- [ ] T2 asp admin/order/list.asp#Search 상태를 넘긴다 [G1]\n"));
        var t = Assert.Single(spec.Tasks);
        Assert.Equal(TaskKind.Asp, t.Kind);
        Assert.Equal("admin/order/list.asp", t.Path);
        Assert.Equal("Search", t.Symbol);
        Assert.Null(t.Lines);
        Assert.Equal("G1", t.Group);
        Assert.Empty(spec.Issues);
    }

    [Fact]
    public void Asp_task_with_class_member_symbol()
    {
        var spec = ChangeSpecParser.Parse(Spec("- [ ] T1 asp p.asp#Order.Save 요청\n"));
        Assert.Equal("Order.Save", Assert.Single(spec.Tasks).Symbol);
    }

    [Fact]
    public void Asp_task_with_lines()
    {
        var spec = ChangeSpecParser.Parse(Spec("- [ ] T1 asp p.asp#L120-148 요청\n"));
        var t = Assert.Single(spec.Tasks);
        Assert.Equal(TaskKind.Asp, t.Kind);
        Assert.Null(t.Symbol);
        Assert.Equal(new PellyCoder.Core.Models.TextSpanInfo(120, 148), t.Lines);
        Assert.Empty(spec.Issues);
    }

    [Fact]
    public void Lines_on_cs_task_is_error() =>
        AssertCode("LinesNotSupported", Spec("- [ ] T1 cs a.cs#L1-2 요청\n"));

    [Fact]
    public void Lines_reversed_is_error() =>
        AssertCode("LinesOutOfFile", Spec("- [ ] T1 asp p.asp#L20-10 요청\n"));

    [Fact]
    public void Lines_zero_is_error() =>
        AssertCode("LinesOutOfFile", Spec("- [ ] T1 asp p.asp#L0-3 요청\n"));

    [Fact]
    public void Asp_checkbox_line_is_recognized_as_task()
    {
        var spec = ChangeSpecParser.Parse(Spec("- [ ] T1 asp\n"));
        Assert.Contains(spec.Issues, i => i.Code == "InvalidTaskLine");
        var outside = ChangeSpecParser.Parse(Front + "## 목표\n- [ ] T1 asp p.asp#F 요청\n## 작업\n- [ ] T2 cs A.cs#M x\n");
        Assert.Contains(outside.Issues, i => i.Code == "InvalidTaskLine");
    }

    [Fact]
    public void Done_asp_task_with_run_comment()
    {
        var spec = ChangeSpecParser.Parse(Spec("- [x] T2 asp p.asp#F 요청 <!-- run: r1 -->\n"));
        var t = Assert.Single(spec.Tasks);
        Assert.True(t.Done);
        Assert.Equal("r1", t.RunId);
        Assert.Equal(TaskKind.Asp, t.Kind);
    }

    [Fact]
    public void KindKeyword_round_trips() =>
        Assert.Equal(["cs", "sql", "asp"],
            new[] { TaskKind.CSharp, TaskKind.Sql, TaskKind.Asp }.Select(ChangeSpecParser.KindKeyword));

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

    [Theory]
    [InlineData("## 작업 (2차)")]
    [InlineData("## 메모")]
    [InlineData("## 수용 기준")]
    public void Task_line_outside_the_tasks_section_is_an_error(string heading)
    {
        var spec = ChangeSpecParser.Parse(Spec("- [ ] T1 sql db/a.sql 첫째\n\n" + heading + "\n- [ ] T2 sql db/b.sql 둘째\n"));

        Assert.Single(spec.Tasks);
        var issue = Assert.Single(spec.Issues, i => i.Code == "InvalidTaskLine");
        Assert.Equal(SpecIssueSeverity.Error, issue.Severity);
        Assert.Contains("## 작업", issue.Message);
        Assert.Contains("밖", issue.Message);
    }

    [Fact]
    public void Task_line_under_the_first_criteria_section_is_an_error()
    {
        var spec = ChangeSpecParser.Parse(Spec("- [ ] T1 sql db/a.sql 첫째\n",
            "- AC1: 하나\n  test: Foo\n- [ ] T2 sql db/b.sql 둘째\n- AC2: 둘\n"));

        Assert.Single(spec.Tasks);
        Assert.Equal(2, spec.Criteria.Count);
        Assert.Equal("Foo", spec.Criteria[0].Test);
        var issue = Assert.Single(spec.Issues, i => i.Code == "InvalidTaskLine");
        Assert.Contains("밖", issue.Message);
    }

    [Fact]
    public void Ordinary_checkbox_outside_the_tasks_section_is_not_an_error()
    {
        var spec = ChangeSpecParser.Parse(Spec("- [ ] T1 sql db/a.sql 첫째\n\n## 확인\n- [ ] 리뷰 받기\n"));

        Assert.DoesNotContain(spec.Issues, i => i.Code == "InvalidTaskLine");
    }

    private static ChangeSpec ParseAc(string criteria) => ChangeSpecParser.Parse(Spec("- [ ] T1 cs a.cs x (AC1)\n", criteria));

    [Fact]
    public void Parse_TestAndBreaks_AnyOrder()
    {
        var spec = ParseAc("- AC1: 하나\n  breaks: A.Old\n  test: A.New\n  breaks: A.Older\n");

        Assert.Empty(spec.Issues);
        Assert.Equal("A.New", spec.Criteria[0].Test);
        Assert.Equal(["A.Old", "A.Older"], spec.Criteria[0].BreakNames);
    }

    [Fact]
    public void Parse_TestOnly_Unchanged()
    {
        var spec = ParseAc("- AC1: 하나\n  test: A.New\n- AC2: 둘\n");

        Assert.Empty(spec.Issues);
        Assert.Equal("A.New", spec.Criteria[0].Test);
        Assert.Empty(spec.Criteria[0].BreakNames);
        Assert.Null(spec.Criteria[1].Test);
    }

    [Fact]
    public void Parse_DuplicateTest_Error()
    {
        var spec = ParseAc("- AC1: 하나\n  test: A.One\n  test: A.Two\n");

        var issue = Assert.Single(spec.Issues, i => i.Code == "DuplicateTest");
        Assert.Equal(SpecIssueSeverity.Error, issue.Severity);
        Assert.Equal(10, issue.Line); // front 6 lines, heading 7, AC1 8, first test 9, second test 10
        Assert.Equal("수용 기준 AC1에 test:가 둘 이상입니다.", issue.Message);
        Assert.Equal("A.One", spec.Criteria[0].Test);
    }

    [Fact]
    public void Parse_InvalidTestName_Error()
    {
        var spec = ParseAc("- AC1: 하나\n  test: A.M(1)\n");

        var issue = Assert.Single(spec.Issues, i => i.Code == "InvalidTestName");
        Assert.Equal(SpecIssueSeverity.Error, issue.Severity);
        Assert.Equal("수용 기준 AC1의 테스트 이름 형식이 아닙니다: A.M(1)", issue.Message);
    }

    [Fact]
    public void Parse_InvalidBreaksName_Error()
    {
        var spec = ParseAc("- AC1: 하나\n  breaks: A M\n");

        Assert.Single(spec.Issues, i => i.Code == "InvalidTestName");
    }

    [Fact]
    public void Parse_BreaksSameAsTest_Error()
    {
        var spec = ParseAc("- AC1: 하나\n  breaks: A.New\n  test: A.New\n");

        var issue = Assert.Single(spec.Issues, i => i.Code == "BreaksSameAsTest");
        Assert.Equal("수용 기준 AC1: 같은 테스트를 test:와 breaks:에 함께 둘 수 없습니다: A.New", issue.Message);
    }

    [Fact]
    public void Parse_DuplicateBreaks_KeptOnce()
    {
        var spec = ParseAc("- AC1: 하나\n  breaks: A.Old\n  breaks: A.Old\n");

        Assert.Empty(spec.Issues);
        Assert.Equal(["A.Old"], spec.Criteria[0].BreakNames);
    }

    [Fact]
    public void Parse_UnindentedLineEndsCriterion()
    {
        var spec = ParseAc("- AC1: 하나\n  test: A.New\nbreaks: X\n  breaks: Y\n");

        Assert.Empty(spec.Issues);
        Assert.Equal("A.New", spec.Criteria[0].Test);
        Assert.Empty(spec.Criteria[0].BreakNames);
    }

    [Fact]
    public void Js_FunctionTarget()
    {
        var spec = ChangeSpecParser.Parse(Spec("- [ ] T1 js js/order.js#fnSearch 페이지 번호를 더한다\n"));
        var t = Assert.Single(spec.Tasks);
        Assert.Equal(TaskKind.Js, t.Kind);
        Assert.Equal("js/order.js", t.Path);
        Assert.Equal("fnSearch", t.Symbol);
        Assert.Null(t.Lines);
        Assert.Empty(spec.Issues);
    }

    [Fact]
    public void Js_QualifiedTarget() =>
        Assert.Equal("Common.fnX", Assert.Single(ChangeSpecParser.Parse(Spec("- [ ] T1 js js/common.js#Common.fnX 요청\n")).Tasks).Symbol);

    [Fact]
    public void Js_LinesTarget_OnPage()
    {
        var spec = ChangeSpecParser.Parse(Spec("- [ ] T2 js admin/list.asp#L10-20 요청\n"));
        var t = Assert.Single(spec.Tasks);
        Assert.Equal(TaskKind.Js, t.Kind);
        Assert.Null(t.Symbol);
        Assert.Equal(new PellyCoder.Core.Models.TextSpanInfo(10, 20), t.Lines);
        Assert.Empty(spec.Issues);
    }

    [Fact]
    public void Css_SelectorTargets()
    {
        var cls = Assert.Single(ChangeSpecParser.Parse(Spec("- [ ] T4 css css/admin.css#.search-box 넓힌다\n")).Tasks);
        Assert.Equal(TaskKind.Css, cls.Kind);
        Assert.Equal(".search-box", cls.Symbol);
        Assert.Equal("css/admin.css", cls.Path);
        var id = Assert.Single(ChangeSpecParser.Parse(Spec("- [ ] T4 css css/admin.css##main 넓힌다\n")).Tasks);
        Assert.Equal("#main", id.Symbol);
    }

    [Fact]
    public void Css_IsNotCs()
    {
        Assert.Equal(TaskKind.Css, Assert.Single(ChangeSpecParser.Parse(Spec("- [ ] T4 css css/a.css#.x 요청\n")).Tasks).Kind);
        Assert.Equal(TaskKind.CSharp, Assert.Single(ChangeSpecParser.Parse(Spec("- [ ] T4 cs src/A.cs#M 요청\n")).Tasks).Kind);
    }

    [Fact]
    public void Lines_StillRejectedForCsAndSql()
    {
        var issue = Assert.Single(ChangeSpecParser.Parse(Spec("- [ ] T1 sql db/a.sql#L1-2 요청\n")).Issues, i => i.Code == "LinesNotSupported");
        Assert.Equal("작업 T1: #L 줄 범위는 asp·js·css 작업에만 쓸 수 있습니다.", issue.Message);
        AssertCode("LinesNotSupported", Spec("- [ ] T1 cs a.cs#L1-2 요청\n"));
    }

    [Fact]
    public void Lines_AcceptedForCss() =>
        Assert.Empty(ChangeSpecParser.Parse(Spec("- [ ] T5 css admin/list.asp#L12-30 요청\n")).Issues);

    [Fact]
    public void GroupAndDoneMarks_WithJs()
    {
        var t = Assert.Single(ChangeSpecParser.Parse(Spec("- [x] T1 js a.js#f 요청 [G1] <!-- run: r1 -->\n")).Tasks);
        Assert.True(t.Done);
        Assert.Equal("G1", t.Group);
        Assert.Equal("r1", t.RunId);
    }

    [Fact]
    public void SyntaxError_MentionsNewKinds()
    {
        var issue = Assert.Single(ChangeSpecParser.Parse(Spec("- [ ] T1 js\n")).Issues, i => i.Code == "InvalidTaskLine");
        Assert.Contains("<cs|sql|asp|js|css>", issue.Message);
    }

    [Fact]
    public void KindKeyword_JsCss()
    {
        Assert.Equal("js", ChangeSpecParser.KindKeyword(TaskKind.Js));
        Assert.Equal("css", ChangeSpecParser.KindKeyword(TaskKind.Css));
    }
}
